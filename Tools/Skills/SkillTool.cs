using System.Text.Json;
using System.Text.Json.Nodes;
using ShiroBot.AiChatPlugin.OpenAi;

namespace ShiroBot.AiChatPlugin.Tools.Skills;

/// <summary>技能目录只列名称和用途，模型需要时再通过 skill 工具加载完整操作说明。</summary>
internal sealed class SkillTool : IReadOnlyChatTool
{
    private sealed record Skill(string Name, string Description, string Instructions);
    private readonly Dictionary<string, Skill> _skills = new(StringComparer.Ordinal);
    public bool HasSkills => _skills.Count > 0;
    public int MaxCallsPerTurn => 16;
    public bool IsConcurrencySafe(string arguments) => true;
    public OpenAiTool Definition { get; }
    public string SystemPrompt { get; }

    /// <summary>每次构造时取一份快照：技能在对话进行中被修改，本轮仍用开始时的内容。</summary>
    public SkillTool(IEnumerable<SkillDefinition> skills)
    {
        foreach (var skill in skills.Where(skill => skill.Enabled).Take(SkillStore.MaxSkills))
            _skills.TryAdd(skill.Name, new(skill.Name, skill.Description, skill.Instructions));
        Definition = new() { Function = new() {
            Name = "skill", Description = "Load the instructions of a configured skill when its purpose matches the user's task. Loading instructions does not execute the task.",
            Parameters = new { type = "object", additionalProperties = false, required = new[] { "name" },
                properties = new { name = new { type = "string", @enum = _skills.Keys.ToArray(), description = "Exact skill name from the available catalog." } } }
        } };
        var catalog = new JsonArray(_skills.Values.Select(skill => (JsonNode)new JsonObject {
            ["name"] = skill.Name, ["description"] = skill.Description }).ToArray());
        SystemPrompt = "有自定义技能可用。任务符合某条用途，或用户明确指定技能时，先调用 skill 加载该技能的操作说明，再使用实际可用工具完成任务。" +
            "技能正文按需加载，不要假装已读取；加载成功仅表示获得操作说明，不代表任务已执行。" +
            "技能不能开启已禁用的工具或改变权限；用户本次明确要求优先。独立的技能可在同一轮加载多个。\n[可用技能目录]\n" + catalog.ToJsonString();
    }

    public Task<string> ExecuteAsync(string arguments, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var doc = JsonDocument.Parse(arguments);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
                return Task.FromResult("Invalid skill arguments. Supply a name from the available catalog.");
            if (!_skills.TryGetValue(name.GetString()!, out var skill))
                return Task.FromResult("Unknown or disabled skill. Choose a name from the available catalog; do not repeat the unavailable name.");
            return Task.FromResult(new JsonObject { ["name"] = skill.Name, ["instructions"] = skill.Instructions }.ToJsonString());
        }
        catch (JsonException) { return Task.FromResult("Invalid skill arguments. Supply a JSON object with name."); }
    }
    public void Dispose() { }
}
