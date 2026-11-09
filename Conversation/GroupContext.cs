namespace ShiroBot.AiChatPlugin.Conversation;

internal static class GroupContext
{
    internal const string Header = "[群聊最近消息：按时间顺序，用来理解大家在聊什么，不需要逐条回应]\n";
    internal static string NewLines(string recent, IEnumerable<ChatTurn> history)
    {
        var seen = history.SelectMany(turn => turn.Parts.OfType<TextPart>())
            .Where(part => part.Text.StartsWith(Header, StringComparison.Ordinal))
            .SelectMany(part => part.Text[Header.Length..].Split('\n', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.Ordinal);
        return string.Join('\n', recent.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(seen.Add));
    }
}
