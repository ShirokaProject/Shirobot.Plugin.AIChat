using System.Text.Json;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.AiChatPlugin.Resources;

namespace ShiroBot.AiChatPlugin.Tools;

internal sealed class SendDownloadedFileTool(DownloadedFiles files,
    Func<DownloadedFileEntry, bool, CancellationToken, Task<string>> send) : IChatTool
{
    private readonly HashSet<string> _sent = new(StringComparer.Ordinal);
    public int MaxCallsPerTurn => int.MaxValue;
    public string SystemPrompt => "需要下载文件时用http_fetch的response_mode=file获取file_id；用户要收到图片或文件时调用send_file。file_id只在本次任务有效，不接受任意本地路径。仅当发送成功才能告诉用户已发送；图片URL或文件ID不代表你已看过图片。";
    public OpenAiTool Definition { get; } = new() { Function = new() {
        Name = "send_file", Description = "Send a file downloaded in this turn to the current conversation. Use as_image for PNG/JPEG/WebP/GIF inline display, false for a file attachment.",
        Parameters = new { type = "object", additionalProperties = false, required = new[] { "file_id" }, properties = new {
            file_id = new { type = "string" }, as_image = new { type = "boolean", description = "Default true for supported image files, false otherwise." } } }
    } };
    public async Task<string> ExecuteAsync(string arguments, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var doc = JsonDocument.Parse(arguments);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("file_id", out var id) || id.ValueKind != JsonValueKind.String) return "file_id is required.";
            var file = files.Resolve(id.GetString()!);
            var supportedImage = file.Info.mime is "image/png" or "image/jpeg" or "image/webp" or "image/gif";
            var asImage = supportedImage;
            if (doc.RootElement.TryGetProperty("as_image", out var value))
            {
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return "as_image must be boolean.";
                asImage = value.GetBoolean();
            }
            if (asImage && !supportedImage) return "This file cannot be sent as an image; use as_image=false.";
            if (_sent.Contains(file.Info.file_id)) return "This file was already sent in this turn.";
            var result = await send(file, asImage, ct).ConfigureAwait(false);
            if (result == "File sent successfully.") _sent.Add(file.Info.file_id);
            return result;
        }
        catch (JsonException) { return "Invalid send_file arguments."; }
        catch (ArgumentException ex) { return ex.Message; }
    }
    public void Dispose() { }
}
