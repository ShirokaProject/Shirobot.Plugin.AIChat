using System.Buffers;
using System.Security.Cryptography;
using ShiroBot.SDK.Abstractions;

namespace ShiroBot.AiChatPlugin.Resources;

/// <summary>
/// 资源缓存。生成 <c>{sha256}.b64</c> 存放完整的 data URL
/// （data:mime;base64,...），发送请求时直接流式读取文件。
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
    /// 分块写入 base64 data URL 到文件，避免在内存中产生完整的大字符串。
    /// </summary>
    private static async Task WriteBase64FileAsync(string path, byte[] bytes, string mime)
    {
        await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await fs.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"data:{mime};base64,"));
        var buffer = ArrayPool<byte>.Shared.Rent(16384);
        try
        {
            const int chunkSize = 12 * 1024;
            for (var offset = 0; offset < bytes.Length; offset += chunkSize)
            {
                var count = Math.Min(chunkSize, bytes.Length - offset);
                System.Buffers.Text.Base64.EncodeToUtf8(bytes.AsSpan(offset, count), buffer, out _, out int written);
                await fs.WriteAsync(buffer.AsMemory(0, written)).ConfigureAwait(false);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
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
