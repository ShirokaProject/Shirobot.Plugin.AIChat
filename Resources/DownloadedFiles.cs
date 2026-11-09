using ShiroBot.AiChatPlugin.Conversation;

namespace ShiroBot.AiChatPlugin.Resources;

internal sealed record DownloadedFile(string file_id, string file_name, string mime, long bytes);
internal sealed record DownloadedFileEntry(DownloadedFile Info, string Path);

/// <summary>Opaque handles scoped to one chat turn; host expires the underlying temporary directory.</summary>
internal sealed class DownloadedFiles(Func<string> createDirectory)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DownloadedFileEntry> _files = new(StringComparer.Ordinal);
    private string? _directory;
    private long _bytes;
    private int _reserved;

    internal async Task<DownloadedFile> SaveAsync(byte[] bytes, string mime, string name, CancellationToken ct)
    {
        var imageMime = MimeGuesser.SniffImageMime(bytes);
        mime = imageMime ?? (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "application/octet-stream" : mime);
        name = Path.GetFileName(name.Replace('\\', '/'));
        name = new string(name.Where(c => !char.IsControl(c)).Take(120).ToArray());
        if (name is "" or "." or "..") name = "download";
        if (imageMime is not null) name = Path.GetFileNameWithoutExtension(name) + MimeGuesser.ExtensionForMime(imageMime);
        var id = "file_" + Guid.NewGuid().ToString("N");
        string path;
        lock (_gate)
        {
            if (_files.Count + _reserved >= 16 || _bytes + bytes.Length > 100_000_000) throw new InvalidOperationException("Download quota reached for this turn (16 files / 100 MB).");
            _directory ??= createDirectory();
            path = Path.Combine(_directory, id);
            _reserved++; _bytes += bytes.Length;
        }
        try
        {
            await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
            var info = new DownloadedFile(id, name, mime, bytes.Length);
            lock (_gate) { _reserved--; _files.Add(id, new(info, path)); }
            return info;
        }
        catch
        {
            lock (_gate) { _reserved--; _bytes -= bytes.Length; }
            try { File.Delete(path); } catch { }
            throw;
        }
    }
    internal DownloadedFileEntry Resolve(string id)
    {
        lock (_gate)
            return _files.TryGetValue(id, out var file) && File.Exists(file.Path) ? file :
                throw new ArgumentException("Unknown or expired file_id. Download it in this turn first.");
    }
    internal async Task<ImagePart[]> ImagesAsync(string[] ids, CancellationToken ct)
    {
        if (ids.Length is < 1 or > 4) throw new ArgumentException("Choose one to four downloaded reference files.");
        var images = new List<ImagePart>();
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            var file = Resolve(id);
            if (file.Info.bytes > 20_000_000) throw new ArgumentException("Reference image exceeds 20 MB.");
            var bytes = await File.ReadAllBytesAsync(file.Path, ct).ConfigureAwait(false);
            var normalized = ReferenceImage.Normalize(bytes);
            images.Add(new ImagePart(null, normalized.Mime, normalized.Bytes.Length, normalized.Bytes));
        }
        return images.ToArray();
    }
}
