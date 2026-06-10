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
    private readonly ConcurrentDictionary<ConversationKey, SemaphoreSlim> _locks = new();
    private readonly string _conversationsDir;
    private readonly bool _persist;
    private readonly int _maxTurns;
    private readonly JsonSerializerOptions _jsonOptions;

    public ConversationStore(string pluginRootDir, bool persist, int maxTurns)
    {
        _conversationsDir = Path.Combine(pluginRootDir, "data", "conversations");
        _persist = persist;
        _maxTurns = Math.Max(2, maxTurns);
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
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
    public async Task<ConversationState> AcquireAsync(ConversationKey key)
    {
        var lk = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await lk.WaitAsync().ConfigureAwait(false);

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
    /// 追加一对 user+assistant turn 到磁盘，并执行 trim。
    /// 调用方必须已通过 AcquireAsync 持有锁。
    /// </summary>
    public async Task AppendTurnsAsync(ConversationState state, ChatTurn userTurn, ChatTurn assistantTurn)
    {
        state.Turns.Add(StripTransientPayloads(userTurn));
        state.Turns.Add(assistantTurn);

        var trimmed = TrimAndReturnRemoved(state);

        if (_persist)
        {
            if (trimmed)
            {
                // trim 发生了，需要重写整个文件
                await RewriteAsync(state).ConfigureAwait(false);
            }
            else
            {
                await AppendToDiskAsync(state.Key, StripTransientPayloads(userTurn), assistantTurn).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 保存共享会话的 metadata（SharedPrompt/SharedModel）到磁盘。
    /// </summary>
    public void SaveMetadata(ConversationState state)
    {
        if (!_persist) return;

        var path = GetMetadataFile(state.Key);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var meta = new ConversationMetadata
            {
                SharedPrompt = state.SharedPrompt,
                SharedModel = state.SharedModel
            };
            var json = JsonSerializer.Serialize(meta, _jsonOptions);
            File.WriteAllText(path, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            BotLog.Warning($"[AiChat] 保存会话 metadata 失败: {ex.Message}");
        }
    }

    public void Clear(ConversationKey key)
    {
        if (!_persist) return;

        try
        {
            // 只清对话历史，不清 metadata（SharedModel/SharedPrompt）
            var path = GetConversationFile(key);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            BotLog.Warning($"[AiChat] 清理会话文件失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 在 context compact 后，用当前内存中的 turns 完整重写会话文件。
    /// </summary>
    public async Task RewriteAsync(ConversationState state)
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
                foreach (var turn in state.Turns)
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(turn, _jsonOptions)).ConfigureAwait(false);
                }
            }

            if (File.Exists(path))
                File.Replace(tempPath, path, destinationBackupFileName: null);
            else
                File.Move(tempPath, path);
        }
        catch (Exception ex)
        {
            BotLog.Warning($"[AiChat] 重写会话文件失败 {path}: {ex.Message}");
        }
    }

    private static ChatTurn StripTransientPayloads(ChatTurn turn)
    {
        var hasTransient = false;
        foreach (var part in turn.Parts)
        {
            if (part is ImagePart)
            {
                hasTransient = true;
                break;
            }
        }

        if (!hasTransient) return turn;

        var stripped = new List<ChatPart>(turn.Parts.Count);
        foreach (var part in turn.Parts)
        {
            stripped.Add(part is ImagePart
                ? new TextPart("[图片已在上一轮发送给模型，后续上下文不再附带图片内容。]")
                : part);
        }

        return turn with { Parts = stripped };
    }

    /// <summary>
    /// 裁剪到 maxTurns，返回是否发生了裁剪。
    /// </summary>
    private bool TrimAndReturnRemoved(ConversationState state)
    {
        var maxItems = _maxTurns * 2;
        if (state.Turns.Count <= maxItems) return false;

        var remove = state.Turns.Count - maxItems;
        state.Turns.RemoveRange(0, remove);
        return true;
    }

    private void LoadTurnsFromDisk(ConversationState state)
    {
        var path = GetConversationFile(state.Key);
        if (!File.Exists(path)) return;

        try
        {
            var lines = File.ReadAllLines(path, Encoding.UTF8);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var turn = JsonSerializer.Deserialize<ChatTurn>(line, _jsonOptions);
                    if (turn is not null) state.Turns.Add(turn);
                }
                catch (JsonException ex)
                {
                    BotLog.Warning($"[AiChat] 跳过损坏的会话行: {ex.Message}");
                }
            }

            // 加载后立即 trim
            var maxItems = _maxTurns * 2;
            if (state.Turns.Count > maxItems)
            {
                var remove = state.Turns.Count - maxItems;
                state.Turns.RemoveRange(0, remove);
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
            var meta = JsonSerializer.Deserialize<ConversationMetadata>(json, _jsonOptions);
            if (meta is not null)
            {
                state.SharedPrompt = meta.SharedPrompt;
                state.SharedModel = meta.SharedModel;
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

            await writer.WriteLineAsync(JsonSerializer.Serialize(userTurn, _jsonOptions)).ConfigureAwait(false);
            await writer.WriteLineAsync(JsonSerializer.Serialize(assistantTurn, _jsonOptions)).ConfigureAwait(false);
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
}
