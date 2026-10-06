using ShiroBot.SDK.Config;

namespace ShiroBot.AiChatPlugin.Config;

/// <summary>
/// Provider 定义。每个 provider 有独立的 base_url 和 api_key。
/// 可选 fetch_models = true 启动时自动从 /v1/models 拉取可用模型列表。
/// </summary>
public sealed class ProviderEntry
{
    /// <summary>Provider 标识名，供 model 引用。</summary>
    [ConfigField("唯一服务商标识，供模型引用。", Label = "服务商名称")]
    public string Name { get; set; } = "";

    [ConfigField("OpenAI 兼容接口地址，通常以 /v1 结尾。", Label = "接口地址")]
    public string BaseUrl { get; set; } = "";

    [ConfigField("该服务商接口使用的 API 密钥。", Label = "API 密钥", Type = "password")]
    public string ApiKey { get; set; } = "";

    /// <summary>是否在启动时从 API 自动拉取 models 列表。</summary>
    [ConfigField("启动或刷新时从服务商 /models 接口获取可用模型。", Label = "自动获取模型列表")]
    public bool FetchModels { get; set; }

    /// <summary>自动拉取时的模型名前缀过滤（为空则全部导入）。</summary>
    [ConfigField("只导入匹配这些名称前缀的模型；空列表不过滤。", Label = "自动获取模型的前缀过滤")]
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
    [ConfigField("发送给上游 API 的模型标识。", Label = "模型名称")]
    public string Name { get; set; } = "";

    /// <summary>引用 [[providers]] 中的 name。优先级高于直接写 base_url/api_key。</summary>
    [ConfigField("引用服务商名称，优先于模型单独配置的接口地址和密钥。", Label = "所属服务商")]
    public string? Provider { get; set; }

    [ConfigField("未引用服务商时使用的 OpenAI 兼容接口地址。", Label = "独立接口地址")]
    public string? BaseUrl { get; set; }
    [ConfigField("未引用服务商时使用的 API 密钥。", Label = "独立 API 密钥", Type = "password")]
    public string? ApiKey { get; set; }
    [ConfigField("关闭时过滤发送给该模型的图片内容。", Label = "支持图片输入")]
    public bool SupportsVision { get; set; } = true;
    [ConfigField("模型列表中展示的友好名称。", Label = "模型显示名称")]
    public string? DisplayName { get; set; }
}
