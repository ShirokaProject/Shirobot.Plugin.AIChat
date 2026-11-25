using System.Net;
using System.Text.Json;

namespace ShiroBot.AiChatPlugin.Tools.Web;

internal sealed class HttpFetchException(Uri url, HttpStatusCode? status, string error, int attempts = 1) : HttpRequestException(error, null, status)
{
    public Uri Url => url;
    public int Attempts => attempts;
    // 不包含查询参数、片段和原始异常消息。
    public string SafeTarget => url.GetLeftPart(UriPartial.Path);
    public string Diagnostic => $"target={PluginJson.Serialize(SafeTarget)} http_status={(StatusCode is null ? "none" : ((int)StatusCode).ToString())} reason={PluginJson.Serialize(Message)} attempts={attempts}";
    public string ToolResult => PluginJson.Serialize(new { url = SafeTarget, error = Message, http_status = StatusCode is null ? (int?)null : (int)StatusCode,
        attempts,
        hint = attempts > 1
            ? "The request was already retried automatically and still failed; the site is likely unreachable from this server. Do not request the same URL again; try another source or tell the user."
            : "Use actual links returned by previous fetch results. Do not guess paths or repeat the same failed request." });
}
