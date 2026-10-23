using System.Text.Json;
using ShiroBot.AiChatPlugin;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.Conversation;
using ShiroBot.AiChatPlugin.Memory;
using ShiroBot.AiChatPlugin.Tools;
using ShiroBot.SDK.Models;

internal static class MemoryNotebookChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static async Task RunAsync()
    {
        var config = new PluginConfig();
        Check(!config.ToolUse.Memory.Enabled && !config.ToolUse.Memory.DailyReview, "Memory notebook and daily review must be off by default.");

        var root = Path.Combine(Path.GetTempPath(), "aichat-notebook-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new MemoryNotebookStore(root);
            var key = new ConversationKey("test-instance", ChatScene.Group, "g1", "0");
            var other = new ConversationKey("test-instance", ChatScene.Group, "g2", "0");
            var section = new MemoryToolSection { MaxEntries = 2, MaxEntryChars = 20 };
            using var tool = new MemoryTool(store, section, key);

            var added = JsonDocument.Parse(await tool.ExecuteAsync("{\"action\":\"add\",\"text\":\"小明喜欢猫\"}")).RootElement;
            Check(added.GetProperty("id").GetString() == "m1", "First entry must get id m1.");
            Check((await tool.ExecuteAsync("{\"action\":\"add\",\"text\":\"小明喜欢猫\"}")).Contains("already_exists"), "Duplicate text must not be added twice.");
            await tool.ExecuteAsync("{\"action\":\"add\",\"text\":\"小红是管理员\"}");
            Check((await tool.ExecuteAsync("{\"action\":\"add\",\"text\":\"第三条\"}")).Contains("full"), "Notebook must enforce max entries.");
            Check((await tool.ExecuteAsync("{\"action\":\"add\",\"text\":\"" + new string('长', 21) + "\"}")).Contains("at most"), "Entries must enforce max length.");
            Check((await tool.ExecuteAsync("{\"action\":\"update\",\"id\":\"#m1\",\"text\":\"小明喜欢狗\"}")).Contains("updated"), "Update must accept ids with '#'.");
            Check((await tool.ExecuteAsync("{\"action\":\"delete\",\"id\":\"m9\"}")).Contains("not found"), "Unknown ids must be reported.");
            Check((await tool.ExecuteAsync("{\"action\":\"delete\",\"id\":\"m2\"}")).Contains("deleted"), "Delete must remove an entry.");
            var notes = await store.LoadAsync(key);
            Check(notes.Count == 1 && notes[0].Text == "小明喜欢狗" && (await store.LoadAsync(other)).Count == 0,
                "Notebook changes must persist and stay isolated per conversation.");
            await tool.ExecuteAsync("{\"action\":\"add\",\"text\":\"新条目\"}");
            Check((await store.LoadAsync(key)).Last().Id == "m2", "New ids must continue after the highest existing id.");
            Console.WriteLine("PASS: memory notebook add/update/delete, dedupe, limits, persistence and per-conversation isolation");

            var original = await store.LoadAsync(key);
            var reviewed = AiChatPlugin.ParseMemoryReview(
                "好的：{\"keep\":[{\"id\":\"m1\",\"text\":\"小明喜欢狗和猫\"},{\"id\":\"m404\",\"text\":\"编造\"},{\"id\":\"m1\",\"text\":\"重复\"}]}", original, 20);
            Check(reviewed is { Count: 1 } && reviewed[0] is { Id: "m1", Text: "小明喜欢狗和猫" }, "Review must keep only known ids once, dropping removed entries.");
            Check(AiChatPlugin.ParseMemoryReview("not json", original, 20) is null && AiChatPlugin.ParseMemoryReview("{\"x\":1}", original, 20) is null,
                "Malformed review output must leave the notebook unchanged.");
            var path = store.Notebooks().Single();
            Check(await store.ReplaceIfUnchangedAsync(path, original, reviewed!) && (await store.LoadAsync(key)).Single().Text == "小明喜欢狗和猫",
                "Review result must replace an unchanged notebook.");
            Check(!await store.ReplaceIfUnchangedAsync(path, original, []), "Review must not overwrite a notebook edited meanwhile.");
            await store.ClearAsync(key);
            Check((await store.LoadAsync(key)).Count == 0, "Clear must empty the notebook.");
            Console.WriteLine("PASS: daily review parsing, known-id guard, malformed output and concurrent-edit protection");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
