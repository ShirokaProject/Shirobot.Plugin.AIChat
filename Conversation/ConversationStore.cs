using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ShiroBot.SDK.Abstractions;

namespace ShiroBot.AiChatPlugin.Conversation;

/// <summary>
/// 会话仓库。不在内存中缓存 turns，每次请求从磁盘加载、处理完立即释放。
/// 只保留轻量的 per-key 锁确保同一会话串行。SharedPrompt/SharedModel 持久化到 metadata 文件。
/// </summary>
internal sealed class ConversationStore
{
    /// <summary>会话文件中保存历史摘要的特殊行，始终位于第一行。</summary>
    internal const string SummaryRole = "summary";
    /// <summary>会话文件中保存记忆本快照的特殊行，位于摘要之后。</summary>
    internal const string MemorySnapshotRole = "memory_snapshot";
    private const string LegacySummaryPrefix = "[对话摘要] ";

    private readonly ConcurrentDictionary<ConversationKey, SemaphoreSlim> _locks = new();
    /// <summary>只保护 metadata 文件的读改写，持有时间极短，与会话锁无关。</summary>
    private readonly Lock _metadataGate = new();
    private readonly string _conversationsDir;
    private readonly bool _persist;
    private readonly JsonSerializerOptions _jsonOptions;

    public ConversationStore(string pluginRootDir, bool persist)
    {
        _conversationsDir = Path.Combine(pluginRootDir, "data", "conversations");
        _persist = persist;
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            TypeInfoResolver = PluginJson.Resolver,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        if (_persist)
        {
            Directory.CreateDirectory(_conversationsDir);
        }
    }

    /// <summary>
    /// 从磁盘加载会话状态（turns + metadata），调用方处理完后应调用 <see cref="ReleaseAsync"/>。
    /// 返回的 state 只在当前请求生命周期内有效。
    /// </summary>
    public async Task<ConversationState> AcquireAsync(ConversationKey key, CancellationToken ct = default)
    {
        var lk = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await lk.WaitAsync(ct).ConfigureAwait(false);

        var state = new ConversationState(key);
        if (_persist)
        {
            LoadTurnsFromDisk(state);
            LoadMetadataFromDisk(state);
        }
        return state;
    }

    /// <summary>
    /// 释放会话锁。调用方在处理完请求后必须调用。
    /// </summary>
    public void Release(ConversationKey key)
    {
        if (_locks.TryGetValue(key, out var lk))
        {
            lk.Release();
        }
    }

    /// <summary>
    /// 追加一对 user+assistant turn。压缩成功时已重写历史，否则追加到磁盘。
    /// 调用方必须已通过 AcquireAsync 持有锁。
    /// </summary>
    public async Task AppendTurnsAsync(ConversationState state, ChatTurn userTurn, ChatTurn assistantTurn,
        Func<Task<bool>>? compact = null)
    {
        var storedUserTurn = StripTransientPayloads(userTurn);
        state.Turns.Add(storedUserTurn);
        state.Turns.Add(assistantTurn);

        var compacted = false;
        try
        {
            compacted = compact is not null && await compact().ConfigureAwait(false);
        }
        finally
        {
            if (_persist && !compacted)
                await AppendToDiskAsync(state.Key, storedUserTurn, assistantTurn).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 保存共享会话的 metadata（SharedPrompt/SharedModel）到磁盘。
    /// </summary>
    /// <summary>只读取 metadata（共享模型、共享提示词、清空时间），不加载历史，也不占会话锁。</summary>
    public ConversationState LoadMetadata(ConversationKey key)
    {
        var state = new ConversationState(key);
        if (_persist)
            lock (_metadataGate) LoadMetadataFromDisk(state);
        return state;
    }

    /// <summary>
    /// 保存共享模型和共享提示词。清空时间以磁盘上的为准，避免拿着旧状态覆盖期间发生的 #clear。
    /// </summary>
    public void SaveMetadata(ConversationState state)
    {
        if (!_persist) return;
        lock (_metadataGate)
        {
            var current = new ConversationState(state.Key);
            LoadMetadataFromDisk(current);
            WriteMetadata(state.Key, state.SharedPrompt, state.SharedModel, current.ClearedAt);
        }
    }

    private void WriteMetadata(ConversationKey key, string? sharedPrompt, string? sharedModel, long? clearedAt)
    {

        var path = GetMetadataFile(key);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var meta = new ConversationMetadata
            {
                SharedPrompt = sharedPrompt,
                SharedModel = sharedModel,
                ClearedAt = clearedAt
            };
            var json = PluginJson.Serialize(meta, _jsonOptions);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, json, Encoding.UTF8);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex)
        {
            BotLog.Warning($"[AiChat] 保存会话 metadata 失败: {ex.Message}");
        }
    }

    /// <summary>清空对话历史并记下清空时间；metadata（SharedModel/SharedPrompt）保留。</summary>
    public void Clear(ConversationKey key, DateTimeOffset clearedAt)
    {
        if (!_persist) return;

        try
        {
            var path = GetConversationFile(key);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            BotLog.Warning($"[AiChat] 清理会话文件失败: {ex.Message}");
        }

        // 记下清空时间，之后附带群聊上下文时不再带上清空之前的消息。
        lock (_metadataGate)
        {
            var state = new ConversationState(key);
            LoadMetadataFromDisk(state);
            WriteMetadata(key, state.SharedPrompt, state.SharedModel, clearedAt.ToUnixTimeMilliseconds());
        }
    }

    /// <summary>最近一次 #clear 的时间；没有清空过时返回 null。</summary>
    public DateTimeOffset? GetClearedAt(ConversationKey key)
    {
        var state = LoadMetadata(key);
        return state.ClearedAt is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
    }

    /// <summary>
    /// 在 context compact 后，用当前内存中的 turns 完整重写会话文件。
    /// </summary>
    public async Task RewriteAsync(ConversationState state, bool throwOnFailure = false)
    {
        if (!_persist) return;

        var path = GetConversationFile(state.Key);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tempPath = path + ".tmp";
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             useAsync: true))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                if (!string.IsNullOrWhiteSpace(state.Summary))
                {
                    var summary = new ChatTurn(SummaryRole, [new TextPart(state.Summary)], DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    await writer.WriteLineAsync(PluginJson.Serialize(summary, _jsonOptions)).ConfigureAwait(false);
                }
                if (state.MemorySnapshot is { } snapshot)
                {
                    var line = new ChatTurn(MemorySnapshotRole, [new TextPart(PluginJson.Serialize(snapshot))], DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    await writer.WriteLineAsync(PluginJson.Serialize(line, _jsonOptions)).ConfigureAwait(false);
                }
                foreach (var turn in state.Turns)
                {
                    await writer.WriteLineAsync(PluginJson.Serialize(turn, _jsonOptions)).ConfigureAwait(false);
                }
            }

            if (File.Exists(path))
                File.Replace(tempPath, path, destinationBackupFileName: null);
            else
                File.Move(tempPath, path);
        }
        catch (Exception ex) when (!throwOnFailure)
        {
            BotLog.Warning($"[AiChat] 重写会话文件失败 {path}: {ex.Message}");
        }
    }

    /// <summary>比较两次读取的历史是否同一条消息：按落盘格式序列化。</summary>
    internal string Fingerprint(ChatTurn turn) => PluginJson.Serialize(turn, _jsonOptions);

    private static ChatTurn StripTransientPayloads(ChatTurn turn)
    {
        var hasTransient = false;
        foreach (var part in turn.Parts)
        {
            if (part is ImagePart { CachePath: null })
            {
                hasTransient = true;
                break;
            }
        }

        if (!hasTransient) return turn;

        var stripped = new List<ChatPart>(turn.Parts.Count);
        foreach (var part in turn.Parts)
        {
            stripped.Add(part is ImagePart { CachePath: null }
                ? new TextPart(ImagePart.HistoryPlaceholder)
                : part);
        }

        return turn with { Parts = stripped };
    }

    /// <summary>旧版本记录的“[生成图片：…]”读取时换成程序记录格式，避免模型模仿这段文字代替工具调用。</summary>
    internal static ChatTurn UpgradeGeneratedImageRecord(ChatTurn turn)
    {
        if (turn.Role != "assistant" || turn.Parts is not [TextPart { Text: var text }, ..] ||
            !text.StartsWith(ImagePart.LegacyGeneratedPrefix, StringComparison.Ordinal) || !turn.Parts.Any(p => p is ImagePart))
            return turn;
        var prompt = text[ImagePart.LegacyGeneratedPrefix.Length..];
        if (prompt.EndsWith(']')) prompt = prompt[..^1];
        return turn with { Parts = [new TextPart(ImagePart.GeneratedRecord(prompt)), .. turn.Parts.Skip(1)] };
    }

    private void LoadTurnsFromDisk(ConversationState state)
    {
        var path = GetConversationFile(state.Key);
        if (!File.Exists(path)) return;

        try
        {
            var lines = File.ReadLines(path, Encoding.UTF8);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var turn = PluginJson.Deserialize<ChatTurn>(line, _jsonOptions);
                    if (turn is null) continue;
                    if (turn.Role == SummaryRole)
                        state.Summary = string.Concat(turn.Parts.OfType<TextPart>().Select(p => p.Text));
                    else if (turn.Role == MemorySnapshotRole)
                        state.MemorySnapshot = PluginJson.Deserialize<List<Memory.MemoryEntry>>(
                            string.Concat(turn.Parts.OfType<TextPart>().Select(p => p.Text))) ?? [];
                    else
                        state.Turns.Add(UpgradeGeneratedImageRecord(turn));
                }
                catch (JsonException ex)
                {
                    BotLog.Warning($"[AiChat] 跳过损坏的会话行: {ex.Message}");
                }
            }

            // 旧版本把摘要存成第一条 assistant 消息，读取时转为历史摘要。
            if (state.Summary is null && state.Turns.Count > 0 && state.Turns[0] is { Role: "assistant" } first &&
                first.Parts is [TextPart { Text: var legacy }] && legacy.StartsWith(LegacySummaryPrefix, StringComparison.Ordinal))
            {
                state.Summary = legacy[LegacySummaryPrefix.Length..];
                state.Turns.RemoveAt(0);
            }
        }
        catch (Exception ex)
        {
            BotLog.Warning($"[AiChat] 读取会话失败 {path}: {ex.Message}");
        }
    }

    private void LoadMetadataFromDisk(ConversationState state)
    {
        var path = GetMetadataFile(state.Key);
        if (!File.Exists(path)) return;

        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            var meta = PluginJson.Deserialize<ConversationMetadata>(json, _jsonOptions);
            if (meta is not null)
            {
                state.SharedPrompt = meta.SharedPrompt;
                state.SharedModel = meta.SharedModel;
                state.ClearedAt = meta.ClearedAt;
            }
        }
        catch (Exception ex)
        {
            BotLog.Warning($"[AiChat] 读取会话 metadata 失败: {ex.Message}");
        }
    }

    private async Task AppendToDiskAsync(ConversationKey key, ChatTurn userTurn, ChatTurn assistantTurn)
    {
        var path = GetConversationFile(key);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));

            await writer.WriteLineAsync(PluginJson.Serialize(userTurn, _jsonOptions)).ConfigureAwait(false);
            await writer.WriteLineAsync(PluginJson.Serialize(assistantTurn, _jsonOptions)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            BotLog.Warning($"[AiChat] 写入会话文件失败 {path}: {ex.Message}");
        }
    }

    private string GetConversationFile(ConversationKey key)
        => Path.Combine(_conversationsDir, key.ToFileSafeString() + ".jsonl");

    private string GetMetadataFile(ConversationKey key)
        => Path.Combine(_conversationsDir, key.ToFileSafeString() + ".meta.json");
}

/// <summary>
/// 会话元数据，持久化共享设置。
/// </summary>
internal sealed class ConversationMetadata
{
    public string? SharedPrompt { get; set; }
    public string? SharedModel { get; set; }
    public long? ClearedAt { get; set; }
}
