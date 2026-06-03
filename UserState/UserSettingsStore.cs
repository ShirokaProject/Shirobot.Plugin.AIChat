using System.Collections.Concurrent;
using System.Text.Json;
using ShiroBot.SDK.Abstractions;

namespace ShiroBot.AiChatPlugin.UserState;

/// <summary>
/// 用户偏好仓库。所有用户合并存到一个 JSON 文件里：
/// <c>{ "12345": { "systemPrompt": "...", "preferredModel": "..." } }</c>
/// 写入采用全量替换 + 原子重命名，保证不会在崩溃中产生半文件。
/// </summary>
internal sealed class UserSettingsStore
{
    private readonly string _filePath;
    private readonly ConcurrentDictionary<long, UserSettings> _byUser;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions;

    public UserSettingsStore(string pluginRootDir)
    {
        var dataDir = Path.Combine(pluginRootDir, "data");
        Directory.CreateDirectory(dataDir);
        _filePath = Path.Combine(dataDir, "user_settings.json");

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        _byUser = new ConcurrentDictionary<long, UserSettings>(LoadFromDisk());
    }

    public UserSettings GetOrCreate(long userId)
        => _byUser.GetOrAdd(userId, _ => new UserSettings());

    public UserSettings? Get(long userId)
        => _byUser.TryGetValue(userId, out var s) ? s : null;

    public async Task SaveAsync()
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var snapshot = _byUser.ToDictionary(p => p.Key.ToString(), p => p.Value);
            var json = JsonSerializer.Serialize(snapshot, _jsonOptions);

            var tempPath = _filePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json).ConfigureAwait(false);

            // 原子替换。File.Replace 在目标不存在时会抛异常，所以分两路处理。
            if (File.Exists(_filePath))
            {
                File.Replace(tempPath, _filePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, _filePath);
            }
        }
        catch (Exception ex)
        {
            BotLog.Warning($"[AiChat] 用户设置写入失败: {ex.Message}");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private Dictionary<long, UserSettings> LoadFromDisk()
    {
        var result = new Dictionary<long, UserSettings>();
        if (!File.Exists(_filePath))
        {
            return result;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return result;
            }

            var raw = JsonSerializer.Deserialize<Dictionary<string, UserSettings>>(json, _jsonOptions);
            if (raw is null)
            {
                return result;
            }

            foreach (var (k, v) in raw)
            {
                if (long.TryParse(k, out var uid) && v is not null)
                {
                    result[uid] = v;
                }
            }
        }
        catch (Exception ex)
        {
            BotLog.Warning($"[AiChat] 用户设置读取失败: {ex.Message}");
        }

        return result;
    }
}
