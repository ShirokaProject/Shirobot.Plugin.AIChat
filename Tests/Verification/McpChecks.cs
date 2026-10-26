using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using ShiroBot.AiChatPlugin;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.Mcp;
using ShiroBot.AiChatPlugin.Proactive;
using ShiroBot.AiChatPlugin.Tools;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

internal static class McpChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    // 假的 stdio MCP 服务：先向客户端发 ping，工具列表分两页，exit 工具会让进程退出。
    private const string FakeServer = """
        import sys, json
        pong = False
        def send(obj):
            print(json.dumps(obj), flush=True)
        print("fake server started", file=sys.stderr, flush=True)
        for line in sys.stdin:
            msg = json.loads(line)
            if "method" not in msg:
                pong = pong or msg.get("id") == "srv-1"
                continue
            if "id" not in msg:
                continue
            m, i = msg["method"], msg["id"]
            if m == "initialize":
                send({"jsonrpc": "2.0", "id": "srv-1", "method": "ping"})
                res = {"protocolVersion": "2025-06-18", "capabilities": {"tools": {}}, "serverInfo": {"name": "fake", "version": "1"}}
            elif m == "tools/list":
                if not msg.get("params", {}).get("cursor"):
                    res = {"tools": [{"name": "echo", "description": "Echo text", "inputSchema": {"$schema": "x", "type": "object", "properties": {"text": {"type": "string"}}}}], "nextCursor": "p2"}
                else:
                    res = {"tools": [{"name": "fail", "description": "Always fails", "inputSchema": {"type": "object"}}, {"name": "exit", "inputSchema": {}}]}
            elif m == "tools/call":
                name, args = msg["params"]["name"], msg["params"].get("arguments", {})
                if name == "echo":
                    res = {"content": [{"type": "text", "text": "echo:" + args.get("text", "") + " pong=" + str(pong)}, {"type": "image", "mimeType": "image/png", "data": "AA=="}]}
                elif name == "fail":
                    res = {"content": [{"type": "text", "text": "boom"}], "isError": True}
                else:
                    sys.exit(0)
            else:
                send({"jsonrpc": "2.0", "id": i, "error": {"code": -32601, "message": "nope"}})
                continue
            send({"jsonrpc": "2.0", "id": i, "result": res})
        """;

    public static async Task RunAsync()
    {
        Check(McpHub.ToolName("my fs", "read.file") == "mcp_my_fs_read_file", "Tool names must be sanitized.");
        var longName = McpHub.ToolName("server", new string('x', 100));
        Check(longName.Length == 64 && longName.StartsWith("mcp_server_") && longName != McpHub.ToolName("server", new string('x', 99)),
            "Long tool names must be truncated with a distinguishing hash.");
        var schema = McpChatTool.Schema(new JsonObject { ["$schema"] = "x", ["type"] = "string" });
        Check(!schema.ContainsKey("$schema") && schema["type"]!.GetValue<string>() == "object" && schema["properties"] is JsonObject,
            "Schemas must be objects without $schema.");
        Check(McpServerConnection.FormatResult(JsonNode.Parse("""{"content":[{"type":"text","text":"abcdefghij"}]}"""), 100) == "abcdefghij" &&
            McpServerConnection.FormatResult(JsonNode.Parse("""{"content":[{"type":"text","text":"%s"}]}""".Replace("%s", new string('a', 300))), 100).StartsWith(new string('a', 100) + "\n…(truncated, 300"),
            "Results must be truncated to the configured size.");
        Check(McpServerConnection.FormatResult(JsonNode.Parse("""{"content":[],"structuredContent":{"a":1}}"""), 1000) == """{"a":1}""",
            "Structured content is used when there is no text content.");
        Console.WriteLine("PASS: MCP tool naming, schema cleanup and result formatting");

        var root = Path.Combine(Path.GetTempPath(), "aichat-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var script = Path.Combine(root, "server.py");
            await File.WriteAllTextAsync(script, FakeServer);
            var section = new McpSection
            {
                Enabled = true, TimeoutSeconds = 10,
                Servers = [
                    new McpServerEntry { Name = "fake", Command = "python3", Args = [script] },
                    new McpServerEntry { Name = "only-echo", Command = "python3", Args = [script], AllowedTools = ["echo"] },
                    new McpServerEntry { Name = "broken", Command = "/nonexistent/mcp-server" },
                    new McpServerEntry { Name = "off", Enabled = false, Command = "python3", Args = [script] }
                ]
            };
            await using (var hub = new McpHub(section, "1.0.0"))
            {
                await hub.Startup;
                var tools = hub.Tools;
                Check(tools.Select(t => t.ToolName).SequenceEqual(["mcp_fake_echo", "mcp_fake_fail", "mcp_fake_exit", "mcp_only-echo_echo"]),
                    "Tools must be listed across pages, filtered by allowed_tools, and disabled or broken servers skipped.");
                Check(hub.Describe().Contains("broken") && hub.Describe().Contains("连接失败") && !hub.Describe().Contains("off"),
                    "Status must show failed servers and omit disabled ones.");

                IChatTool Tool(string name)
                {
                    var t = tools.Single(x => x.ToolName == name);
                    return new McpChatTool(hub, t.ToolName, t.Server, t.Tool, 3);
                }
                var echo = Tool("mcp_fake_echo");
                var parameters = (JsonObject)echo.Definition.Function.Parameters;
                Check(!parameters.ContainsKey("$schema") && echo.Definition.Function.Description.StartsWith("[MCP fake] Echo"),
                    "Definitions must use the cleaned schema and mark the server.");
                Check(await echo.ExecuteAsync("""{"text":"你好"}""") == "echo:你好 pong=True\n[image image/png]",
                    "Calls must return text, describe images and answer server pings.");
                Check((await Tool("mcp_fake_fail").ExecuteAsync("{}")).StartsWith("MCP tool error: boom"), "Tool errors must be marked.");
                Check((await echo.ExecuteAsync("[1]")).Contains("JSON object"), "Non-object arguments must be rejected.");
                Check((await Tool("mcp_fake_exit").ExecuteAsync("")).StartsWith("MCP call failed"), "A server crash must fail the call cleanly.");
                Check((await echo.ExecuteAsync("""{"text":"again"}""")).StartsWith("echo:again"), "A crashed stdio server must be restarted on the next call.");
            }
            Console.WriteLine("PASS: MCP stdio handshake, paging, ping, calls, errors, crash recovery and filters");

            await RunHttpAsync();
            Console.WriteLine("PASS: MCP streamable HTTP with SSE responses, session and protocol headers");

            // 默认只对所有者/管理员提供；主动插话不提供。
            var plugin = new AiChatPlugin();
            var config = new PluginConfig();
            config.ToolUse.Mcp = new McpSection { Enabled = true, Servers = [new McpServerEntry { Name = "fake", Command = "python3", Args = [script] }] };
            typeof(AiChatPlugin).GetField("_config", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(plugin, config);
            await using var gated = new McpHub(config.ToolUse.Mcp, "1.0.0");
            await gated.Startup;
            typeof(AiChatPlugin).GetField("_mcp", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(plugin, gated);
            var admin = false;
            var ctx = DispatchProxy.Create<IBotContext, TestProxy>();
            ((TestProxy)(object)ctx).Call = (m, _) => m.Name == "IsAdmin" ? admin : throw new NotSupportedException(m.Name);
            typeof(PluginBase).GetProperty("Context", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(plugin, ctx);
            var toolsFor = typeof(AiChatPlugin).GetMethod("McpToolsFor", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var message = new MessageEvent { Platform = "qq", InstanceId = "i", SelfId = "bot", MessageId = "m", Channel = Channel.Group("g"), Sender = new User("u"), Segments = [] };
            int Count(ProactiveRequest? proactive = null) => ((List<IChatTool>)toolsFor.Invoke(plugin, [message, proactive])!).Count;
            Check(Count() == 0, "MCP tools must not be offered to ordinary users by default.");
            admin = true;
            Check(Count() == 3, "Admins must get MCP tools.");
            Check(Count(new ProactiveRequest("i:g", "")) == 0, "Proactive replies must never get MCP tools.");
            admin = false;
            config.ToolUse.Mcp.AllowEveryone = true;
            Check(Count() == 3, "allow_everyone must offer MCP tools to everyone.");
            config.ToolUse.Enabled = false;
            Check(Count() == 0, "Disabling tool use must hide MCP tools.");
            Check(!new PluginConfig().ToolUse.Mcp.Enabled && !new PluginConfig().ToolUse.Mcp.AllowEveryone, "MCP must be off and admin-only by default.");
            Console.WriteLine("PASS: MCP off by default, admin-only unless allow_everyone, never for proactive replies");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    private static async Task RunHttpAsync()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/mcp/");
        listener.Start();
        var seenHeaders = new List<string>();
        var server = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); }
                catch (Exception) { return; }
                var request = context.Request;
                var response = context.Response;
                if (request.HttpMethod == "DELETE") { response.StatusCode = 200; response.Close(); continue; }
                var body = JsonNode.Parse(await new StreamReader(request.InputStream).ReadToEndAsync())!.AsObject();
                var method = body["method"]!.GetValue<string>();
                seenHeaders.Add($"{method}|{request.Headers["Mcp-Session-Id"]}|{request.Headers["MCP-Protocol-Version"]}|{request.Headers["Authorization"]}");
                if (body["id"] is null) { response.StatusCode = 202; response.Close(); continue; }
                var id = body["id"]!.DeepClone();
                JsonObject result = method switch
                {
                    "initialize" => new JsonObject { ["protocolVersion"] = "2025-03-26", ["capabilities"] = new JsonObject() },
                    "tools/list" => JsonNode.Parse("""{"tools":[{"name":"time","description":"Now","inputSchema":{"type":"object"}}]}""")!.AsObject(),
                    _ => JsonNode.Parse("""{"content":[{"type":"text","text":"12:00"}]}""")!.AsObject()
                };
                var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString();
                if (method == "initialize") response.Headers["Mcp-Session-Id"] = "s-1";
                byte[] bytes;
                if (method == "tools/call")
                {
                    // SSE：先发一条通知，再发响应。
                    response.ContentType = "text/event-stream";
                    bytes = Encoding.UTF8.GetBytes("event: message\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\"}\n\nevent: message\ndata: " + reply + "\n\n");
                }
                else
                {
                    response.ContentType = "application/json";
                    bytes = Encoding.UTF8.GetBytes(reply);
                }
                await response.OutputStream.WriteAsync(bytes);
                response.Close();
            }
        });
        var section = new McpSection { Enabled = true, TimeoutSeconds = 10,
            Servers = [new McpServerEntry { Name = "web", Url = $"http://127.0.0.1:{port}/mcp/", Headers = ["Authorization: Bearer t0k"] }] };
        await using (var hub = new McpHub(section, "1.0.0"))
        {
            await hub.Startup;
            var tool = hub.Tools.Single();
            Check(tool.ToolName == "mcp_web_time", "HTTP tools must be listed.");
            Check(await new McpChatTool(hub, tool.ToolName, tool.Server, tool.Tool, 3).ExecuteAsync("{}") == "12:00", "SSE responses must be matched by id.");
        }
        listener.Stop();
        await server;
        Check(seenHeaders[0] == "initialize|||Bearer t0k", "initialize must carry configured headers only.");
        Check(seenHeaders.Skip(1).All(h => h.EndsWith("|s-1|2025-03-26|Bearer t0k")),
            "Later requests must carry the session id and negotiated protocol version.");
    }
}
