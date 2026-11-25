using System.Text.Json.Nodes;
using ShiroBot.AiChatPlugin.Tools.Skills;
using ShiroBot.SDK.Dashboard;

namespace ShiroBot.AiChatPlugin;

/// <summary>Dashboard 的“AI 技能”页面：网页在 Web/skills，操作都经过这里校验后写入 data/skills。</summary>
public sealed partial class AiChatPlugin
{
    private void RegisterSkillsPage() =>
        Context.Dashboard.AddWebPage("skills", "AI 技能", new DashboardWebPage(typeof(AiChatPlugin).Assembly, "web/skills/")
        {
            Icon = "menu_book",
            Description = "新建、编辑和导入 SKILL.md 技能",
            HandleAsync = HandleSkillsPageAsync,
            Confirmations = new Dictionary<string, string> { ["delete"] = "删除这个技能？技能目录里的文件会一起删除，不能撤销。" },
            StrictDom = true
        });

    private Task<JsonNode?> HandleSkillsPageAsync(string action, JsonNode? arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string Arg(string key) => arguments?[key]?.GetValue<string>() ?? "";
        bool Flag(string key, bool fallback) => arguments?[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : fallback;
        try
        {
            JsonNode? result = action switch
            {
                "list" => SkillList(),
                "get" => SkillJson(_skillStore.Get(Arg("name")) ?? throw new ArgumentException("找不到这个技能，可能已被删除。"), full: true),
                "save" => SkillJson(_skillStore.Save(Arg("name"), Arg("description"), Arg("instructions"), Flag("enabled", true),
                    string.IsNullOrEmpty(Arg("original_name")) ? null : Arg("original_name")), full: true),
                "toggle" => SkillJson(_skillStore.SetEnabled(Arg("name"), Flag("enabled", true)), full: false),
                "delete" => DeleteSkill(Arg("name")),
                "parse" => SkillJson(SkillStore.Parse(Arg("content")), full: true),
                "import" => ImportSkill(Arg("content"), Flag("overwrite", false)),
                "export" => ExportSkill(Arg("name")),
                "reload" => ReloadSkills(),
                _ => throw new ArgumentException("未知操作。")
            };
            return Task.FromResult(result);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return Task.FromResult<JsonNode?>(new JsonObject { ["error"] = ex.Message });
        }
    }

    private JsonObject SkillList() => new()
    {
        ["skills"] = new JsonArray(_skillStore.All.Select(skill => (JsonNode)SkillJson(skill, full: false)).ToArray()),
        ["directory"] = "data/skills",
        ["enabled"] = _config.ToolUse.Enabled && _config.Skills.Enabled,
        ["limits"] = new JsonObject
        {
            ["skills"] = SkillStore.MaxSkills, ["name"] = SkillStore.MaxName,
            ["description"] = SkillStore.MaxDescription, ["instructions"] = SkillStore.MaxInstructions
        }
    };

    private static JsonObject SkillJson(SkillDefinition skill, bool full)
    {
        var json = new JsonObject
        {
            ["name"] = skill.Name, ["description"] = skill.Description, ["enabled"] = skill.Enabled,
            ["folder"] = skill.Folder, ["instructions_chars"] = skill.Instructions.Length,
            ["modified"] = skill.Modified == default ? null : skill.Modified.ToUnixTimeMilliseconds()
        };
        if (full) json["instructions"] = skill.Instructions;
        return json;
    }

    private JsonObject DeleteSkill(string name)
    {
        _skillStore.Delete(name);
        return new JsonObject { ["deleted"] = name };
    }

    private JsonObject ImportSkill(string content, bool overwrite)
    {
        var parsed = SkillStore.Parse(content);
        var existing = _skillStore.Get(parsed.Name);
        if (existing is not null && !overwrite) throw new ArgumentException($"已经有名为 {parsed.Name} 的技能；勾选覆盖后再导入。");
        return SkillJson(_skillStore.Save(parsed.Name, parsed.Description, parsed.Instructions, parsed.Enabled, existing?.Name), full: false);
    }

    private JsonObject ExportSkill(string name)
    {
        var skill = _skillStore.Get(name) ?? throw new ArgumentException("找不到这个技能。");
        return new JsonObject { ["file_name"] = "SKILL.md", ["folder"] = skill.Folder, ["content"] = SkillStore.Format(skill) };
    }

    private JsonObject ReloadSkills()
    {
        _skillStore.Reload();
        return SkillList();
    }
}
