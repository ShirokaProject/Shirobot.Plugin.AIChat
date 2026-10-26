using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.SDK.Abstractions;

namespace ShiroBot.AiChatPlugin.Mcp;

internal sealed record McpToolInfo(string Name, string Description, JsonObject InputSchema);

/// <summary>一个 MCP 服务的会话：握手、列出工具、调用工具。</summary>
internal sealed class McpServerConnection : IAsyncDisposable
{
    internal const string ProtocolVersion = "2025-06-18";
    private readonly IMcpTransport _transport;
    private long _nextId;

    private McpServerConnection(IMcpTransport transport) => _transport = transport;

    public IReadOnlyList<McpToolInfo> Tools { get; private set; } = [];
    public bool IsAlive => _transport.IsAlive;

    public static async Task<McpServerConnection> ConnectAsync(IMcpTransport transport, string clientVersion, TimeSpan timeout, CancellationToken ct)
    {
        var connection = new McpServerConnection(transport);
        try
        {
            var init = await connection.RequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "ShiroBot.AiChatPlugin", ["version"] = clientVersion }
            }, timeout, ct).ConfigureAwait(false);
            if (init?["protocolVersion"] is JsonValue version && version.TryGetValue<string>(out var negotiated))
                transport.SetProtocolVersion(negotiated);
            await transport.SendNotificationAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, ct).ConfigureAwait(false);

            var tools = new List<McpToolInfo>();
            string? cursor = null;
            do
            {
                var page = await connection.RequestAsync("tools/list",
                    cursor is null ? new JsonObject() : new JsonObject { ["cursor"] = cursor }, timeout, ct).ConfigureAwait(false);
                foreach (var item in page?["tools"] as JsonArray ?? [])
                {
                    if (item is not JsonObject tool || tool["name"] is not JsonValue nameValue || !nameValue.TryGetValue<string>(out var name)) continue;
                    var description = tool["description"] is JsonValue d && d.TryGetValue<string>(out var text) ? text : "";
                    var schema = tool["inputSchema"] as JsonObject ?? new JsonObject();
                    tools.Add(new McpToolInfo(name, description, (JsonObject)schema.DeepClone()));
                }
                cursor = page?["nextCursor"] is JsonValue next && next.TryGetValue<string>(out var c) && c.Length > 0 ? c : null;
            } while (cursor is not null && tools.Count < 500);
            connection.Tools = tools;
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<string> CallToolAsync(string name, JsonObject arguments, TimeSpan timeout, int maxChars, CancellationToken ct)
    {
        var result = await RequestAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments }, timeout, ct).ConfigureAwait(false);
        return FormatResult(result, maxChars);
    }

    /// <summary>把 tools/call 的结果转成给模型看的文字：文本原样保留，图片等资源只保留类型。</summary>
    internal static string FormatResult(JsonNode? result, int maxChars)
    {
        var sb = new StringBuilder();
        foreach (var item in result?["content"] as JsonArray ?? [])
        {
            if (item is not JsonObject part) continue;
            var type = Str(part["type"]);
            var text = type switch
            {
                "text" => Str(part["text"]),
                "image" => $"[image {Str(part["mimeType"])}]",
                "audio" => $"[audio {Str(part["mimeType"])}]",
                "resource" => Str(part["resource"]?["text"]) is { Length: > 0 } body ? body : $"[resource {Str(part["resource"]?["uri"])}]",
                "resource_link" => $"[resource {Str(part["name"])} {Str(part["uri"])}]",
                _ => ""
            };
            if (text.Length == 0) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(text);
        }
        if (sb.Length == 0 && result?["structuredContent"] is { } structured) sb.Append(structured.ToJsonString());
        if (sb.Length == 0) sb.Append("(empty result)");
        var output = sb.ToString();
        if (result?["isError"] is JsonValue error && error.TryGetValue<bool>(out var isError) && isError) output = "MCP tool error: " + output;
        maxChars = Math.Max(100, maxChars);
        return output.Length <= maxChars ? output : output[..maxChars] + $"\n…(truncated, {output.Length} characters in total)";
    }

    private static string Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private async Task<JsonNode?> RequestAsync(string method, JsonObject parameters, TimeSpan timeout, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        JsonObject response;
        try
        {
            response = await _transport.SendRequestAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters
            }, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new McpException($"{method} 超时（{timeout.TotalSeconds:0} 秒）。");
        }
        if (response["error"] is JsonObject error)
            throw new McpException($"{method} 失败: {Str(error["message"])}");
        return response["result"];
    }

    public ValueTask DisposeAsync() => _transport.DisposeAsync();
}

/// <summary>
/// 管理配置中的全部 MCP 服务：后台连接、汇总工具、调用时按需重连。
/// 工具名统一为 mcp_&lt;服务&gt;_&lt;工具&gt;，满足 OpenAI 函数名限制。
/// </summary>
internal sealed class McpHub : IAsyncDisposable
{
    private sealed class ServerState(McpServerEntry entry)
    {
        public readonly McpServerEntry Entry = entry;
        public readonly SemaphoreSlim Gate = new(1, 1);
        public McpServerConnection? Connection;
        public string Status = "连接中";
    }

    private readonly McpSection _config;
    private readonly string _clientVersion;
    private readonly Func<McpServerEntry, IMcpTransport> _createTransport;
    private readonly List<ServerState> _servers;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _startup;

    public McpHub(McpSection config, string clientVersion, Func<McpServerEntry, IMcpTransport>? createTransport = null)
    {
        _config = config;
        _clientVersion = clientVersion;
        _createTransport = createTransport ?? McpTransport.Create;
        _servers = config.Servers.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Name)).Select(s => new ServerState(s)).ToList();
        _startup = Task.WhenAll(_servers.Select(s => Task.Run(() => EnsureConnectedAsync(s, _stop.Token))));
    }

    /// <summary>等待首次连接完成（测试和 #mcp 命令使用）。</summary>
    public Task Startup => _startup;

    private TimeSpan Timeout => TimeSpan.FromSeconds(Math.Clamp(_config.TimeoutSeconds, 5, 600));

    private async Task<McpServerConnection?> EnsureConnectedAsync(ServerState server, CancellationToken ct)
    {
        await server.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (server.Connection is { IsAlive: true } alive) return alive;
            if (server.Connection is not null) await server.Connection.DisposeAsync().ConfigureAwait(false);
            server.Connection = null;
            try
            {
                server.Connection = await McpServerConnection.ConnectAsync(_createTransport(server.Entry), _clientVersion, Timeout, ct).ConfigureAwait(false);
                server.Status = $"已连接，{server.Connection.Tools.Count} 个工具";
                BotLog.Info($"[AiChat] MCP 服务 {server.Entry.Name} 已连接，工具: {string.Join(", ", server.Connection.Tools.Select(t => t.Name))}");
                return server.Connection;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                server.Status = "连接失败: " + ex.Message;
                BotLog.Warning($"[AiChat] MCP 服务 {server.Entry.Name} 连接失败: {ex.Message}");
                return null;
            }
        }
        finally { server.Gate.Release(); }
    }

    /// <summary>当前可用的工具；还没连上或连接失败的服务不提供工具。</summary>
    public IReadOnlyList<(string ToolName, string Server, McpToolInfo Tool)> Tools
    {
        get
        {
            var result = new List<(string, string, McpToolInfo)>();
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var server in _servers)
            {
                if (server.Connection is not { } connection) continue;
                var allowed = server.Entry.AllowedTools.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToHashSet(StringComparer.Ordinal);
                foreach (var tool in connection.Tools)
                {
                    if (allowed.Count > 0 && !allowed.Contains(tool.Name)) continue;
                    var name = ToolName(server.Entry.Name, tool.Name);
                    if (used.Add(name)) result.Add((name, server.Entry.Name, tool));
                }
            }
            return result;
        }
    }

    public async Task<string> CallAsync(string serverName, string toolName, JsonObject arguments, CancellationToken ct)
    {
        var server = _servers.FirstOrDefault(s => s.Entry.Name == serverName) ?? throw new McpException($"MCP 服务 {serverName} 不存在。");
        // 进程意外退出时在调用前重连一次。
        var connection = await EnsureConnectedAsync(server, ct).ConfigureAwait(false) ?? throw new McpException(server.Status);
        return await connection.CallToolAsync(toolName, arguments, Timeout, Math.Clamp(_config.MaxResultChars, 500, 100_000), ct).ConfigureAwait(false);
    }

    public string Describe() =>
        _servers.Count == 0 ? "没有启用的 MCP 服务。" :
        string.Join("\n", _servers.Select(s => $"- {s.Entry.Name}（{(string.IsNullOrWhiteSpace(s.Entry.Url) ? "stdio" : "http")}）：{s.Status}" +
            (s.Connection is { } c ? "\n  " + string.Join(", ", c.Tools.Select(t => t.Name)) : "")));

    internal static string ToolName(string server, string tool)
    {
        static string Clean(string text) => Regex.Replace(text.Trim(), "[^A-Za-z0-9_-]", "_");
        var name = $"mcp_{Clean(server)}_{Clean(tool)}";
        if (name.Length <= 64) return name;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(server + "\0" + tool)))[..8].ToLowerInvariant();
        return name[..55] + "_" + hash;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try { await _startup.ConfigureAwait(false); } catch (Exception) { }
        foreach (var server in _servers)
        {
            await server.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (server.Connection is not null) await server.Connection.DisposeAsync().ConfigureAwait(false);
                server.Connection = null;
            }
            finally { server.Gate.Release(); }
            server.Gate.Dispose();
        }
        _stop.Dispose();
    }
}
