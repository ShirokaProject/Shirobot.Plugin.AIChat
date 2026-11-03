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
    private static string UsageSource(ConversationKey key) => key.InstanceId + " / " + (key.Scene == ChatScene.Group
        ? key.UserId is "" or "0" ? $"群 {key.PeerId}" : $"群 {key.PeerId} / {key.UserId}"
        : $"私聊 {key.PeerId}");

    private static string UsageSource(string instanceId, ChannelType type, string channelId) =>
        instanceId + " / " + (type == ChannelType.Direct ? $"私聊 {channelId}" : $"群 {channelId}");

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
            description: "本地前缀复用、上游缓存命中和 Token 用量");

    private Task<UiNode> RenderUsagePageAsync(DashboardContext page, CancellationToken ct)
    {
        var range = page.Filter("range") ?? "1";
        var days = int.TryParse(range, out var parsed) && parsed is >= 1 and <= 31 ? parsed : 1;
        var from = DateOnly.FromDateTime(DateTime.Now).AddDays(1 - days);
        var total = _usage.Total(from);

        var source = page.Filter("source");
        var recent = _usage.Recent(RecentRequestRows, string.IsNullOrEmpty(source) ? null : source, from);
        var sources = _usage.GroupBy(from, key => key.Source);

        return Task.FromResult(Ui.Column(
            Ui.Filters(Ui.Select("range", "时间", [("1", "今天"), ("7", "最近 7 天"), ("30", "最近 30 天")])),
            Ui.Row(
                Ui.Metric("请求数", total.Requests.ToString("N0")),
                Ui.Metric("上游输入 Token", total.UsageRequests == 0 ? "—" : total.PromptTokens.ToString("N0")),
                Ui.Metric("上游输出 Token", total.UsageRequests == 0 ? "—" : total.CompletionTokens.ToString("N0")),
                Ui.Metric("返回用量", $"{total.UsageRequests:N0}/{total.Requests:N0} 次")),
            Ui.Row(
                Ui.Metric("本地前缀复用率", Rate(total.LocalRate), $"覆盖 {total.LocalRequests:N0} 次请求；按 UTF-8 字节计算"),
                Ui.Metric("上游缓存命中率", Rate(total.CacheRate), CacheHint(total)),
                Ui.Metric("本地复用量", Bytes(total.LocalReusableBytes), $"请求内容共 {Bytes(total.LocalInputBytes)}")),
            Ui.Text("本地在实际发送时比较同一实例、会话和上游模型的工具定义与消息（含图片），统计完整工具/消息边界前相同的 UTF-8 字节。它确认前缀复用，不代表服务商实际命中缓存；上游命中率按返回的缓存 Token 计算。首次、重启后及独立用途请求从 0 开始。"),
            Ui.Text(total.ComparedPromptTokens > 0
                ? $"同时具备两项数据的请求：本地复用 {Rate(total.ComparedLocalRate)}（字节），上游命中 {Rate(total.ComparedCacheRate)}（Token）。"
                : "还没有同时具备本地前缀和上游缓存信息的请求；上游未返回时仍会记录本地复用。"),
            Ui.Empty("还没有完成的模型请求。", total.Requests == 0),
            Ui.Tabs("view",
                Ui.Tab("sources", "按会话", TotalsTable(sources, "会话")),
                Ui.Tab("purposes", "按用途", TotalsTable(_usage.GroupBy(from, key => PurposeLabel(key.Purpose)), "用途")),
                Ui.Tab("models", "按模型", TotalsTable(_usage.GroupBy(from, key => key.Model), "模型")),
                Ui.Tab("days", "按天", TotalsTable(_usage.GroupBy(from, key => key.Day).OrderByDescending(item => item.Name).ToArray(), "日期")),
                Ui.Tab("recent", "最近请求",
                    Ui.Text("“前缀变化”和同一会话的上一次请求比较：正常的下一轮是“仅追加”；“system 变化”“工具定义变化”或“第 N 条起变化”说明前缀被改写，可结合本地复用量排查前缀被改写的位置。"),
                    Ui.Filters(Ui.Select("source", "会话", sources.Select(item => (item.Name, item.Name)))),
                    Ui.Table(recent, record => record.At.ToUnixTimeMilliseconds() + record.Source + record.Purpose)
                        .Column("时间", record => record.At.ToString("MM-dd HH:mm:ss"))
                        .Column("会话", record => record.Source)
                        .Column("用途", record => PurposeLabel(record.Purpose))
                        .Column("模型", record => record.Model)
                        .Column("上游输入", record => record.UsageReported ? record.PromptTokens.ToString("N0") : "未返回")
                        .Column("上游命中", record => record.CachedTokens is { } cached
                            ? $"{cached:N0}（{Rate(record.PromptTokens == 0 ? null : (double)cached / record.PromptTokens)}）" : "未返回")
                        .Column("本地复用", record => record.LocalInputBytes is { } input
                            ? $"{Rate(input == 0 ? null : (double)(record.LocalReusableBytes ?? 0) / input)}（{Bytes(record.LocalReusableBytes ?? 0)}/{Bytes(input)}）" : "未采集")
                        .Column("上游输出", record => record.UsageReported ? record.CompletionTokens.ToString("N0") : "未返回")
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
            .Column("上游输入 Token", row => row.Totals.UsageRequests == 0 ? "—" : row.Totals.PromptTokens.ToString("N0"))
            .Column("本地复用率", row => Rate(row.Totals.LocalRate))
            .Column("上游命中率", row => Rate(row.Totals.CacheRate))
            .Column("返回用量/请求", row => $"{row.Totals.UsageRequests:N0}/{row.Totals.Requests:N0}")
            .Column("上游输出 Token", row => row.Totals.UsageRequests == 0 ? "—" : row.Totals.CompletionTokens.ToString("N0"));

    private static string Bytes(long bytes) => bytes < 1024 ? $"{bytes:N0} B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:N1} KiB" : $"{bytes / (1024d * 1024):N1} MiB";

    private static string Rate(double? rate) => rate is { } value ? value.ToString("P1") : "—";

    private static string CacheHint(UsageTotals total) =>
        total.CacheKnownPromptTokens == 0 ? "上游未返回可计算的缓存用量"
            : $"按返回缓存信息的 {total.CacheKnownPromptTokens:N0} 输入 Token 计算";
}
