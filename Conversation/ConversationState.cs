using ShiroBot.AiChatPlugin.Memory;

namespace ShiroBot.AiChatPlugin.Conversation;

/// <summary>
/// 单个会话的运行时状态。仅在单次请求生命周期内存在，处理完后释放。
/// </summary>
internal sealed class ConversationState
{
    public ConversationKey Key { get; }
    public List<ChatTurn> Turns { get; } = [];

    /// <summary>早期对话合并成的历史摘要，随会话历史一起保存和清空。</summary>
    public string? Summary { get; set; }

    /// <summary>
    /// 放进 system prompt 的记忆本快照，只在新会话开始或压缩时刷新，之后的变化附在用户消息末尾。
    /// null 表示尚未建立快照（旧会话），此时整本记忆本都作为变化发送。
    /// </summary>
    public List<MemoryEntry>? MemorySnapshot { get; set; }

    /// <summary>共享会话的公共 system prompt（仅 shared 模式使用）。null 表示用默认。</summary>
    public string? SharedPrompt { get; set; }

    /// <summary>共享会话的公共模型偏好（仅 shared 模式使用）。null 表示用默认。</summary>
    public string? SharedModel { get; set; }

    public ConversationState(ConversationKey key)
    {
        Key = key;
    }
}
