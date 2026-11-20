using System.Text;
using System.Text.RegularExpressions;

namespace ShiroBot.AiChatPlugin.Delivery;

/// <summary>
/// 关闭 Markdown 发送时，把模型偶尔写出的 Markdown 标记改成 QQ 纯文本能正常显示的样子。
/// 只处理不会误伤普通文字的常见写法：加粗、标题、列表符号、链接、代码围栏、引用和分隔线；
/// 代码块里的内容原样保留。
/// </summary>
internal static partial class PlainTextFormat
{
    public static string Strip(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var output = new StringBuilder(text.Length);
        var inFence = false;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (Fence().IsMatch(raw))
            {
                inFence = !inFence;
                continue;
            }
            output.Append(inFence ? raw : StripLine(raw)).Append('\n');
        }
        var result = ExtraBlankLines().Replace(output.ToString(), "\n\n").Trim();
        return result.Length == 0 ? text.Trim() : result;
    }

    private static string StripLine(string line)
    {
        if (Rule().IsMatch(line)) return "";
        line = Heading().Replace(line, "");
        line = Quote().Replace(line, "");
        line = Bullet().Replace(line, "$1· ");
        line = Link().Replace(line, match =>
        {
            var label = match.Groups["label"].Value.Trim();
            var url = match.Groups["url"].Value;
            return label.Length == 0 || label == url ? url : $"{label} {url}";
        });
        line = Bold().Replace(line, "${body}");
        line = InlineCode().Replace(line, "${body}");
        return line;
    }

    [GeneratedRegex(@"^\s{0,3}(```|~~~)")]
    private static partial Regex Fence();

    [GeneratedRegex(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$")]
    private static partial Regex Rule();

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\s{0,3}>\s+")]
    private static partial Regex Quote();

    [GeneratedRegex(@"^(\s*)[-*+]\s+(?=\S)")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"!?\[(?<label>[^\]\n]*)\]\((?<url>https?://[^\s)]+)\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"\*\*(?<body>[^*\n]+?)\*\*")]
    private static partial Regex Bold();

    [GeneratedRegex(@"`(?<body>[^`\n]+)`")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExtraBlankLines();
}
