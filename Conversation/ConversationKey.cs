using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
/// 私聊：scene=Friend, peerId=对方原生 uid, userId=对方原生 uid（一致）。
/// 群聊：scene=Group, peerId=群原生 ID, userId=发言者原生 ID（每用户独立）。
/// </summary>
public readonly record struct ConversationKey(string InstanceId, ChatScene Scene, string PeerId, string UserId)
{
    /// <summary>
    /// 用作文件名的安全形式：包含来源实例的确定性摘要；不透明 ID 不作为路径片段。
    /// </summary>
    public string ToFileSafeString()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(InstanceId);
        var identity = PluginJson.Serialize(new[] { InstanceId.ToUpperInvariant(), Scene.ToString(), PeerId, UserId });
        return Scene + "_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
}
