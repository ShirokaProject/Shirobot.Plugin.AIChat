namespace ShiroBot.AiChatPlugin.UserState;

/// <summary>
/// 单个用户的偏好设置，跨会话生效。
/// </summary>
public sealed class UserSettings
{
    /// <summary>用户自定义的 system prompt。null 表示使用 default。</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>用户偏好的模型名。null 表示使用 default。</summary>
    public string? PreferredModel { get; set; }

    /// <summary>
    /// 对话模式。null 表示使用默认（multi）。
    /// single = 单次无记录；multi = 用户独立多轮；shared = 群聊共享上下文。
    /// </summary>
    public string? ChatMode { get; set; }
}
