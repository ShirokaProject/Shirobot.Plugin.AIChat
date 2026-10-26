using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.SDK.Abstractions;

namespace ShiroBot.AiChatPlugin.Mcp;

/// <summary>JSON-RPC 消息通道：请求返回对应 id 的响应消息（含 result 或 error）。</summary>
internal interface IMcpTransport : IAsyncDisposable
{
    bool IsAlive { get; }
    Task<JsonObject> SendRequestAsync(JsonObject request, CancellationToken ct);
    Task SendNotificationAsync(JsonObject notification, CancellationToken ct);
    /// <summary>initialize 成功后告知协商的协议版本（HTTP 需要放进请求头）。</summary>
    void SetProtocolVersion(string version) { }
}

internal sealed class McpException(string message) : Exception(message);

internal static class McpTransport
{
    public static IMcpTransport Create(McpServerEntry server) =>
        !string.IsNullOrWhiteSpace(server.Url) ? new HttpMcpTransport(server) : new StdioMcpTransport(server);

    internal static string RequestId(JsonObject request) => request["id"]?.ToJsonString() ?? "";
}

/// <summary>stdio：启动本机进程，按行收发 JSON-RPC 消息。</summary>
internal sealed class StdioMcpTransport : IMcpTransport
{
    private readonly string _name;
    private readonly Process _process;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly Task _reader;
    private readonly Task _stderr;
    /// <summary>stdout 已结束；进程可能还没被系统回收，HasExited 仍为 false。</summary>
    private volatile bool _closed;

    public StdioMcpTransport(McpServerEntry server)
    {
        _name = server.Name;
        if (string.IsNullOrWhiteSpace(server.Command)) throw new McpException("未配置 command 或 url。");
        var start = new ProcessStartInfo(server.Command.Trim())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false)
        };
        foreach (var arg in server.Args) start.ArgumentList.Add(arg);
        if (!string.IsNullOrWhiteSpace(server.WorkingDirectory)) start.WorkingDirectory = server.WorkingDirectory;
        foreach (var pair in server.Env)
        {
            var index = pair.IndexOf('=');
            if (index > 0) start.Environment[pair[..index].Trim()] = pair[(index + 1)..];
        }
        _process = Process.Start(start) ?? throw new McpException($"无法启动 {server.Command}。");
        _reader = Task.Run(ReadLoopAsync);
        _stderr = Task.Run(DrainStderrAsync);
    }

    public bool IsAlive
    {
        get
        {
            try { return !_closed && !_process.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }

    public async Task<JsonObject> SendRequestAsync(JsonObject request, CancellationToken ct)
    {
        var id = McpTransport.RequestId(request);
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await WriteAsync(request, ct).ConfigureAwait(false);
            return await completion.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public Task SendNotificationAsync(JsonObject notification, CancellationToken ct) => WriteAsync(notification, ct);

    private async Task WriteAsync(JsonObject message, CancellationToken ct)
    {
        if (!IsAlive) throw new McpException("MCP 进程已退出。");
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 消息之间以换行分隔，ToJsonString 不会输出原始换行。
            await _process.StandardInput.WriteLineAsync(message.ToJsonString().AsMemory(), ct).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            _closed = true;
            throw new McpException("MCP 进程已退出。");
        }
        finally { _write.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonObject? message;
                try { message = JsonNode.Parse(line) as JsonObject; }
                catch (Exception) { continue; }
                if (message is null) continue;
                if (message.ContainsKey("method"))
                {
                    // 服务端发来的请求：只支持 ping，其余回复“方法不存在”；通知忽略。
                    var isPing = message["method"] is JsonValue method && method.TryGetValue<string>(out var name) && name == "ping";
                    if (message["id"] is { } id)
                        await WriteAsync(isPing
                            ? new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = new JsonObject() }
                            : new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(),
                                ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" } }, CancellationToken.None).ConfigureAwait(false);
                    continue;
                }
                if (_pending.TryRemove(McpTransport.RequestId(message), out var completion)) completion.TrySetResult(message);
            }
        }
        catch (Exception) { /* 进程退出或管道关闭 */ }
        _closed = true;
        foreach (var pair in _pending)
            if (_pending.TryRemove(pair.Key, out var completion)) completion.TrySetException(new McpException("MCP 进程已退出。"));
    }

    private async Task DrainStderrAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                if (!string.IsNullOrWhiteSpace(line))
                    BotLog.Info($"[AiChat][MCP:{_name}] {(line.Length > 300 ? line[..300] + "…" : line)}");
        }
        catch (Exception) { /* 进程退出 */ }
    }

    public async ValueTask DisposeAsync()
    {
        try { _process.StandardInput.Close(); } catch (Exception) { }
        try
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            try { _process.Kill(entireProcessTree: true); } catch (Exception) { }
        }
        try { await Task.WhenAll(_reader, _stderr).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (Exception) { }
        _process.Dispose();
        _write.Dispose();
    }
}

/// <summary>Streamable HTTP：每条消息一次 POST，响应可以是 JSON 或 SSE 流。</summary>
internal sealed class HttpMcpTransport : IMcpTransport
{
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly Uri _url;
    private readonly List<(string Name, string Value)> _headers = [];
    private string? _sessionId;
    private string? _protocolVersion;

    public HttpMcpTransport(McpServerEntry server)
    {
        if (!Uri.TryCreate(server.Url.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            throw new McpException("url 必须是 http 或 https 地址。");
        _url = url;
        foreach (var header in server.Headers)
        {
            var index = header.IndexOf(':');
            if (index > 0) _headers.Add((header[..index].Trim(), header[(index + 1)..].Trim()));
        }
    }

    public bool IsAlive => true;

    public void SetProtocolVersion(string version) => _protocolVersion = version;

    public async Task<JsonObject> SendRequestAsync(JsonObject request, CancellationToken ct)
    {
        using var response = await PostAsync(request, ct).ConfigureAwait(false);
        var id = McpTransport.RequestId(request);
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await foreach (var message in ReadEventsAsync(stream, ct).ConfigureAwait(false))
                if (!message.ContainsKey("method") && McpTransport.RequestId(message) == id) return message;
            throw new McpException("SSE 流结束前没有收到响应。");
        }
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonNode.Parse(body) as JsonObject ?? throw new McpException("响应不是 JSON 对象。");
    }

    public async Task SendNotificationAsync(JsonObject notification, CancellationToken ct)
    {
        using var _ = await PostAsync(notification, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> PostAsync(JsonObject message, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (_sessionId is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        if (_protocolVersion is not null) request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _protocolVersion);
        foreach (var (name, value) in _headers) request.Headers.TryAddWithoutValidation(name, value);
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new McpException($"HTTP {status}");
        }
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var session)) _sessionId = session.FirstOrDefault() ?? _sessionId;
        return response;
    }

    internal static async IAsyncEnumerable<JsonObject> ReadEventsAsync(Stream stream, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null || line.Length == 0)
            {
                if (data.Length > 0)
                {
                    JsonObject? message = null;
                    try { message = JsonNode.Parse(data.ToString()) as JsonObject; }
                    catch (Exception) { /* 跳过无法解析的事件 */ }
                    data.Clear();
                    if (message is not null) yield return message;
                }
                if (line is null) yield break;
                continue;
            }
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.AsSpan(5).TrimStart(' '));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        // 告诉服务端结束会话；失败不影响卸载。
        if (_sessionId is not null)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Delete, _url);
                request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var _ = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception) { }
        }
        _http.Dispose();
    }
}
