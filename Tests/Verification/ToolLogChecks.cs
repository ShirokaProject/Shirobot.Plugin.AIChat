using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.AiChatPlugin.Tools;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Plugin;

internal static class ToolLogChecks
{
    public static async Task RunAsync()
    {
        var logger = new CaptureLogger();
        using var scope = BotLog.BeginScope(logger);
        const string secret = "secret-api-key-do-not-log";
        using var tool = new CallbackTool(() => throw new InvalidOperationException(secret));
        var result = await ChatToolRegistry.ExecuteLoggedAsync(tool, secret, "tool=\"test\"", default);
        if (!result.Contains("failed") || !logger.Lines.Any(l => l.Contains("ToolUse 失败")) || logger.Lines.Any(l => l.Contains(secret)))
            throw new Exception("Failed tool calls must be logged without leaking arguments or exception secrets.");
        using var cancellation = new CancellationTokenSource();
        using var canceled = new CallbackTool(() => { cancellation.Cancel(); return Task.FromResult(secret); });
        try
        {
            await ChatToolRegistry.ExecuteLoggedAsync(canceled, secret, "tool=\"test\"", cancellation.Token);
            throw new Exception("Expected cancellation.");
        }
        catch (OperationCanceledException) { }
        if (!logger.Lines.Any(l => l.Contains("ToolUse 取消")) || logger.Lines.Any(l => l.Contains(secret)))
            throw new Exception("Canceled calls must be logged and cancellation propagated without leaking tool output.");
        var handler = new StubHandler(_ => "{\"choices\":[{\"message\":{\"content\":\"\",\"reasoning_content\":\"private reasoning\",\"tool_calls\":[{\"id\":\"call1\",\"type\":\"function\",\"function\":{\"name\":\"first\",\"arguments\":\"{}\"}}]}}]}");
        handler.Respond = _ => handler.Requests.Count <= 2 ? handler.RespondInitial : "{\"choices\":[{\"message\":{\"content\":\"partial answer with stated limits\"}}]}";
        using var client = new OpenAiClient(10, "");
        var clients = (Dictionary<string, HttpClient>)typeof(OpenAiClient).GetField("_clients", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(client)!;
        clients["https://example.invalid/v1"] = new HttpClient(handler);
        using var registry = new ChatToolRegistry();
        var first = new FakeTool("first"); registry.Register(first);
        var finalAnswer = await registry.ChatAsync(client, new ShiroBot.AiChatPlugin.Config.ModelEntry { Name = "test", BaseUrl = "https://example.invalid/v1", ApiKey = "test" }, [], 1);
        if (first.Calls != 1 || handler.Requests.Count != 3 || finalAnswer != "partial answer with stated limits")
            throw new Exception("Tool-round exhaustion must close rejected calls and request one bounded final answer without executing more tools.");
        Console.WriteLine("PASS: tool failure/cancellation logging and secret argument/result/exception protection");
    }
    sealed class CallbackTool(Func<Task<string>> execute) : IChatTool
    {
        public OpenAiTool Definition { get; } = new() { Function = new() { Name = "test", Description = "test", Parameters = new { type = "object" } } };
        public string SystemPrompt => "";
        public int MaxCallsPerTurn => 1;
        public Task<string> ExecuteAsync(string arguments, CancellationToken ct = default) => execute();
        public void Dispose() { }
    }
    internal sealed class CaptureLogger : IConsoleLogger
    {
        public List<string> Lines { get; } = [];
        public bool IsEnabled { get; set; } = true;
        public void Log(string message) => Lines.Add(message);
        public void Info(string message) => Log(message);
        public void Success(string message) => Log(message);
        public void Warning(string message) => Log(message);
        public void Error(string message) => Log(message);
    }
}
