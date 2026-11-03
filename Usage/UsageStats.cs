using System.Text.Json;
using ShiroBot.SDK.Abstractions;

namespace ShiroBot.AiChatPlugin.Usage;

/// <summary>发送给上游的一次请求：用量、来源，以及判断前缀缓存为何失效所需的请求特征。</summary>
/// <param name="CommonMessages">和同一会话上一次请求开头相同的消息条数。</param>
/// <param name="Change">和上一次请求相比前缀怎样变化，例如“仅追加”“system 变化”“工具定义变化”“第 5 条起变化”。</param>
internal sealed record UsageRecord(
    DateTimeOffset At, string Model, string Source, string Purpose,
    int PromptTokens, int CompletionTokens, int? CachedTokens,
    int Messages, int Images, int Tools, int CommonMessages = 0, string Change = "",
    bool UsageReported = true, long? LocalInputBytes = null, long? LocalReusableBytes = null);

/// <summary>一组请求的合计。缓存命中率只按返回了缓存信息的请求计算。</summary>
internal sealed class UsageTotals
{
    public long Requests { get; set; }
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long CachedTokens { get; set; }
    /// <summary>返回了缓存信息的请求的输入 token 合计，是命中率的分母。</summary>
    public long CacheKnownPromptTokens { get; set; }

    public long UsageRequests { get; set; }
    public long LocalRequests { get; set; }
    public long LocalInputBytes { get; set; }
    public long LocalReusableBytes { get; set; }
    public long ComparedLocalBytes { get; set; }
    public long ComparedReusableBytes { get; set; }
    public long ComparedPromptTokens { get; set; }
    public long ComparedCachedTokens { get; set; }

    public double? LocalRate => LocalInputBytes == 0 ? null : (double)LocalReusableBytes / LocalInputBytes;
    public double? ComparedLocalRate => ComparedLocalBytes == 0 ? null : (double)ComparedReusableBytes / ComparedLocalBytes;
    public double? ComparedCacheRate => ComparedPromptTokens == 0 ? null : (double)ComparedCachedTokens / ComparedPromptTokens;

    public double? CacheRate => CacheKnownPromptTokens == 0 ? null : (double)CachedTokens / CacheKnownPromptTokens;

    public void Add(UsageTotals other)
    {
        Requests += other.Requests;
        PromptTokens += other.PromptTokens;
        CompletionTokens += other.CompletionTokens;
        CachedTokens += other.CachedTokens;
        CacheKnownPromptTokens += other.CacheKnownPromptTokens;
        UsageRequests += other.UsageRequests;
        LocalRequests += other.LocalRequests;
        LocalInputBytes += other.LocalInputBytes;
        LocalReusableBytes += other.LocalReusableBytes;
        ComparedLocalBytes += other.ComparedLocalBytes;
        ComparedReusableBytes += other.ComparedReusableBytes;
        ComparedPromptTokens += other.ComparedPromptTokens;
        ComparedCachedTokens += other.ComparedCachedTokens;
    }
}

/// <summary>
/// 按天、模型、来源和用途累加 token 用量，另外保留最近的请求明细，持久化到插件目录下的 JSON 文件。
/// 在发送侧（<see cref="OpenAi.OpenAiClient"/>）记录，所以对话、工具轮次、历史压缩、网页摘要都会计入。
/// </summary>
internal sealed class UsageStats
{
    private const int KeepDays = 31;
    private const int RecentCapacity = 300;
    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Day, string Model, string Source, string Purpose), UsageTotals> _buckets = new();
    private readonly LinkedList<UsageRecord> _recent = new();
    // 每个会话最近一次请求的前缀摘要，只在内存里，用来判断下一次请求的前缀是否被改写。
    private readonly Dictionary<string, (RequestShape? Shape, PrefixSnapshot? Prefix)> _lastShapes = new(StringComparer.Ordinal);
    // 返回过缓存字段的上游模型（端点摘要加模型）。它们之后缺少缓存字段视为 0 命中；随统计文件保存，清空统计时保留。
    private readonly HashSet<string> _cacheReporters = new(StringComparer.Ordinal);
    private bool _dirty;

    public UsageStats(string path) => _path = path;

    /// <summary>
    /// 本次请求计入统计的缓存命中 Token。有的上游只在命中时才返回缓存字段：返回过一次之后，缺少字段就按 0 命中计；
    /// 从没返回过缓存字段的上游仍视为未知，不计入命中率。
    /// </summary>
    public int? ResolveCachedTokens(string upstreamModel, int? cached)
    {
        lock (_gate)
        {
            if (cached is not null)
            {
                if (_cacheReporters.Add(upstreamModel)) _dirty = true;
                return cached;
            }
            return _cacheReporters.Contains(upstreamModel) ? 0 : null;
        }
    }

    /// <param name="compareKey">同一个 key 的相邻请求互相比较前缀；独立的请求（摘要、压缩）传 null。</param>
    public void Record(UsageRecord record, RequestShape? shape = null, string? compareKey = null, PrefixSnapshot? prefix = null)
    {
        lock (_gate)
        {
            var previous = compareKey is null ? default : _lastShapes.GetValueOrDefault(compareKey);
            if (shape is not null && compareKey is not null)
            {
                var (common, change) = shape.CompareWith(previous.Shape);
                record = record with { CommonMessages = common, Change = change };
            }
            if (prefix is not null)
                record = record with { LocalInputBytes = prefix.Bytes, LocalReusableBytes = prefix.CommonBytes(previous.Prefix) };
            if (compareKey is not null)
            {
                // 只保存最近 256 个比较范围的摘要，不保存正文；重启后重新建立基线。
                _lastShapes.Remove(compareKey);
                if (_lastShapes.Count >= 256) _lastShapes.Remove(_lastShapes.Keys.First());
                _lastShapes[compareKey] = (shape, prefix);
            }
            var key = (Day(record.At), record.Model, record.Source, record.Purpose);
            if (!_buckets.TryGetValue(key, out var totals)) _buckets[key] = totals = new UsageTotals();
            totals.Requests++;
            if (record.UsageReported) totals.UsageRequests++;
            if (record.LocalInputBytes is { } input)
            {
                totals.LocalRequests++;
                totals.LocalInputBytes += input;
                totals.LocalReusableBytes += record.LocalReusableBytes ?? 0;
                if (record.CachedTokens is { } comparableCached)
                {
                    totals.ComparedLocalBytes += input;
                    totals.ComparedReusableBytes += record.LocalReusableBytes ?? 0;
                    totals.ComparedPromptTokens += record.PromptTokens;
                    totals.ComparedCachedTokens += comparableCached;
                }
            }
            totals.PromptTokens += record.PromptTokens;
            totals.CompletionTokens += record.CompletionTokens;
            if (record.CachedTokens is { } cached)
            {
                totals.CachedTokens += cached;
                totals.CacheKnownPromptTokens += record.PromptTokens;
            }
            _recent.AddFirst(record);
            while (_recent.Count > RecentCapacity) _recent.RemoveLast();
            _dirty = true;
        }
    }

    /// <summary>从 <paramref name="from"/>（本地日期，含当天）起按 <paramref name="group"/> 分组的合计，按输入 token 从多到少排序。</summary>
    public IReadOnlyList<(string Name, UsageTotals Totals)> GroupBy(DateOnly from, Func<(string Day, string Model, string Source, string Purpose), string> group)
    {
        var start = from.ToString("yyyy-MM-dd");
        lock (_gate)
        {
            return _buckets
                .Where(pair => string.CompareOrdinal(pair.Key.Day, start) >= 0)
                .GroupBy(pair => group(pair.Key))
                .Select(items =>
                {
                    var totals = new UsageTotals();
                    foreach (var item in items) totals.Add(item.Value);
                    return (items.Key, totals);
                })
                .OrderByDescending(item => item.totals.PromptTokens)
                .ToArray();
        }
    }

    public UsageTotals Total(DateOnly from)
    {
        var totals = new UsageTotals();
        foreach (var (_, item) in GroupBy(from, _ => "")) totals.Add(item);
        return totals;
    }

    public IReadOnlyList<UsageRecord> Recent(int count, string? source = null, DateOnly? from = null)
    {
        lock (_gate)
            return _recent.Where(record => (source is null || record.Source == source) &&
                (from is null || DateOnly.FromDateTime(record.At.LocalDateTime) >= from)).Take(count).ToArray();
    }

    public void Reset()
    {
        lock (_gate)
        {
            _buckets.Clear();
            _recent.Clear();
            _lastShapes.Clear();
            _dirty = true;
        }
    }

    public async Task LoadAsync()
    {
        if (!File.Exists(_path)) return;
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllBytesAsync(_path).ConfigureAwait(false));
            lock (_gate)
            {
                foreach (var item in Items(doc.RootElement, "buckets"))
                {
                    var key = (Str(item, "day"), Str(item, "model"), Str(item, "source"), Str(item, "purpose"));
                    _buckets[key] = new UsageTotals
                    {
                        Requests = Long(item, "requests"), PromptTokens = Long(item, "prompt"), CompletionTokens = Long(item, "completion"),
                        CachedTokens = Long(item, "cached"), CacheKnownPromptTokens = Long(item, "cache_known_prompt"),
                        UsageRequests = item.TryGetProperty("usage_requests", out _) ? Long(item, "usage_requests") : Long(item, "requests"),
                        LocalRequests = Long(item, "local_requests"),
                        LocalInputBytes = Long(item, "local_input_bytes"),
                        LocalReusableBytes = Long(item, "local_reusable_bytes"),
                        ComparedLocalBytes = Long(item, "compared_local_bytes"),
                        ComparedReusableBytes = Long(item, "compared_reusable_bytes"),
                        ComparedPromptTokens = Long(item, "compared_prompt_tokens"),
                        ComparedCachedTokens = Long(item, "compared_cached_tokens")
                    };
                }
                foreach (var item in Items(doc.RootElement, "cache_reporters"))
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } reporter) _cacheReporters.Add(reporter);
                foreach (var item in Items(doc.RootElement, "recent"))
                {
                    _recent.AddLast(new UsageRecord(
                        DateTimeOffset.FromUnixTimeMilliseconds(Long(item, "at")).ToLocalTime(), Str(item, "model"), Str(item, "source"),
                        Str(item, "purpose"), (int)Long(item, "prompt"), (int)Long(item, "completion"),
                        item.TryGetProperty("cached", out var cached) && cached.ValueKind == JsonValueKind.Number ? cached.GetInt32() : null,
                        (int)Long(item, "messages"), (int)Long(item, "images"), (int)Long(item, "tools"),
                        (int)Long(item, "common_messages"), Str(item, "change"),
                        !item.TryGetProperty("usage_reported", out var reported) || reported.ValueKind != JsonValueKind.False,
                        OptionalLong(item, "local_input_bytes"), OptionalLong(item, "local_reusable_bytes")));
                }
                Prune();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            BotLog.Warning($"[AiChat] 读取用量统计失败，重新开始统计: {ex.Message}");
        }
    }

    /// <summary>有变化时写入文件；先写临时文件再替换，中途退出不会留下半个文件。</summary>
    public async Task SaveAsync()
    {
        byte[] json;
        lock (_gate)
        {
            if (!_dirty) return;
            Prune();
            json = Serialize();
            _dirty = false;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            await File.WriteAllBytesAsync(temp, json).ConfigureAwait(false);
            File.Move(temp, _path, overwrite: true);
        }
        catch (IOException ex)
        {
            lock (_gate) _dirty = true;
            BotLog.Warning($"[AiChat] 保存用量统计失败: {ex.Message}");
        }
    }

    private void Prune()
    {
        var oldest = Day(DateTimeOffset.Now.AddDays(-KeepDays));
        foreach (var key in _buckets.Keys.Where(key => string.CompareOrdinal(key.Day, oldest) < 0).ToArray())
            _buckets.Remove(key);
    }

    private byte[] Serialize()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 2);
            writer.WriteStartArray("buckets");
            foreach (var ((day, model, source, purpose), totals) in _buckets)
            {
                writer.WriteStartObject();
                writer.WriteString("day", day);
                writer.WriteString("model", model);
                writer.WriteString("source", source);
                writer.WriteString("purpose", purpose);
                writer.WriteNumber("requests", totals.Requests);
                writer.WriteNumber("prompt", totals.PromptTokens);
                writer.WriteNumber("completion", totals.CompletionTokens);
                writer.WriteNumber("cached", totals.CachedTokens);
                writer.WriteNumber("cache_known_prompt", totals.CacheKnownPromptTokens);
                writer.WriteNumber("usage_requests", totals.UsageRequests);
                writer.WriteNumber("local_requests", totals.LocalRequests);
                writer.WriteNumber("local_input_bytes", totals.LocalInputBytes);
                writer.WriteNumber("local_reusable_bytes", totals.LocalReusableBytes);
                writer.WriteNumber("compared_local_bytes", totals.ComparedLocalBytes);
                writer.WriteNumber("compared_reusable_bytes", totals.ComparedReusableBytes);
                writer.WriteNumber("compared_prompt_tokens", totals.ComparedPromptTokens);
                writer.WriteNumber("compared_cached_tokens", totals.ComparedCachedTokens);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("cache_reporters");
            foreach (var reporter in _cacheReporters) writer.WriteStringValue(reporter);
            writer.WriteEndArray();
            writer.WriteStartArray("recent");
            foreach (var record in _recent)
            {
                writer.WriteStartObject();
                writer.WriteNumber("at", record.At.ToUnixTimeMilliseconds());
                writer.WriteString("model", record.Model);
                writer.WriteString("source", record.Source);
                writer.WriteString("purpose", record.Purpose);
                writer.WriteNumber("prompt", record.PromptTokens);
                writer.WriteNumber("completion", record.CompletionTokens);
                if (record.CachedTokens is { } cached) writer.WriteNumber("cached", cached);
                writer.WriteNumber("messages", record.Messages);
                writer.WriteNumber("images", record.Images);
                writer.WriteNumber("tools", record.Tools);
                writer.WriteNumber("common_messages", record.CommonMessages);
                writer.WriteString("change", record.Change);
                writer.WriteBoolean("usage_reported", record.UsageReported);
                if (record.LocalInputBytes is { } input) writer.WriteNumber("local_input_bytes", input);
                if (record.LocalReusableBytes is { } reusable) writer.WriteNumber("local_reusable_bytes", reusable);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private static string Day(DateTimeOffset at) => at.ToLocalTime().ToString("yyyy-MM-dd");

    private static IEnumerable<JsonElement> Items(JsonElement root, string name) =>
        root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : [];

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static long? OptionalLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;

    private static long Long(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0;
}
