using System.Text;
using ShiroBot.AiChatPlugin.Conversation;
using ShiroBot.SDK.Abstractions;

namespace ShiroBot.AiChatPlugin.Memory;

internal sealed record MemoryEntry(string Id, string Text, DateTimeOffset UpdatedAt);

/// <summary>
/// 每个会话一本记忆本，保存在 data/memory/&lt;会话&gt;.json。读写都经过同一把锁，
/// 每日整理在锁外调用模型，写回前确认记忆本未被修改。
/// </summary>
internal sealed class MemoryNotebookStore
{
    private readonly string _dir;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MemoryNotebookStore(string pluginRoot) => _dir = Path.Combine(pluginRoot, "data", "memory");

    public string ReviewStampPath => Path.Combine(_dir, ".last-review");

    public async Task<List<MemoryEntry>> LoadAsync(ConversationKey key)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return Read(PathFor(key)); }
        finally { _gate.Release(); }
    }

    /// <summary>在锁内读取、修改并保存；change 返回 Changed=false 时不写盘。</summary>
    public async Task<string> UpdateAsync(ConversationKey key, Func<List<MemoryEntry>, (bool Changed, string Result)> change)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var path = PathFor(key);
            var entries = Read(path);
            var (changed, result) = change(entries);
            if (changed) Write(path, entries);
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task ClearAsync(ConversationKey key)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { File.Delete(PathFor(key)); }
        finally { _gate.Release(); }
    }

    public IReadOnlyList<string> Notebooks() =>
        Directory.Exists(_dir) ? Directory.GetFiles(_dir, "*.json") : [];

    public async Task<List<MemoryEntry>> ReadFileAsync(string path)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return Read(path); }
        finally { _gate.Release(); }
    }

    /// <summary>仅当记忆本仍与 snapshot 一致时写入，返回是否写入。</summary>
    public async Task<bool> ReplaceIfUnchangedAsync(string path, IReadOnlyList<MemoryEntry> snapshot, List<MemoryEntry> replacement)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!Read(path).SequenceEqual(snapshot)) return false;
            Write(path, replacement);
            return true;
        }
        finally { _gate.Release(); }
    }

    public static string Format(IReadOnlyList<MemoryEntry> entries) =>
        string.Join("\n", entries.Select(e => $"#{e.Id} {e.Text}"));

    private string PathFor(ConversationKey key) => Path.Combine(_dir, key.ToFileSafeString() + ".json");

    private static List<MemoryEntry> Read(string path)
    {
        if (!File.Exists(path)) return [];
        try { return PluginJson.Deserialize<List<MemoryEntry>>(File.ReadAllText(path, Encoding.UTF8)) ?? []; }
        catch (Exception ex)
        {
            // 损坏的文件不覆盖，交给用户处理；本次请求不使用记忆本。
            BotLog.Warning($"[AiChat] 读取记忆本失败 {path}: {ex.Message}");
            throw new InvalidOperationException("记忆本文件损坏，请检查 " + path, ex);
        }
    }

    private static void Write(string path, List<MemoryEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (entries.Count == 0)
        {
            File.Delete(path);
            return;
        }
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, PluginJson.Serialize(entries), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }
}
