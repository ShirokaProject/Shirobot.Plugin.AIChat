using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ShiroBot.Model.QQ;

namespace ShiroBot.AiChatPlugin.Resources;

/// <summary>
/// 把 QQ 小程序/XML 分享卡片压成一行可读文字（来源、标题、简介、链接），给模型看。
/// 只挑常见字段，格式不认识时退回占位文本；卡片内容由用户发出，和普通文字一样视为不可信。
/// </summary>
internal static class QqCardText
{
    internal const int MaxChars = 300;
    private static readonly string[] UrlKeys = ["qqdocurl", "jumpUrl", "jumpurl", "url", "musicUrl"];

    public static string Describe(QIncomingLightApp app, bool brief = false)
    {
        var card = Parse(app.JsonPayload);
        var source = Clean(card.Source) is { Length: > 0 } s ? s : Clean(app.AppName);
        return Format("小程序卡片", source, card.Title, card.Desc, card.Url, brief);
    }

    public static string Describe(QIncomingXml xml, bool brief = false)
    {
        string? title = null, desc = null, url = null, source = null;
        try
        {
            var root = XDocument.Parse(xml.XmlPayload).Root;
            if (root is not null)
            {
                url = (string?)root.Attribute("url");
                title = root.Descendants("title").Select(e => e.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                desc = root.Descendants("summary").Select(e => e.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                source = (string?)root.Descendants("source").FirstOrDefault()?.Attribute("name");
                if (string.IsNullOrWhiteSpace(title)) title = (string?)root.Attribute("brief");
            }
        }
        catch (Exception) { }
        return Format("XML卡片", source, title, desc, url, brief);
    }

    internal static (string? Source, string? Title, string? Desc, string? Url) Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return default;
            string? title = null, desc = null, url = null, source = null, tag = null;
            var miniApp = false;
            if (root.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
            {
                // meta 下一般只有一个对象：detail_1（小程序）、news（链接分享）、music、contact 等。
                foreach (var entry in meta.EnumerateObject())
                {
                    if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                    var m = entry.Value;
                    miniApp = entry.Name.StartsWith("detail_", StringComparison.Ordinal);
                    title = Str(m, "title");
                    desc = Str(m, "desc");
                    tag = Str(m, "tag");
                    url = UrlKeys.Select(k => Str(m, k)).FirstOrDefault(IsWebUrl);
                    if (m.TryGetProperty("host", out var host) && host.ValueKind == JsonValueKind.Object) source ??= Str(host, "nick");
                    break;
                }
            }
            // detail_1 的 title 是小程序名（如“哔哩哔哩”），desc 才是内容标题。
            if (miniApp && title is not null && desc is not null) (source, title, desc) = (title, desc, null);
            source ??= tag;
            if (title is null && Str(root, "prompt") is { } prompt) title = prompt;
            return (source, title, desc, url);
        }
        catch (JsonException) { return default; }
    }

    private static string Format(string kind, string? source, string? title, string? desc, string? url, bool brief)
    {
        source = Clean(source); title = Clean(title); desc = Clean(desc); url = IsWebUrl(url) ? url!.Trim() : null;
        if (title is not null && desc is not null && (desc == title || title.Contains(desc))) desc = null;
        if (source is not null && title is not null && title.Contains(source)) source = null;
        var head = source is null ? kind : $"{kind} {source}";
        if (brief) return title is null ? $"[{head}]" : $"[{head}: {Truncate(title, 60)}]";
        if (title is null && desc is null && url is null) return $"[{head}]";
        var body = new StringBuilder();
        if (title is not null) body.Append(title);
        if (desc is not null) body.Append(body.Length > 0 ? " - " : "").Append(desc);
        var text = Truncate(body.ToString(), MaxChars - Math.Min(url?.Length ?? 0, 200));
        if (url is not null) text += (text.Length > 0 ? " " : "") + Truncate(url, 200);
        return $"[{head}: {text}]";
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool IsWebUrl(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u) && u.Scheme is "http" or "https";

    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) { if (!space && sb.Length > 0) sb.Append(' '); space = true; continue; }
            // 方括号会和程序标注混淆，换成全角。
            sb.Append(c switch { '[' => '［', ']' => '］', _ => c });
            space = false;
        }
        var result = sb.ToString().Trim();
        return result.Length == 0 ? null : result;
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : string.Concat(text.EnumerateRunes().Take(Math.Max(1, max - 1))) + "…";
}
