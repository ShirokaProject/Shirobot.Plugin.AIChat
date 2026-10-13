using System.Collections.Concurrent;
using System.Text.Json;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Models;

namespace ShiroBot.AiChatPlugin.UserState;

/// <summary>
/// 用户偏好仓库。所有用户合并存到一个 JSON 文件里：
/// <c>{ "12345": { "systemPrompt": "...", "preferredModel": "..." } }</c>
/// 写入采用全量替换 + 原子重命名，保证不会在崩溃中产生半文件。
/// </summary>
internal sealed class UserSettingsStore
{
    private readonly string _filePath;
    private readonly ConcurrentDictionary<string, UserSettings> _byUser;
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
            TypeInfoResolver = PluginJson.Resolver,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        _byUser = new ConcurrentDictionary<string, UserSettings>(LoadFromDisk());
    }

    public UserSettings GetOrCreate(UserReference user)
        => _byUser.GetOrAdd(Key(user), _ => new UserSettings());

    public UserSettings? Get(UserReference user)
        => _byUser.GetValueOrDefault(Key(user));

    private static string Key(UserReference user) =>
        new UserReference(user.InstanceId.ToUpperInvariant(), user.UserId).ToConfigString();

    public async Task SaveAsync()
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var snapshot = _byUser.ToDictionary(p => p.Key, p => p.Value);
            var json = PluginJson.Serialize(snapshot, _jsonOptions);

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

    private Dictionary<string, UserSettings> LoadFromDisk()
    {
        var result = new Dictionary<string, UserSettings>();
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

            var raw = PluginJson.Deserialize<Dictionary<string, UserSettings>>(json, _jsonOptions);
            if (raw is null)
            {
                return result;
            }

            foreach (var (k, v) in raw)
            {
                result[k] = v;
            }
        }
        catch (Exception ex)
        {
            BotLog.Warning($"[AiChat] 用户设置读取失败: {ex.Message}");
        }

        return result;
    }
}
