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

    public TavilySearchClient(TavilySection config, string httpProxy, HttpMessageHandler? handler = null)
    {
        _config = config;
        _http = new HttpClient(handler ?? HttpProxy.CreateHandler(httpProxy));
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_config.ApiKey);

    public async Task<string> SearchAsync(string query, CancellationToken ct, bool includeImages = false)
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
            include_images = includeImages,
            include_image_descriptions = includeImages,
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
        var lines = new List<string>();
        foreach (var item in results.ValueKind == JsonValueKind.Array ? results.EnumerateArray().ToArray() : [])
        {
            var title = item.TryGetProperty("title", out var t) ? t.GetString() : "Untitled";
            var url = item.TryGetProperty("url", out var u) ? u.GetString() : "";
            var content = item.TryGetProperty("content", out var c) ? c.GetString() : "";
            lines.Add($"Title: {title}\nURL: {url}\nSummary: {content}");
        }

        if (includeImages)
        {
            var images = ExtractImages(document.RootElement);
            if (images.Count > 0) lines.Add("Image URLs (descriptions are metadata, not visual inspection; download with http_fetch response_mode=file):\n" +
                string.Join("\n", images.Select(image => PluginJson.Serialize(image))));
        }
        BotLog.Info($"[AiChat][Tavily] 搜索完成: {lines.Count} 条结果。");
        return lines.Count == 0 ? "No search results found." : string.Join("\n\n", lines);
    }

    internal static IReadOnlyList<Dictionary<string, string>> ExtractImages(JsonElement root)
    {
        var output = new List<Dictionary<string, string>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(JsonElement source, string? page = null)
        {
            if (!source.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array) return;
            foreach (var image in images.EnumerateArray())
            {
                var link = image.ValueKind == JsonValueKind.String ? image.GetString() :
                    image.ValueKind == JsonValueKind.Object && image.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String ? url.GetString() : null;
                if (link is null || !Uri.TryCreate(link, UriKind.Absolute, out var parsed)) continue;
                try { Tools.PublicHttpPolicy.ValidateUrl(parsed); } catch (InvalidOperationException) { continue; }
                if (output.Count >= 12 || !seen.Add(parsed.AbsoluteUri)) continue;
                var description = image.ValueKind == JsonValueKind.Object && image.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String ? desc.GetString() ?? "" : "";
                output.Add(new() { ["url"] = parsed.AbsoluteUri, ["description"] = description[..Math.Min(description.Length, 500)], ["source_url"] = page ?? "" });
            }
        }
        Add(root);
        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            foreach (var result in results.EnumerateArray())
                if (result.ValueKind == JsonValueKind.Object) Add(result, result.TryGetProperty("url", out var page) && page.ValueKind == JsonValueKind.String ? page.GetString() : null);
        return output;
    }

    public void Dispose() => _http.Dispose();

}
