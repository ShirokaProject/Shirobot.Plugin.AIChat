using System.Text.Json;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.Conversation;
using ShiroBot.AiChatPlugin.Memory;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.SDK.Abstractions;

namespace ShiroBot.AiChatPlugin.Tools;

/// <summary>让模型维护当前会话的记忆本。修改立即生效，同一条内容重复新增时直接返回已有条目。</summary>
internal sealed class MemoryTool(MemoryNotebookStore store, MemoryToolSection config, ConversationKey key) : IChatTool
{
    internal const string Prompt = "你有一个记忆本，现有条目列在 [记忆本] 中，可用 memory_note 增改删。用户要求你记住或忘记某事时必须使用。也可以主动记录以后对话仍然有用的长期信息，例如某人的称呼、身份、偏好、约定和重要事实；不要记录寒暄、一次性问题或记忆本里已有的内容。信息变化时用 update 修改原条目，过时或错误时用 delete 删除。每条一句话，写清是谁的信息（群聊中带上对方的称呼）。记忆本满时先删除或合并旧条目。";
    public string SystemPrompt => Prompt;
    public int MaxCallsPerTurn => 5;
    public OpenAiTool Definition { get; } = new()
    {
        Function = new OpenAiToolFunction { Name = "memory_note", Description = "Add, update or delete an entry in this conversation's long-term memory notebook.",
            Parameters = new { type = "object", additionalProperties = false, required = new[] { "action" }, properties = new {
                action = new { type = "string", @enum = new[] { "add", "update", "delete" } },
                id = new { type = "string", description = "Entry id without '#', required for update and delete." },
                text = new { type = "string", description = "One-sentence memory content, required for add and update." } } } }
    };

    public async Task<string> ExecuteAsync(string arguments, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var doc = JsonDocument.Parse(arguments);
        var root = doc.RootElement;
        var action = Get(root, "action");
        var id = Get(root, "id")?.TrimStart('#');
        var text = Get(root, "text")?.Trim();
        var maxChars = Math.Clamp(config.MaxEntryChars, 20, 1000);
        if (action is "add" or "update" && (string.IsNullOrEmpty(text) || text.Length > maxChars))
            return $"text is required and must be at most {maxChars} characters.";
        if (action is "update" or "delete" && string.IsNullOrEmpty(id))
            return "id is required for update and delete.";

        var result = await store.UpdateAsync(key, entries =>
        {
            var now = DateTimeOffset.UtcNow;
            var index = id is null ? -1 : entries.FindIndex(e => e.Id == id);
            switch (action)
            {
                case "add":
                    if (entries.FirstOrDefault(e => e.Text == text) is { } existing)
                        return (false, PluginJson.Serialize(new { id = existing.Id, status = "already_exists" }));
                    if (entries.Count >= Math.Clamp(config.MaxEntries, 1, 200))
                        return (false, "Memory notebook is full. Delete or merge outdated entries first.");
                    var next = entries.Select(e => int.TryParse(e.Id.TrimStart('m'), out var n) ? n : 0).DefaultIfEmpty(0).Max() + 1;
                    entries.Add(new MemoryEntry("m" + next, text!, now));
                    return (true, PluginJson.Serialize(new { id = "m" + next, status = "added" }));
                case "update":
                    if (index < 0) return (false, $"Entry {id} not found.");
                    entries[index] = entries[index] with { Text = text!, UpdatedAt = now };
                    return (true, PluginJson.Serialize(new { id, status = "updated" }));
                case "delete":
                    if (index < 0) return (false, $"Entry {id} not found.");
                    entries.RemoveAt(index);
                    return (true, PluginJson.Serialize(new { id, status = "deleted" }));
                default:
                    return (false, "action must be add, update or delete.");
            }
        }).ConfigureAwait(false);
        BotLog.Info($"[AiChat] 记忆本 {action} id={id} text={text} => {result}");
        return result;
    }

    private static string? Get(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public void Dispose() { }
}
