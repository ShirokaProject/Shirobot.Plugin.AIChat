using ShiroBot.AiChatPlugin.Resources;
using SkiaSharp;

internal static class ReferenceImageChecks
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static async Task RunAsync()
    {
        var gif = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");
        Check(ReferenceEquals(ReferenceImage.Normalize(gif).Bytes, gif) && ReferenceImage.Normalize(gif).Mime == "image/gif", "GIF must be retained for first upload.");
        var result = ReferenceImage.ToPng(gif);
        Check(result.Mime == "image/png" && MimeGuesser.SniffImageMime(result.Bytes) == "image/png", "GIF not converted to PNG.");
        using (var decoded = SKBitmap.Decode(result.Bytes)) Check(decoded.Width == 1 && decoded.Height == 1, "Converted image dimensions changed.");
        Check(ReferenceEquals(ReferenceImage.Normalize(result.Bytes).Bytes, result.Bytes), "Accepted PNG was needlessly re-encoded.");
        var rejected = false;
        try { ReferenceImage.Normalize("not an image"u8.ToArray()); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "Invalid image bytes accepted.");
        var root = Path.Combine(Path.GetTempPath(), "aichat-gif-reference-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var files = new DownloadedFiles(() => root);
            var file = await files.SaveAsync(gif, "image/gif", "reference.gif", default);
            var references = await files.ImagesAsync([file.file_id], default);
            Check(references.Single().Mime == "image/gif" && references.Single().InlineBytes!.SequenceEqual(gif), "Downloaded GIF bytes were not preserved for first upload.");
            Check(File.ReadAllBytes(files.Resolve(file.file_id).Path).SequenceEqual(gif), "Conversion overwrote original downloadable GIF.");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("PASS: GIF reference first-frame conversion, PNG byte preservation, invalid-image rejection, downloaded references and original file preservation");
    }
}
