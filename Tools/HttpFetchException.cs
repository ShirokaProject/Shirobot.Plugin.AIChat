using System.Net;
using System.Text.Json;

namespace ShiroBot.AiChatPlugin.Tools;

internal sealed class HttpFetchException(Uri url, HttpStatusCode? status, string error) : HttpRequestException(error, null, status)
{
    // 不包含查询参数、片段和原始异常消息。
    public string SafeTarget => url.GetLeftPart(UriPartial.Path);
    public string Diagnostic => $"target={JsonSerializer.Serialize(SafeTarget)} http_status={(StatusCode is null ? "none" : ((int)StatusCode).ToString())} reason={JsonSerializer.Serialize(Message)}";
    public string ToolResult => JsonSerializer.Serialize(new { url = SafeTarget, error = Message, http_status = StatusCode is null ? (int?)null : (int)StatusCode,
        hint = "Use actual links returned by previous fetch results. Do not guess paths or repeat the same failed request." });
}
