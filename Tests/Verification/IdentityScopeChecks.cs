using System.Text.Json;
using ShiroBot.AiChatPlugin.Conversation;
using ShiroBot.AiChatPlugin.UserState;
using ShiroBot.SDK.Models;

internal static class IdentityScopeChecks
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "AiChat.Identity", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "data"));
        try
        {
            var settingsPath = Path.Combine(root, "data", "user_settings.json");
            await File.WriteAllTextAsync(settingsPath, "{\"123\":{\"preferredModel\":\"legacy\"}}");
            var first = new UserReference("first", "123");
            var second = new UserReference("second", "123");
            var settings = new UserSettingsStore(root);
            settings.GetOrCreate(first).PreferredModel = "first-model";
            settings.GetOrCreate(second).PreferredModel = "second-model";
            await settings.SaveAsync();
            var reloaded = new UserSettingsStore(root);
            Check(reloaded.Get(first)!.PreferredModel == "first-model" && reloaded.Get(second)!.PreferredModel == "second-model", "User preferences crossed instances or were lost on reload.");
            Check(reloaded.Get(first with { InstanceId = "FIRST" })!.PreferredModel == "first-model", "Instance casing changed identity.");
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
            Check(json.RootElement.GetProperty("123").GetProperty("preferredModel").GetString() == "legacy", "Legacy data was removed or claimed silently.");
            var key = new ConversationKey("first", ChatScene.Group, "../../opaque_channel", "opaque/user");
            var other = key with { InstanceId = "second" };
            Check(key.ToFileSafeString() != other.ToFileSafeString() && !key.ToFileSafeString().Contains('/') && !key.ToFileSafeString().Contains('\\'), "Conversation path is unsafe or lacks instance scope.");
            Check(key.ToFileSafeString() == (key with { InstanceId = "FIRST" }).ToFileSafeString(), "Conversation filename is not deterministic.");
            var store = new ConversationStore(root, true, 20);
            var state = await store.AcquireAsync(key);
            state.SharedPrompt = "first-secret";
            store.SaveMetadata(state); store.Release(key);
            var isolated = await store.AcquireAsync(other);
            Check(isolated.SharedPrompt is null, "Conversation history crossed instances."); store.Release(other);
            var restored = await new ConversationStore(root, true, 20).AcquireAsync(key);
            Check(restored.SharedPrompt == "first-secret", "Scoped metadata did not persist.");
            Console.WriteLine("PASS: opaque identity persistence, instance isolation, safe deterministic filenames and legacy data preservation");
        }
        finally { Directory.Delete(root, true); }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
