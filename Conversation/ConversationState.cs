namespace ShiroBot.AiChatPlugin.Conversation;

/// <summary>
/// 单个会话的运行时状态。仅在单次请求生命周期内存在，处理完后释放。
/// </summary>
internal sealed class ConversationState
{
    public ConversationKey Key { get; }
    public List<ChatTurn> Turns { get; } = [];

    /// <summary>共享会话的公共 system prompt（仅 shared 模式使用）。null 表示用默认。</summary>
    public string? SharedPrompt { get; set; }

    /// <summary>共享会话的公共模型偏好（仅 shared 模式使用）。null 表示用默认。</summary>
    public string? SharedModel { get; set; }

    public ConversationState(ConversationKey key)
    {
        Key = key;
    }
}
