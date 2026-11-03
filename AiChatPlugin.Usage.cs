using ShiroBot.AiChatPlugin.Conversation;
using ShiroBot.AiChatPlugin.Usage;
using ShiroBot.SDK.Dashboard;
using ShiroBot.SDK.Models;

namespace ShiroBot.AiChatPlugin;

/// <summary>用量统计：请求来源的标记和 Dashboard 上的统计页面。</summary>
public sealed partial class AiChatPlugin
{
    private const int RecentRequestRows = 100;

    /// <summary>统计里的会话名：群或私聊对象；群里按用户独立对话时带上用户。</summary>
    private static string UsageSource(ConversationKey key) => key.Scene == ChatScene.Group
        ? key.UserId is "" or "0" ? $"群 {key.PeerId}" : $"群 {key.PeerId} / {key.UserId}"
        : $"私聊 {key.PeerId}";

    private static string UsageSource(ChannelType type, string channelId) =>
        type == ChannelType.Direct ? $"私聊 {channelId}" : $"群 {channelId}";

    private static string PurposeLabel(string purpose) => purpose switch
    {
        "chat" => "对话",
        "proactive" => "主动插话",
        "timer" => "定时提醒",
        "compact" => "历史压缩",
        "page_summary" => "网页摘要",
        "memory_review" => "记忆整理",
        "plugin_api" => "其他插件",
        _ => purpose
    };

    private void RegisterUsagePage() =>
        Context.Dashboard.AddPage("usage", "用量统计", RenderUsagePageAsync, icon: "chart",
            description: "token 用量、缓存命中率和最近请求的前缀变化");

    private Task<UiNode> RenderUsagePageAsync(DashboardContext page, CancellationToken ct)
    {
        var range = page.Filter("range") ?? "1";
        var days = int.TryParse(range, out var parsed) && parsed is >= 1 and <= 31 ? parsed : 1;
        var from = DateOnly.FromDateTime(DateTime.Now).AddDays(1 - days);
        var total = _usage.Total(from);

        var source = page.Filter("source");
        var recent = _usage.Recent(RecentRequestRows, string.IsNullOrEmpty(source) ? null : source);
        var sources = _usage.GroupBy(from, key => key.Source);

        return Task.FromResult(Ui.Column(
            Ui.Filters(Ui.Select("range", "时间", [("1", "今天"), ("7", "最近 7 天"), ("30", "最近 30 天")])),
            Ui.Row(
                Ui.Metric("请求数", total.Requests.ToString("N0")),
                Ui.Metric("输入 Token", total.PromptTokens.ToString("N0")),
                Ui.Metric("缓存命中率", Rate(total.CacheRate), CacheHint(total)),
                Ui.Metric("输出 Token", total.CompletionTokens.ToString("N0"))),
            Ui.Empty("还没有记录到用量。上游需要返回 usage；流式请求需要开启 default.stream_usage。", total.Requests == 0),
            Ui.Tabs("view",
                Ui.Tab("sources", "按会话", TotalsTable(sources, "会话")),
                Ui.Tab("purposes", "按用途", TotalsTable(_usage.GroupBy(from, key => PurposeLabel(key.Purpose)), "用途")),
                Ui.Tab("models", "按模型", TotalsTable(_usage.GroupBy(from, key => key.Model), "模型")),
                Ui.Tab("days", "按天", TotalsTable(_usage.GroupBy(from, key => key.Day).OrderByDescending(item => item.Name).ToArray(), "日期")),
                Ui.Tab("recent", "最近请求",
                    Ui.Text("“前缀变化”和同一会话的上一次请求比较：正常的下一轮是“仅追加”；“system 变化”“工具定义变化”或“第 N 条起变化”说明前缀被改写，之后的内容都无法命中缓存。"),
                    Ui.Filters(Ui.Select("source", "会话", sources.Select(item => (item.Name, item.Name)))),
                    Ui.Table(recent, record => record.At.ToUnixTimeMilliseconds() + record.Source + record.Purpose)
                        .Column("时间", record => record.At.ToString("MM-dd HH:mm:ss"))
                        .Column("会话", record => record.Source)
                        .Column("用途", record => PurposeLabel(record.Purpose))
                        .Column("模型", record => record.Model)
                        .Column("输入", record => record.PromptTokens.ToString("N0"))
                        .Column("命中", record => record.CachedTokens is { } cached
                            ? $"{cached:N0}（{Rate(record.PromptTokens == 0 ? null : (double)cached / record.PromptTokens)}）" : "未返回")
                        .Column("输出", record => record.CompletionTokens.ToString("N0"))
                        .Column("消息/图片/工具", record => $"{record.Messages}/{record.Images}/{record.Tools}")
                        .Column("前缀变化", record => record.Change),
                    Ui.Empty("没有请求记录。", recent.Count == 0))),
            Ui.Button("reset", "清空统计", (_, _) =>
            {
                _usage.Reset();
                return Task.FromResult(DashboardResult.Done("已清空用量统计。"));
            }, confirm: "确定清空全部用量统计和请求记录？", danger: true)));
    }

    private static UiNode TotalsTable(IReadOnlyList<(string Name, UsageTotals Totals)> rows, string title) =>
        Ui.Table(rows, row => row.Name)
            .Column(title, row => row.Name)
            .Column("请求", row => row.Totals.Requests.ToString("N0"))
            .Column("输入 Token", row => row.Totals.PromptTokens.ToString("N0"))
            .Column("命中 Token", row => row.Totals.CachedTokens.ToString("N0"))
            .Column("命中率", row => Rate(row.Totals.CacheRate))
            .Column("输出 Token", row => row.Totals.CompletionTokens.ToString("N0"));

    private static string Rate(double? rate) => rate is { } value ? value.ToString("P1") : "—";

    private static string? CacheHint(UsageTotals total) =>
        total.CacheKnownPromptTokens < total.PromptTokens
            ? $"按返回了缓存信息的 {total.CacheKnownPromptTokens:N0} 输入 Token 计算"
            : null;
}
