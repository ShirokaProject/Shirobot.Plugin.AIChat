using System.Text;
using ShiroBot.AiChatPlugin.Config;

namespace ShiroBot.AiChatPlugin;

/// <summary>模型解析：按名称、provider 和候选列表查找模型。</summary>
public sealed partial class AiChatPlugin
{
    private ModelEntry? ResolveModel(string name)
    {
        name = name.Trim();
        // 优先支持 "provider-name" 格式匹配（列表显示格式）。必须放在裸 name 前，
        // 避免 provider fetch 拉到同名裸模型时抢先命中错误 provider。
        var match = _config.Models.FirstOrDefault(m =>
            !string.IsNullOrEmpty(m.Provider) &&
            string.Equals(GetModelDisplayId(m), name, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match;

        // 精确匹配 model name
        match = _config.Models.FirstOrDefault(m =>
            string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match;

        if (ResolveProviderModel(name) is { } configured) return configured;
        if (_config.Models.Count == 0 && string.Equals(name, _config.Default.Model, StringComparison.OrdinalIgnoreCase))
            return new ModelEntry { Name = _config.Default.Model };
        return null;
    }

    // Discovery is a catalog, not an authority on whether an explicitly named upstream model exists.
    private ModelEntry? ResolveProviderModel(string name)
    {
        var provider = _config.Providers.Where(p => p.FetchModels && !string.IsNullOrWhiteSpace(p.Name))
            .OrderByDescending(p => p.Name.Length)
            .FirstOrDefault(p => name.StartsWith(p.Name + "-", StringComparison.OrdinalIgnoreCase) && name.Length > p.Name.Length + 1);
        if (provider is not null)
        {
            var modelId = name[(provider.Name.Length + 1)..];
            if (provider.ModelFilter is { Length: > 0 } && !provider.ModelFilter.Any(f => modelId.StartsWith(f, StringComparison.OrdinalIgnoreCase))) return null;
            return new ModelEntry { Name = modelId, Provider = provider.Name, SupportsVision = true };
        }
        return null;
    }

    private ModelEntry? ResolveModelForSelection(string name)
    {
        name = name.Trim();
        var registered = _config.Models.FirstOrDefault(m =>
            string.Equals(GetModelDisplayId(m), name, StringComparison.OrdinalIgnoreCase));
        if (registered is not null) return registered;
        if (ResolveProviderModel(name) is { } explicitModel) return explicitModel;
        return _config.Models.Count == 0 && string.Equals(name, _config.Default.Model, StringComparison.OrdinalIgnoreCase)
            ? ResolveModel(name) : null;
    }

    private string BuildModelNotFoundReply(string keyword)
    {
        var matches = FindModelCandidates(keyword, maxCount: 12);
        if (matches.Count == 0)
        {
            return $"未找到模型: {keyword}。发送 #model list 查看可用列表。";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"未找到模型: {keyword}");
        sb.AppendLine("你可能想选:");
        foreach (var m in matches)
        {
            var displayLabel = !string.IsNullOrEmpty(m.DisplayName) ? $" ({m.DisplayName})" : "";
            sb.AppendLine($"  - {GetModelDisplayId(m)}{displayLabel}");
        }
        sb.AppendLine("用法: #model <上面的完整名称>");
        return sb.ToString().TrimEnd();
    }

    private List<ModelEntry> FindModelCandidates(string keyword, int maxCount)
    {
        keyword = keyword.Trim();
        if (string.IsNullOrEmpty(keyword)) return [];

        return _config.Models
            .Where(m =>
                m.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                GetModelDisplayId(m).Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(m.DisplayName) && m.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(m => GetModelDisplayId(m).StartsWith(keyword, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(m => m.Name.StartsWith(keyword, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(GetModelDisplayId, StringComparer.OrdinalIgnoreCase)
            .Take(maxCount)
            .ToList();
    }

    private static string GetModelDisplayId(ModelEntry model) =>
        string.IsNullOrWhiteSpace(model.Provider) ? model.Name : $"{model.Provider}-{model.Name}";

    private bool IsSelectedModel(ModelEntry model, string? selected)
    {
        if (string.IsNullOrWhiteSpace(selected)) return false;

        return string.Equals(model.Name, selected, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(GetModelDisplayId(model), selected, StringComparison.OrdinalIgnoreCase);
    }

    private string FormatSelectedModel(string? selected, bool includeDefaultMark)
    {
        var isDefault = string.IsNullOrWhiteSpace(selected);
        var value = isDefault ? _config.Default.Model : selected!;
        var model = ResolveModel(value);
        var display = model is null ? value : GetModelDisplayId(model);
        return isDefault && includeDefaultMark ? display + " (默认)" : display;
    }
}
