using ShiroBot.AiChatPlugin.Conversation;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.AiChatPlugin.Tools.Memory;
using ShiroBot.AiChatPlugin.Usage;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Models;

namespace ShiroBot.AiChatPlugin;

/// <summary>记忆本：#memory 命令和后台整理。</summary>
public sealed partial class AiChatPlugin
{
    // -------- #memory --------

    private async Task HandleMemoryAsync(MessageEvent message)
    {
        if (!_config.ToolUse.Enabled || !_config.ToolUse.Memory.Enabled) return;
        var isGroup = !message.IsDirect;
        var arg = StripPrefix(message.GetPlainText(), "#memory");
        var mode = _userSettings.GetOrCreate(ScopedUser(SenderId(message))).ChatMode ?? _config.Default.DefaultChatMode;
        var key = ResolveConversationKey(isGroup ? ChatScene.Group : ChatScene.Friend, PeerId(message), SenderId(message));
        if (mode == "single")
        {
            await ReplyAsync(message, isGroup, "single 模式不使用记忆本。").ConfigureAwait(false);
            return;
        }
        if (arg is "clear" or "清空")
        {
            if (isGroup && mode == "shared" && !await CanClearAsync(message).ConfigureAwait(false)) return;
            await _memory.ClearAsync(key).ConfigureAwait(false);
            await ReplyAsync(message, isGroup, "已清空当前会话的记忆本。").ConfigureAwait(false);
            return;
        }
        List<MemoryEntry> notes;
        try { notes = await _memory.LoadAsync(key).ConfigureAwait(false); }
        catch (Exception ex)
        {
            await ReplyAsync(message, isGroup, ex.Message).ConfigureAwait(false);
            return;
        }
        await ReplyAsync(message, isGroup, notes.Count == 0
            ? "当前会话的记忆本是空的。"
            : $"记忆本（{notes.Count} 条）：\n{MemoryNotebookStore.Format(notes)}").ConfigureAwait(false);
    }

    /// <summary>每分钟检查一次日期；跨过本地 0 点后整理所有记忆本。首次运行只记录日期，不立即整理。</summary>
    private async Task RunMemoryReviewLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var memory = _config.ToolUse.Memory;
                    var today = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
                    var stamp = _memory.ReviewStampPath;
                    var last = File.Exists(stamp) ? File.ReadAllText(stamp).Trim() : null;
                    if (_config.ToolUse.Enabled && memory is { Enabled: true, DailyReview: true } && last != today)
                    {
                        if (last is not null) await ReviewMemoryNotebooksAsync(ct).ConfigureAwait(false);
                        Directory.CreateDirectory(Path.GetDirectoryName(stamp)!);
                        await File.WriteAllTextAsync(stamp, today, ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    BotLog.Warning($"[AiChat] 记忆本整理失败: {ex.Message}");
                }
                await Task.Delay(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    internal async Task ReviewMemoryNotebooksAsync(CancellationToken ct)
    {
        var model = ResolveModel(_config.Default.Model) ?? throw new InvalidOperationException("默认模型未注册");
        var maxChars = Math.Clamp(_config.ToolUse.Memory.MaxEntryChars, 20, 1000);
        using var usageScope = UsageScope.Begin("-", "memory_review");
        foreach (var path in _memory.Notebooks())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var entries = await _memory.ReadFileAsync(path).ConfigureAwait(false);
                if (entries.Count == 0) continue;
                await _globalConcurrency.WaitAsync(ct).ConfigureAwait(false);
                string response;
                try
                {
                    response = await _openAi.ChatAsync(_config.Default, model, new OpenAiChatRequest
                    {
                        Model = model.Name, Stream = false, MaxCompletionTokens = 4096,
                        Messages =
                        [
                            new OpenAiMessage { Role = "system", Content =
                                $"你在整理聊天机器人的记忆本。今天是 {DateTime.Now:yyyy-MM-dd}。删除已经过时（如已过去的日程）、重复、互相矛盾中较旧的、或没有长期价值的条目；把内容相近的条目合并成一条；其余条目保持原文。不要编造新信息，每条不超过 {maxChars} 字。" +
                                "只输出 JSON，格式为 {\"keep\":[{\"id\":\"保留或合并后沿用的原条目 id\",\"text\":\"条目内容\"}]}。" },
                            new OpenAiMessage { Role = "user", Content = MemoryNotebookStore.Format(entries) }
                        ]
                    }, ct).ConfigureAwait(false);
                }
                finally { _globalConcurrency.Release(); }

                var kept = ParseMemoryReview(response, entries, maxChars);
                if (kept is null)
                {
                    BotLog.Warning($"[AiChat] 记忆本整理返回无法解析，保持原样 {Path.GetFileName(path)}:\n{ForLog(response)}");
                    continue;
                }
                if (kept.SequenceEqual(entries)) continue;
                if (!await _memory.ReplaceIfUnchangedAsync(path, entries, kept).ConfigureAwait(false))
                {
                    BotLog.Info($"[AiChat] 记忆本在整理期间被修改，跳过 {Path.GetFileName(path)}");
                    continue;
                }
                var removed = entries.Select(e => e.Id).Except(kept.Select(e => e.Id));
                BotLog.Info($"[AiChat] 记忆本整理完成 {Path.GetFileName(path)}: {entries.Count} -> {kept.Count} 条, 删除 [{string.Join(", ", removed)}]\n{MemoryNotebookStore.Format(kept)}");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                BotLog.Warning($"[AiChat] 整理记忆本 {Path.GetFileName(path)} 失败: {ex.Message}");
            }
        }
    }

    /// <summary>只接受原有 id；格式错误返回 null，保持记忆本不变。</summary>
    internal static List<MemoryEntry>? ParseMemoryReview(string response, IReadOnlyList<MemoryEntry> original, int maxChars)
    {
        var start = response.IndexOf('{');
        var end = response.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(response[start..(end + 1)]);
            if (!doc.RootElement.TryGetProperty("keep", out var keep) || keep.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
            var result = new List<MemoryEntry>();
            foreach (var item in keep.EnumerateArray())
            {
                if (item.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
                var id = item.TryGetProperty("id", out var idValue) ? idValue.GetString()?.TrimStart('#') : null;
                var text = item.TryGetProperty("text", out var textValue) ? textValue.GetString()?.Trim() : null;
                var source = original.FirstOrDefault(e => e.Id == id);
                if (source is null || string.IsNullOrEmpty(text) || result.Any(e => e.Id == id)) continue;
                if (text.Length > maxChars) text = text[..maxChars];
                result.Add(text == source.Text ? source : source with { Text = text, UpdatedAt = DateTimeOffset.UtcNow });
            }
            return result;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
