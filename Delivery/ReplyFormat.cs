using System.Text;

namespace ShiroBot.AiChatPlugin.Delivery;

internal enum ReplyMode { Answer, Chat }

/// <summary>
/// 模型自选的回复模式。模型在首行写 [chat]、[chat+reply] 或 [answer]，正文从下一行开始；
/// 缺少标记时按解答模式整条发送，保持旧行为。
/// </summary>
internal sealed record ParsedReply(ReplyMode Mode, bool Quote, string Text, IReadOnlyList<string> Messages)
{
    public const string SystemPrompt =
        "回复格式：第一行只写模式标记，正文从第二行开始。\n" +
        "[chat] 聊天模式：用于闲聊、吹水、接话、情绪回应和简单问答。像真人发消息一样，每一行会作为一条独立消息依次发出，每行一句短话；不要分点、不要标题、不要长段落。\n" +
        "条数要克制：默认只发 1 条；确实有自然的补充或追问时才发 2 条；只有情绪很强烈（特别激动、委屈、炸毛、大笑）时才发 3 条。同一个意思不要拆成几条重复说。\n" +
        "[chat+reply] 同聊天模式，但第一条会引用回复对方的原消息。只在需要明确回应对方某句话时使用（如回答对方的问题、群里指定回应某人）；纯吹水、附和、接梗用 [chat]。\n" +
        "[answer] 解答模式：用户要求解释、教程、对比、分析、写代码，或问题本身需要完整说明时使用，可以写成完整的长内容，长内容会自动收进聊天记录。\n" +
        "根据当前这条消息选择模式，不要因为之前回复过长内容就继续写长；拿不准时用 [chat]。";

    private static readonly (string Tag, ReplyMode Mode, bool Quote)[] Tags =
    [
        ("[chat+reply]", ReplyMode.Chat, true),
        ("[chat+quote]", ReplyMode.Chat, true),
        ("[chat]", ReplyMode.Chat, false),
        ("[answer]", ReplyMode.Answer, true)
    ];

    public static ParsedReply Parse(string raw, int maxMessages)
    {
        ReplyMode? mode = null;
        var quote = true;
        var body = new List<string>();
        foreach (var line in raw.Replace("\r\n", "\n").Split('\n'))
        {
            // 工具轮次的前置文字会拼在最终回答前，因此以最后出现的标记为准，所有标记行都不发送。
            if (TryParseTag(line, out var m, out var q, out var rest))
            {
                mode = m;
                quote = q;
                if (rest.Length > 0) body.Add(rest);
                continue;
            }
            body.Add(line);
        }

        var text = string.Join('\n', body).Trim();
        if (text.Length == 0) text = raw.Trim();
        if (mode != ReplyMode.Chat) return new(ReplyMode.Answer, true, text, [text]);

        var messages = body.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        maxMessages = Math.Max(1, maxMessages);
        if (messages.Count > maxMessages)
            messages = [.. messages.Take(maxMessages - 1), string.Join('\n', messages.Skip(maxMessages - 1))];
        if (messages.Count == 0) messages = [text];
        return new(ReplyMode.Chat, quote, string.Join('\n', messages), messages);
    }

    /// <summary>流式预览内容：解答模式去掉标记后推送；聊天模式或标记尚未写完时返回 null。</summary>
    public static string? StreamPreview(string cumulative)
    {
        var start = cumulative.TrimStart();
        var newline = start.IndexOf('\n');
        var firstLine = newline < 0 ? start : start[..newline];
        if (TryParseTag(firstLine, out var mode, out _, out _))
            return mode == ReplyMode.Chat ? null : Parse(start, 1).Text;
        // 首行仍可能是未写完的标记。
        if (newline < 0 && Tags.Any(t => t.Tag.StartsWith(firstLine.Trim(), StringComparison.OrdinalIgnoreCase)))
            return null;
        return Parse(start, 1).Text;
    }

    private static bool TryParseTag(string line, out ReplyMode mode, out bool quote, out string rest)
    {
        var trimmed = line.TrimStart();
        foreach (var (tag, m, q) in Tags)
        {
            if (!trimmed.StartsWith(tag, StringComparison.OrdinalIgnoreCase)) continue;
            mode = m;
            quote = q;
            rest = trimmed[tag.Length..].Trim();
            return true;
        }
        mode = ReplyMode.Answer;
        quote = true;
        rest = "";
        return false;
    }

    /// <summary>把长文本按段落切成若干节点，单个段落超长时在句末标点处断开。</summary>
    public static List<string> SplitForForward(string text, int maxChars)
    {
        maxChars = Math.Max(50, maxChars);
        var nodes = new List<string>();
        var current = new StringBuilder();
        var currentRunes = 0;

        void Flush()
        {
            var node = current.ToString().Trim();
            if (node.Length > 0) nodes.Add(node);
            current.Clear();
            currentRunes = 0;
        }

        foreach (var paragraph in text.Replace("\r\n", "\n").Split("\n\n"))
        {
            foreach (var piece in SplitLong(paragraph, maxChars))
            {
                var runes = piece.EnumerateRunes().Count();
                if (currentRunes > 0 && currentRunes + runes > maxChars) Flush();
                if (currentRunes > 0) current.Append("\n\n");
                current.Append(piece);
                currentRunes += runes;
            }
        }
        Flush();
        return nodes.Count > 0 ? nodes : [text];
    }

    private static IEnumerable<string> SplitLong(string paragraph, int maxChars)
    {
        var piece = new StringBuilder();
        var runes = 0;
        var lastBreak = -1;
        foreach (var rune in paragraph.EnumerateRunes())
        {
            piece.Append(rune.ToString());
            runes++;
            if (rune.Value is '。' or '！' or '？' or '；' or '.' or '!' or '?' or ';' or '\n') lastBreak = piece.Length;
            if (runes < maxChars) continue;
            // 找不到句末标点时硬切，避免单个节点无限增长。
            var cut = lastBreak > 0 ? lastBreak : piece.Length;
            yield return piece.ToString(0, cut);
            var remainder = piece.ToString(cut, piece.Length - cut);
            piece.Clear().Append(remainder);
            runes = remainder.EnumerateRunes().Count();
            lastBreak = -1;
        }
        if (piece.Length > 0) yield return piece.ToString();
    }
}
