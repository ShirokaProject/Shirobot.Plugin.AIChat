namespace ShiroBot.AiChatPlugin.Config;

/// <summary>
/// Provider 定义。每个 provider 有独立的 base_url 和 api_key。
/// 可选 fetch_models = true 启动时自动从 /v1/models 拉取可用模型列表。
/// </summary>
public sealed class ProviderEntry
{
    /// <summary>Provider 标识名，供 model 引用。</summary>
    public string Name { get; set; } = "";

    public string BaseUrl { get; set; } = "";

    public string ApiKey { get; set; } = "";

    /// <summary>是否在启动时从 API 自动拉取 models 列表。</summary>
    public bool FetchModels { get; set; }

    /// <summary>自动拉取时的模型名前缀过滤（为空则全部导入）。</summary>
    public string[]? ModelFilter { get; set; }
}

/// <summary>
/// 单个模型注册项。可以为不同模型指定不同 base_url / api_key，
/// 或通过 provider 字段引用一个已定义的 provider。
/// 默认假设支持视觉——绝大多数现代模型都支持。如果某模型确实是纯文本，
/// 在配置里显式写 supports_vision = false 把图片过滤掉即可。
/// </summary>
public sealed class ModelEntry
{
    public string Name { get; set; } = "";

    /// <summary>引用 [[providers]] 中的 name。优先级高于直接写 base_url/api_key。</summary>
    public string? Provider { get; set; }

    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public bool SupportsVision { get; set; } = true;
    public string? DisplayName { get; set; }
}
