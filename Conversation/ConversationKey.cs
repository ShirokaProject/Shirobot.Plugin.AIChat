namespace ShiroBot.AiChatPlugin.Conversation;

/// <summary>
/// 会话场景：私聊或群聊。
/// </summary>
public enum ChatScene
{
    Friend,
    Group
}

/// <summary>
/// 会话标识。
/// 私聊：scene=Friend, peerId=对方 uid, userId=对方 uid（一致）。
/// 群聊：scene=Group, peerId=群号, userId=发言者 uid（每用户独立）。
/// </summary>
public readonly record struct ConversationKey(ChatScene Scene, long PeerId, long UserId)
{
    /// <summary>
    /// 用作文件名的安全形式：scene_peer_user。
    /// </summary>
    public string ToFileSafeString() => $"{Scene}_{PeerId}_{UserId}";
}
