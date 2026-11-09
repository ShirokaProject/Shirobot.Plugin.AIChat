using System.Reflection;
using System.Text.Json;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.AiChatPlugin.Tools;
using Tomlyn;

internal static class SkillsConcurrencyChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static OpenAiToolCall Call(string id, string name, string args = "{}") => new() { Id = id, Function = new() { Name = name, Arguments = args } };
    public static async Task RunAsync()
    {
        var template = (string)typeof(ShiroBot.AiChatPlugin.AiChatPlugin).GetField("DefaultConfigTemplate", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
        var templateConfig = TomlSerializer.Deserialize<PluginConfig>(template, new TomlSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })!;
        Check(templateConfig.ToolUse.MaxParallelCalls == 4 && templateConfig.Skills.Enabled && templateConfig.Skills.Entries.Count == 0,
            "Default template must enable the empty skill catalog and configure bounded concurrency.");
        var config = TomlSerializer.Deserialize<PluginConfig>("""
            [tool_use]
            max_parallel_calls = 2
            [tool_use.http_fetch]
            enabled = false
            [[skills.entries]]
            name = "stickers"
            description = "制作贴纸时使用"
            instructions = "BODY_ONLY: 透明背景，选择 PNG，调用 imagegen。"
            [[skills.entries]]
            enabled = false
            name = "disabled"
            description = "不要展示"
            instructions = "DISABLED_BODY"
            """, new TomlSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })!;
        using var skills = new SkillTool(config.Skills);
        Check(skills.SystemPrompt.Contains("stickers") && !skills.SystemPrompt.Contains("BODY_ONLY") && !skills.SystemPrompt.Contains("disabled"), "Skill catalog leaked bodies or disabled entries.");
        Check((await skills.ExecuteAsync("{\"name\":\"stickers\"}")).Contains("BODY_ONLY"), "Skill instructions not loaded.");
        Check((await skills.ExecuteAsync("{\"name\":\"disabled\"}")).Contains("Unknown"), "Disabled skill was loaded.");
        Check((await skills.ExecuteAsync("[]")).Contains("Invalid"), "Malformed skill arguments accepted.");
        using (var registry = ChatToolRegistry.Create(config)) Check(registry.Names.SequenceEqual(new[] { "skill" }) && registry.HasReadOnlyTools, "Skill registration failed.");
        config.Skills.Entries[0].Instructions = "UPDATED_BODY";
        using (var changed = new SkillTool(config.Skills))
            Check((await changed.ExecuteAsync("{\"name\":\"stickers\"}")).Contains("UPDATED_BODY") &&
                (await skills.ExecuteAsync("{\"name\":\"stickers\"}")).Contains("BODY_ONLY"), "Skill reload did not preserve turn snapshots.");
        config.Skills.Enabled = false;
        using (var registry = ChatToolRegistry.Create(config)) Check(!registry.Names.Contains("skill"), "Disabled skill tool advertised.");
        config.Skills.Enabled = true; config.ToolUse.Enabled = false;
        using (var registry = ChatToolRegistry.Create(config)) Check(!registry.HasTools && registry.SystemPrompt == "", "Global tool disable leaked skill metadata.");
        Console.WriteLine("PASS: TOML skill entries, progressive loading, disabled skills, snapshot reload, global disable");

        using var runner = new ChatToolRegistry(2);
        var started = new[] { Signal(), Signal(), Signal() };
        var release = new[] { Signal(), Signal(), Signal() };
        var active = 0; var peak = 0;
        using var read = new ProbeTool("read", true, 20, async (args, ct) => {
            var index = int.Parse(args);
            var now = Interlocked.Increment(ref active);
            peak = Math.Max(peak, now);
            started[index].TrySetResult();
            try { await release[index].Task.WaitAsync(ct); return "result" + index; }
            finally { Interlocked.Decrement(ref active); }
        });
        var tools = new Dictionary<string, IChatTool> { ["read"] = read };
        var pending = runner.ExecuteCallsAsync([Call("a", "read", "0"), Call("b", "read", "1"), Call("c", "read", "2")], tools, new(), "test", 1, default);
        await Task.WhenAll(started[0].Task, started[1].Task).WaitAsync(TimeSpan.FromSeconds(3));
        Check(!started[2].Task.IsCompleted && active == 2, "Concurrency cap was exceeded or reads did not overlap.");
        release[1].TrySetResult();
        await started[2].Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(!release[0].Task.IsCompleted, "Pool did not replenish behind slow first call.");
        release[2].TrySetResult(); release[0].TrySetResult();
        var results = await pending;
        Check(peak == 2 && results.Select(r => r.ToolCallId).SequenceEqual(new[] { "a", "b", "c" }) &&
            results.Select(r => (string)r.Content).SequenceEqual(new[] { "result0", "result1", "result2" }), "Results lost model order.");

        var before = Signal(); var allowBefore = Signal(); var writeStarted = Signal(); var allowWrite = Signal(); var after = Signal();
        using var barrierRead = new ProbeTool("read", true, 20, async (args, ct) => {
            if (args == "before") { before.TrySetResult(); await allowBefore.Task.WaitAsync(ct); }
            else after.TrySetResult();
            return args;
        });
        using var write = new ProbeTool("write", false, 20, async (_, ct) => { writeStarted.TrySetResult(); await allowWrite.Task.WaitAsync(ct); return "written"; });
        tools = new() { ["read"] = barrierRead, ["write"] = write };
        var barriers = runner.ExecuteCallsAsync([Call("1", "read", "before"), Call("2", "write"), Call("3", "read", "after")], tools, new(), "test", 1, default);
        await before.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(!writeStarted.Task.IsCompleted && !after.Task.IsCompleted, "Exclusive barrier started too early.");
        allowBefore.TrySetResult(); await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(!after.Task.IsCompleted, "Read crossed an unfinished write barrier.");
        allowWrite.TrySetResult(); await barriers.WaitAsync(TimeSpan.FromSeconds(3));
        Check(after.Task.IsCompleted, "Later read was never dispatched.");

        var executions = 0;
        using var limited = new ProbeTool("limited", true, 1, (_, _) => { executions++; return Task.FromResult("ok"); });
        var budget = new Dictionary<string, int>();
        var limits = await runner.ExecuteCallsAsync([Call("1", "limited"), Call("2", "limited"), Call("3", "missing")],
            new Dictionary<string, IChatTool> { ["limited"] = limited }, budget, "test", 1, default);
        Check(executions == 2 && (string)limits[1].Content == "ok" && ((string)limits[2].Content).Contains("Unknown"), "Legacy per-tool limits must not block sibling calls; unknown tools remain rejected.");
        await runner.ExecuteCallsAsync([Call("4", "limited")], new Dictionary<string, IChatTool> { ["limited"] = limited }, budget, "test", 2, default);
        Check(executions == 3, "Legacy per-tool limit blocked another permitted global round.");

        using var failing = new ProbeTool("failure", true, 20, (args, _) => args == "bad" ? throw new InvalidOperationException("private") : Task.FromResult("ok"));
        var failures = await runner.ExecuteCallsAsync([Call("f1", "failure", "bad"), Call("f2", "failure", "good")],
            new Dictionary<string, IChatTool> { ["failure"] = failing }, new(), "test", 1, default);
        Check(((string)failures[0].Content).Contains("failed") && (string)failures[1].Content == "ok", "One failed parallel call lost sibling results.");

        using var cancel = new CancellationTokenSource();
        var running = Signal(); var settled = Signal(); var dispatched = 0;
        using var cancellable = new ProbeTool("cancel", true, 20, async (_, ct) => {
            Interlocked.Increment(ref dispatched); running.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); return "never"; }
            finally { settled.TrySetResult(); }
        });
        using var serial = new ChatToolRegistry(1);
        var cancelling = serial.ExecuteCallsAsync([Call("1", "cancel"), Call("2", "cancel")], new Dictionary<string, IChatTool> { ["cancel"] = cancellable }, new(), "test", 1, cancel.Token);
        await running.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancel.Cancel();
        try { await cancelling.WaitAsync(TimeSpan.FromSeconds(3)); throw new Exception("Cancellation swallowed."); } catch (OperationCanceledException) { }
        Check(dispatched == 1 && settled.Task.IsCompleted, "Cancellation started pending calls or abandoned running work.");
        Console.WriteLine("PASS: bounded rolling parallelism, exclusive barriers, ordered results, unified round limits, unknown tools, serial mode and cancellation draining");

        var initial = "{\"choices\":[{\"message\":{\"tool_calls\":[{\"id\":\"s1\",\"type\":\"function\",\"function\":{\"name\":\"skill\",\"arguments\":\"{\\\"name\\\":\\\"stickers\\\"}\"}}]}}]}";
        var handler = new StubHandler(_ => initial);
        handler.Respond = request => handler.Requests.Count == 1 ? initial : "{\"choices\":[{\"message\":{\"content\":\"done\"}}]}";
        using var client = new OpenAiClient(10, "");
        ((Dictionary<string, HttpClient>)typeof(OpenAiClient).GetField("_clients", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!)["https://example.invalid/v1"] = new HttpClient(handler);
        using var chat = new ChatToolRegistry(); chat.Register(skills);
        var answer = await chat.ChatAsync(client, new ModelEntry { Name = "test", BaseUrl = "https://example.invalid/v1", ApiKey = "test" }, [], 2);
        Check(answer == "done" && handler.Requests[1].GetProperty("messages").EnumerateArray().Any(message =>
            message.GetProperty("role").GetString() == "tool" && message.GetProperty("content").GetString()!.Contains("BODY_ONLY")), "Skill output did not reach the next model step.");
        await ToolLogChecks.RunAsync();
        Console.WriteLine("PASS: skill load results reach next model request; existing round limits and log checks preserved");
    }
    private sealed class ProbeTool(string name, bool parallel, int maxCalls, Func<string, CancellationToken, Task<string>> execute) : IChatTool
    {
        public OpenAiTool Definition => new() { Function = new() { Name = name, Description = name, Parameters = new { type = "object" } } };
        public string SystemPrompt => "";
        public int MaxCallsPerTurn => maxCalls;
        public bool IsConcurrencySafe(string arguments) => parallel;
        public Task<string> ExecuteAsync(string arguments, CancellationToken ct = default) => execute(arguments, ct);
        public void Dispose() { }
    }
}
