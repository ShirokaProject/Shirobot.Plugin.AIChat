using System.Text;
using System.Text.Json;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.AiChatPlugin.Resources;

namespace ShiroBot.AiChatPlugin.Tools;

/// <summary>
/// 把模型写好的文本存成本轮的临时文件，返回 file_id 交给 send_file。
/// 之前模型只能先把正文 POST 到 paste.rs 之类的公网站点再下载回来，内容会被公开。
/// </summary>
internal sealed class CreateTextFileTool(DownloadedFiles files) : IChatTool
{
    internal const int MaxBytes = 1_048_576;

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = "text/plain", [".md"] = "text/markdown", [".markdown"] = "text/markdown", [".log"] = "text/plain",
        [".csv"] = "text/csv", [".tsv"] = "text/tab-separated-values", [".json"] = "application/json", [".jsonl"] = "application/jsonl",
        [".xml"] = "application/xml", [".yaml"] = "application/yaml", [".yml"] = "application/yaml", [".toml"] = "application/toml",
        [".ini"] = "text/plain", [".conf"] = "text/plain", [".cfg"] = "text/plain", [".env"] = "text/plain",
        [".html"] = "text/html", [".htm"] = "text/html", [".css"] = "text/css", [".svg"] = "image/svg+xml",
        [".js"] = "text/javascript", [".mjs"] = "text/javascript", [".ts"] = "text/plain", [".tsx"] = "text/plain", [".jsx"] = "text/plain",
        [".py"] = "text/x-python", [".cs"] = "text/plain", [".java"] = "text/plain", [".kt"] = "text/plain", [".kts"] = "text/plain",
        [".go"] = "text/plain", [".rs"] = "text/plain", [".c"] = "text/plain", [".h"] = "text/plain", [".cpp"] = "text/plain",
        [".hpp"] = "text/plain", [".lua"] = "text/plain", [".rb"] = "text/plain", [".php"] = "text/plain", [".swift"] = "text/plain",
        [".sql"] = "application/sql", [".sh"] = "text/plain", [".gradle"] = "text/plain", [".properties"] = "text/plain",
        [".tex"] = "text/plain", [".srt"] = "text/plain", [".ass"] = "text/plain", [".lrc"] = "text/plain"
    };

    public int MaxCallsPerTurn => int.MaxValue;
    public string SystemPrompt => "需要把一段文字、代码、表格或日志作为文件发给用户时，用 create_text_file 在本地生成临时文件拿到 file_id，再调用 send_file 发送；不要为此把内容上传到 paste.rs 等第三方网站。";
    public OpenAiTool Definition { get; } = new() { Function = new() {
        Name = "create_text_file",
        Description = "Create a temporary UTF-8 text file from the given content and return a file_id for send_file. Valid only in this turn. Use instead of uploading text to third-party paste sites.",
        Parameters = new { type = "object", additionalProperties = false, required = new[] { "content", "file_name" }, properties = new {
            content = new { type = "string", description = "Full file content (UTF-8 text, at most 1 MB)." },
            file_name = new { type = "string", description = "Display file name with a text extension, e.g. notes.md, data.csv, script.py. Unknown extensions become .txt." } } }
    } };

    public async Task<string> ExecuteAsync(string arguments, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        string content, fileName;
        try
        {
            using var doc = JsonDocument.Parse(arguments);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("content", out var contentValue) || contentValue.ValueKind != JsonValueKind.String)
                return "content is required and must be a string.";
            content = contentValue.GetString()!;
            fileName = root.TryGetProperty("file_name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String ? nameValue.GetString()! : "";
        }
        catch (JsonException) { return "Invalid create_text_file arguments."; }

        if (content.Length == 0) return "content is empty; nothing to save.";
        var bytes = Encoding.UTF8.GetBytes(content);
        if (bytes.Length > MaxBytes) return "content exceeds 1 MB; shorten it or split it into several files.";

        var (name, mime) = Normalize(fileName);
        try
        {
            var file = await files.SaveAsync(bytes, mime, name, ct).ConfigureAwait(false);
            return PluginJson.Serialize(new { success = true, file, scope = "current_turn",
                hint = "Call send_file with this file_id (as_image=false) to deliver it. The file is not sent until send_file succeeds." });
        }
        catch (InvalidOperationException ex) { return ex.Message; }
    }

    internal static (string Name, string Mime) Normalize(string fileName)
    {
        var name = Path.GetFileName(fileName.Replace('\\', '/')).Trim();
        name = new string(name.Where(c => !char.IsControl(c)).ToArray());
        var extension = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name).Trim().TrimStart('.');
        if (stem.Length == 0) stem = "file";
        if (stem.Length > 100) stem = stem[..100];
        if (!Types.TryGetValue(extension, out var mime)) return (stem + (extension.Length > 0 ? extension.Replace('.', '_') : "") + ".txt", "text/plain");
        return (stem + extension.ToLowerInvariant(), mime);
    }

    public void Dispose() { }
}
