using System.Buffers;
using System.Security.Cryptography;
using ShiroBot.SDK.Abstractions;

namespace ShiroBot.AiChatPlugin.Resources;

/// <summary>
/// 资源缓存。生成 <c>{sha256}.b64</c> 存放完整的 data URL
/// （data:mime;base64,...），后续读取零转换、零大对象分配。
/// </summary>
internal sealed class ResourceCache
{
    private readonly string _cacheDir;
    private readonly int _cacheMaxAgeDays;

    public ResourceCache(string pluginRootDir, string relativeCacheDir, int cacheMaxAgeDays)
    {
        var normalized = relativeCacheDir.Replace('/', Path.DirectorySeparatorChar);
        _cacheDir = Path.IsPathRooted(normalized)
            ? Path.GetFullPath(normalized)
            : Path.GetFullPath(Path.Combine(pluginRootDir, normalized));
        _cacheMaxAgeDays = cacheMaxAgeDays;
        EnsureDirectoryExists();
        BotLog.Info($"[AiChat] 资源缓存目录: {_cacheDir}");
    }

    public string CacheDir => _cacheDir;

    /// <summary>
    /// 把字节流写到缓存，生成 .b64 文件存放完整 data URL。
    /// 返回按 hash 推导出的原始文件路径；即使未保留原图，也可用它定位同名 .b64。
    /// </summary>
    public async Task<string?> StoreAsync(byte[] bytes, string extension, string mime)
    {
        var hash = ComputeSha256(bytes);
        var fileName = hash + (extension.StartsWith('.') ? extension : "." + extension);
        var fullPath = Path.Combine(_cacheDir, fileName);
        var b64Path = Path.Combine(_cacheDir, hash + ".b64");

        // 如果 .b64 已存在，说明之前已经处理过
        if (File.Exists(b64Path))
        {
            return fullPath;
        }

        try
        {
            EnsureDirectoryExists();

            // 写原始文件
            if (!File.Exists(fullPath))
            {
                await File.WriteAllBytesAsync(fullPath, bytes).ConfigureAwait(false);
            }

            // 写 .b64 文件：分块编码避免大字符串进 LOH
            await WriteBase64FileAsync(b64Path, bytes, mime).ConfigureAwait(false);

            TryDeleteFile(fullPath);

            return fullPath;
        }
        catch (Exception ex)
        {
            BotLog.Warning($"[AiChat] 写缓存失败: {ex.Message}");
            return null;
        }
    }

    public Task CleanupAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                EnsureDirectoryExists();
                DeleteOriginalImagesWithBase64Cache();
                DeleteExpiredCacheFiles();
            }
            catch (Exception ex)
            {
                BotLog.Warning($"[AiChat] 清理资源缓存失败: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// 读取已缓存的 data URL。直接从 .b64 文件读取，零转换。
    /// 返回 null 表示缓存不存在。
    /// </summary>
    public string? ReadDataUrl(string? cachePath)
    {
        if (string.IsNullOrEmpty(cachePath)) return null;

        var fileName = Path.GetFileNameWithoutExtension(cachePath);
        var b64Path = Path.Combine(_cacheDir, fileName + ".b64");

        if (!File.Exists(b64Path)) return null;

        try
        {
            return File.ReadAllText(b64Path);
        }
        catch (Exception ex)
        {
            BotLog.Warning($"[AiChat] 读取 .b64 缓存失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 分块写入 base64 data URL 到文件，避免在内存中产生完整的大字符串。
    /// </summary>
    private static async Task WriteBase64FileAsync(string path, byte[] bytes, string mime)
    {
        await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);
        await using var writer = new StreamWriter(fs, System.Text.Encoding.ASCII);

        // 写 data URL 前缀
        await writer.WriteAsync($"data:{mime};base64,").ConfigureAwait(false);

        // 分块编码写入（每块 48KB 原始 → 64KB base64，远低于 85KB LOH 阈值）
        const int chunkSize = 48 * 1024;
        var offset = 0;
        while (offset < bytes.Length)
        {
            var count = Math.Min(chunkSize, bytes.Length - offset);
            var chunk = Convert.ToBase64String(bytes, offset, count);
            await writer.WriteAsync(chunk).ConfigureAwait(false);
            offset += count;
        }
    }

    public static string ComputeSha256(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void EnsureDirectoryExists()
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
        }
        catch (Exception ex)
        {
            BotLog.Warning($"[AiChat] 创建缓存目录失败 {_cacheDir}: {ex.Message}");
        }
    }

    private void DeleteOriginalImagesWithBase64Cache()
    {
        foreach (var file in Directory.EnumerateFiles(_cacheDir))
        {
            var ext = Path.GetExtension(file);
            if (!IsImageExtension(ext)) continue;

            var b64Path = Path.Combine(_cacheDir, Path.GetFileNameWithoutExtension(file) + ".b64");
            if (File.Exists(b64Path)) TryDeleteFile(file);
        }
    }

    private void DeleteExpiredCacheFiles()
    {
        if (_cacheMaxAgeDays <= 0) return;

        var cutoff = DateTime.UtcNow.AddDays(-_cacheMaxAgeDays);
        foreach (var file in Directory.EnumerateFiles(_cacheDir))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex)
            {
                BotLog.Warning($"[AiChat] 删除过期缓存失败 {file}: {ex.Message}");
            }
        }
    }

    private static bool IsImageExtension(string extension) =>
        extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase);

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            BotLog.Warning($"[AiChat] 删除缓存文件失败 {path}: {ex.Message}");
        }
    }
}
