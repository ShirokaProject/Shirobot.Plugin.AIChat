using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.SDK.Abstractions;

namespace ShiroBot.AiChatPlugin.OpenAi;

internal sealed class TavilySearchClient : IDisposable
{
    private readonly TavilySection _config;
    private readonly HttpClient _http;

    public TavilySearchClient(TavilySection config, string httpProxy)
    {
        _config = config;
        _http = new HttpClient(HttpProxy.CreateHandler(httpProxy));
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_config.ApiKey);

    public async Task<string> SearchAsync(string query, CancellationToken ct)
    {
        var searchDepth = _config.SearchDepth.Equals("advanced", StringComparison.OrdinalIgnoreCase) ? "advanced" : "basic";
        var maxResults = Math.Clamp(_config.MaxResults, 1, 5);
        BotLog.Info($"[AiChat][Tavily] 开始搜索: query={query}, depth={searchDepth}, max_results={maxResults}");

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.tavily.com/search");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
        request.Content = new StringContent(PluginJson.Serialize(new
        {
            query,
            search_depth = searchDepth,
            max_results = maxResults,
            include_answer = false,
            include_raw_content = false
        }), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            BotLog.Warning($"[AiChat][Tavily] 搜索失败: HTTP {(int)response.StatusCode}。");
            return $"Tavily search failed: HTTP {(int)response.StatusCode}.";
        }

        using var document = JsonDocument.Parse(body);
        var results = document.RootElement.TryGetProperty("results", out var value) ? value : default;
        if (results.ValueKind != JsonValueKind.Array)
        {
            BotLog.Info("[AiChat][Tavily] 搜索完成: 0 条结果。");
            return "No search results found.";
        }

        var lines = new List<string>();
        foreach (var item in results.EnumerateArray())
        {
            var title = item.TryGetProperty("title", out var t) ? t.GetString() : "Untitled";
            var url = item.TryGetProperty("url", out var u) ? u.GetString() : "";
            var content = item.TryGetProperty("content", out var c) ? c.GetString() : "";
            lines.Add($"Title: {title}\nURL: {url}\nSummary: {content}");
        }

        BotLog.Info($"[AiChat][Tavily] 搜索完成: {lines.Count} 条结果。");
        return lines.Count == 0 ? "No search results found." : string.Join("\n\n", lines);
    }

    public void Dispose() => _http.Dispose();

}
