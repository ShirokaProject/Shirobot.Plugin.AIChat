using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ShiroBot.AiChatPlugin.OpenAi;

namespace ShiroBot.AiChatPlugin.Tools;

/// <summary>
/// 历史只保存每轮最后的文字回复，工具调用本身不会进入上下文。模型只看到“已提交生图”“已设好提醒”这样的文字时，
/// 会以为说出这句话就算完成，转而模仿文字、不再调用工具。这里负责两件事：
/// 把本轮实际执行的工具记成程序记录写进历史；回复声称做了某事却没有调用对应工具时，给出纠正提示让模型重来一次。
/// </summary>
internal static partial class ToolCallRecord
{
    internal const string Prefix = "[程序记录：本轮实际执行的工具调用，由程序自动添加，不是回复文字；";

    /// <summary>从工具循环追加的消息里整理出本轮执行过的工具；没有调用工具时返回空字符串。</summary>
    internal static string Describe(IReadOnlyList<OpenAiMessage> transcript)
    {
        var results = transcript.Where(m => m.Role == "tool" && m.ToolCallId is not null)
            .GroupBy(m => m.ToolCallId!).ToDictionary(g => g.Key, g => g.Last().Content as string ?? "");
        var lines = new List<string>();
        foreach (var call in transcript.Where(m => m.Role == "assistant").SelectMany(m => m.ToolCalls ?? []))
        {
            var name = call.Function?.Name ?? "";
            if (name.Length == 0) continue;
            var result = call.Id is not null && results.TryGetValue(call.Id, out var text) ? text : "";
            lines.Add($"{name}（{Summarize(name, call.Function?.Arguments)}）→ {Clip(result, 80)}");
        }
        return lines.Count == 0 ? "" : Prefix + string.Join("；", lines) + "]";
    }

    private static string Summarize(string name, string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return "";
        try
        {
            using var doc = JsonDocument.Parse(arguments);
            // 生图提示词、提醒内容、记忆条目是最能说明这次做了什么的字段。
            foreach (var key in new[] { "prompt", "text", "query", "url", "action" })
                if (doc.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                    return Clip(value.GetString() ?? "", 100);
        }
        catch (JsonException) { }
        return Clip(arguments, 100);
    }

    private static string Clip(string text, int max)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    // 只覆盖会产生动作、且模型常常只说不做的工具；匹配宽松一些，误判的代价只是多请求一次。
    [GeneratedRegex(@"(提交|交给|交上|排队|安排).{0,10}(生图|画|图)|(生图|画图|出图).{0,8}(排队|提交|中|好了)|画好(会|后|了)|完成后.{0,8}(单独)?发|图片?(生成中|在生成)|正在(画|生成)", RegexOptions.CultureInvariant)]
    private static partial Regex ImageClaim();

    [GeneratedRegex(@"(设|定|预约|安排|创建)(好|了).{0,8}(提醒|定时|闹钟)|(提醒|定时).{0,6}(设好|已设|已创建|已安排|安排好)|到(时候|点)(会|就)?(提醒|叫|@)你", RegexOptions.CultureInvariant)]
    private static partial Regex TimerClaim();

    [GeneratedRegex(@"(记|写|存)(进|到|入)(了)?(记忆本|小本本)|记忆本.{0,6}(记下|记好|记住|更新|删)|已经?(记住|记下)了?", RegexOptions.CultureInvariant)]
    private static partial Regex MemoryClaim();

    private static readonly (string Tool, Func<Regex> Pattern, string Action)[] Claims =
    [
        ("imagegen", ImageClaim, "提交了生图"),
        ("timer_tool", TimerClaim, "设置了提醒"),
        ("memory_note", MemoryClaim, "改了记忆本")
    ];

    /// <summary>
    /// 回复声称执行了某个可用工具，但本轮并没有调用它时，返回纠正提示；否则返回 null。
    /// </summary>
    internal static string? CheckClaims(string reply, IReadOnlySet<string> available, IReadOnlySet<string> called)
    {
        foreach (var (tool, pattern, action) in Claims)
        {
            if (!available.Contains(tool) || called.Contains(tool) || !pattern().IsMatch(reply)) continue;
            return $"你刚才的回复说已经{action}，但本轮没有调用 {tool}，实际上什么都没有执行。" +
                $"如果用户确实要求这件事，现在调用 {tool}；如果不需要，就重新回复，不要声称已经完成。不要提到这次更正。";
        }
        return null;
    }
}
