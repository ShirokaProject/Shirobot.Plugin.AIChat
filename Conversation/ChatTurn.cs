namespace ShiroBot.AiChatPlugin.Conversation;

/// <summary>
/// 一轮对话。Role 取 "user" 或 "assistant"。
/// system prompt 不存在 turn 里，由 UserSettings 提供。
/// </summary>
public sealed record ChatTurn(
    string Role,
    IReadOnlyList<ChatPart> Parts,
    long Timestamp);
