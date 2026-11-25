using System.Reflection;
using System.Text.Json;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.AiChatPlugin.Tools;
using ShiroBot.AiChatPlugin.Tools.Skills;
using System.Text.Json.Nodes;
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
        var skillRoot = Path.Combine(Path.GetTempPath(), "aichat-skills-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SkillStore(skillRoot);
            var changes = 0;
            store.Changed += () => changes++;
            // 旧版 config.toml 里的技能迁移一次；在页面上删掉后不会因为配置里还在而重新出现。
            Check(store.MigrateLegacy(config.Skills.Entries) == 2 && store.All.Count == 2 && !store.Get("disabled")!.Enabled, "Legacy TOML skills must migrate once, keeping the enabled flag.");
            store.Delete("disabled");
            Check(store.MigrateLegacy(config.Skills.Entries) == 0 && store.Get("disabled") is null, "A deleted migrated skill must not be migrated again.");
            Check(File.Exists(Path.Combine(skillRoot, "stickers", "SKILL.md")), "Skills must be stored as <folder>/SKILL.md.");

            using var skills = new SkillTool(store.All);
            Check(skills.SystemPrompt.Contains("stickers") && !skills.SystemPrompt.Contains("BODY_ONLY"), "Skill catalog must list names and purposes but not bodies.");
            Check((await skills.ExecuteAsync("{\"name\":\"stickers\"}")).Contains("BODY_ONLY"), "Skill instructions not loaded.");
            Check((await skills.ExecuteAsync("{\"name\":\"disabled\"}")).Contains("Unknown"), "Deleted skill was loaded.");
            Check((await skills.ExecuteAsync("[]")).Contains("Invalid"), "Malformed skill arguments accepted.");
            store.SetEnabled("stickers", false);
            using (var off = new SkillTool(store.All)) Check(!off.HasSkills, "Disabled skills must not be offered.");
            store.SetEnabled("stickers", true);

            // 常见 SKILL.md 写法：引号、多行块、frontmatter 里的其他字段。
            var imported = SkillStore.Parse("\uFEFF---\r\nname: \"pdf-helper\"\r\ndescription: >\r\n  处理 PDF 时使用，\r\n  包括合并和拆分\r\nlicense: MIT\r\nallowed-tools: [Read]\r\n---\r\n\r\n# PDF\r\n1. 先读文件\r\n");
            Check(imported.Name == "pdf-helper" && imported.Description == "处理 PDF 时使用， 包括合并和拆分" && imported.Instructions == "# PDF\n1. 先读文件" && imported.Enabled,
                "SKILL.md frontmatter with quotes and folded blocks must parse.");
            var literal = SkillStore.Parse("---\nname: 'it''s'\ndescription: |\n  第一行\n  第二行\nenabled: false\n---\nbody");
            Check(literal.Name == "it's" && literal.Description == "第一行\n第二行" && !literal.Enabled, "Literal blocks, single quotes and enabled:false must parse.");
            foreach (var bad in new[] { "no frontmatter", "---\nname: x\n---\nbody", "---\nname: x\ndescription: y\n---\n", "---\nname: x\ndescription: y\n" })
            {
                try { SkillStore.Parse(bad); throw new Exception("Invalid SKILL.md accepted: " + bad); } catch (ArgumentException) { }
            }
            var tricky = new SkillDefinition("引号\"和\\斜杠", "多行\n用途: 带冒号 # 不是注释", "正文\n---\n分隔线也保留", false, "", default);
            var roundTrip = SkillStore.Parse(SkillStore.Format(tricky));
            Check(roundTrip with { Folder = "", Modified = default } == tricky, "Format and Parse must round-trip names, descriptions and bodies.");

            // 改名保留原目录；重名、超长被拒绝；目录名去掉路径字符。
            var saved = store.Save("../逃逸 名", "用途", "说明", true, null);
            Check(saved.Folder == "逃逸-名", $"Folder names must not contain path segments, got {saved.Folder}.");
            Check(Path.GetDirectoryName(Path.Combine(skillRoot, saved.Folder, "SKILL.md")) == Path.Combine(skillRoot, saved.Folder), "Skill folder escaped the skills directory.");
            var renamed = store.Save("renamed", "用途", "说明", true, "../逃逸 名");
            Check(renamed.Folder == saved.Folder && store.Get("../逃逸 名") is null, "Renaming must keep the folder and drop the old name.");
            try { store.Save("stickers", "x", "y", true, "renamed"); throw new Exception("Duplicate name accepted."); } catch (ArgumentException) { }
            try { store.Save(new string('n', 65), "x", "y", true, null); throw new Exception("Overlong name accepted."); } catch (ArgumentException) { }

            // 手动改文件后重新读取；无效文件跳过，不影响其他技能。
            File.WriteAllText(Path.Combine(skillRoot, "stickers", "SKILL.md"), "---\nname: stickers\ndescription: 改过\n---\nEDITED_ON_DISK");
            Directory.CreateDirectory(Path.Combine(skillRoot, "broken"));
            File.WriteAllText(Path.Combine(skillRoot, "broken", "SKILL.md"), "not a skill");
            store.Reload();
            Check(store.Get("stickers")!.Instructions == "EDITED_ON_DISK" && store.All.Count == 2, "Reload must pick up edited files and skip broken ones.");
            Check(changes > 0, "Store changes must be announced so the skill tool is rebuilt.");

            // 注册表替换 skill 工具时，已经开始的对话继续使用拿到的那份。
            using (var registry = ChatToolRegistry.Create(config, skills: store.All))
            {
                Check(registry.Names.SequenceEqual(new[] { "skill" }) && registry.HasReadOnlyTools, "Skill registration failed.");
                var catalogBefore = registry.SystemPrompt;
                store.Save("new-skill", "新的用途", "新的说明", true, null);
                registry.ReplaceSkills(new SkillTool(store.All));
                Check(registry.SystemPrompt.Contains("new-skill") && !catalogBefore.Contains("new-skill"), "Replacing skills must update the catalog.");
                registry.ReplaceSkills(new SkillTool([]));
                Check(!registry.Names.Contains("skill") && !registry.HasTools, "An empty skill snapshot must remove the skill tool.");
            }
            config.Skills.Enabled = false;
            using (var registry = ChatToolRegistry.Create(config, skills: store.All)) Check(!registry.Names.Contains("skill"), "Disabled skill tool advertised.");
            config.Skills.Enabled = true; config.ToolUse.Enabled = false;
            using (var registry = ChatToolRegistry.Create(config, skills: store.All)) Check(!registry.HasTools && registry.SystemPrompt == "", "Global tool disable leaked skill metadata.");
            config.ToolUse.Enabled = true;

            // Dashboard 页面接口：错误以 JSON 返回，不抛出。
            var plugin = new ShiroBot.AiChatPlugin.AiChatPlugin();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(ShiroBot.AiChatPlugin.AiChatPlugin).GetField("_config", flags)!.SetValue(plugin, config);
            typeof(ShiroBot.AiChatPlugin.AiChatPlugin).GetField("_skillStore", flags)!.SetValue(plugin, store);
            var handle = typeof(ShiroBot.AiChatPlugin.AiChatPlugin).GetMethod("HandleSkillsPageAsync", flags)!;
            async Task<JsonNode?> Page(string action, JsonObject? args = null) =>
                await (Task<JsonNode?>)handle.Invoke(plugin, [action, args, CancellationToken.None])!;
            var list = await Page("list");
            Check(list!["skills"]!.AsArray().Count == store.All.Count && list["enabled"]!.GetValue<bool>() && list["skills"]![0]!["instructions"] is null,
                "The list must not include full instructions.");
            var importText = "---\nname: imported\ndescription: 导入的\n---\n导入正文";
            Check((await Page("import", new JsonObject { ["content"] = importText }))!["name"]!.GetValue<string>() == "imported", "Import failed.");
            Check((await Page("import", new JsonObject { ["content"] = importText }))!["error"] is not null, "Importing a duplicate without overwrite must fail.");
            Check((await Page("import", new JsonObject { ["content"] = importText.Replace("导入正文", "覆盖正文"), ["overwrite"] = true }))!["error"] is null
                && store.Get("imported")!.Instructions == "覆盖正文", "Overwrite import failed.");
            Check((await Page("export", new JsonObject { ["name"] = "imported" }))!["content"]!.GetValue<string>().Contains("覆盖正文"), "Export lost the body.");
            Check((await Page("save", new JsonObject { ["name"] = "", ["description"] = "x", ["instructions"] = "y" }))!["error"] is not null, "Invalid save must return an error.");
            Check((await Page("delete", new JsonObject { ["name"] = "imported" }))!["deleted"] is not null && store.Get("imported") is null, "Delete failed.");
            Check((await Page("bogus"))!["error"] is not null, "Unknown actions must be rejected.");
        }
        finally { try { Directory.Delete(skillRoot, true); } catch (IOException) { } }
        Console.WriteLine("PASS: file-based skills: migration, SKILL.md parsing, rename/reload, registry swap and dashboard actions");

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
        using var chat = new ChatToolRegistry();
        chat.Register(new SkillTool([new SkillDefinition("stickers", "制作贴纸时使用", "BODY_ONLY: 透明背景", true, "stickers", default)]));
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
