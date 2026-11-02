using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.AiChatPlugin.Tools;

/// <summary>回复发出后要调用的服务及其参数，由插件工具的结果给出。</summary>
internal sealed record PluginToolFollowUp(string Service, JsonNode? Arguments);

/// <summary>
/// 其他插件以 "chat.tool.名称" 按名称提供的工具。AIChat 只按约定交换 JSON，不引用提供方的任何类型，
/// 提供方可以随时卸载或更新；调用时它不在，就让模型改用文字回复。
/// <para>服务描述：<c>description</c>（必填）、<c>parameters</c>（JSON Schema）、<c>prompt</c>（追加到系统提示）、
/// <c>max_calls_per_turn</c>。调用参数：<c>{ arguments, context }</c>。
/// 结果：<c>content</c> 交给模型；可选 <c>after_reply: { service, arguments }</c>，在文字回复发出后调用。</para>
/// </summary>
internal sealed partial class PluginChatTool : IChatTool
{
    internal const string ServicePrefix = "chat.tool.";

    private readonly IPluginServices _services;
    private readonly string _service;
    private readonly JsonObject _context;

    private PluginChatTool(IPluginServices services, string service, string name, string description,
        JsonObject parameters, string prompt, int maxCalls, JsonObject context)
    {
        _services = services;
        _service = service;
        _context = context;
        SystemPrompt = prompt;
        MaxCallsPerTurn = maxCalls;
        Definition = new OpenAiTool
        {
            Function = new OpenAiToolFunction { Name = name, Description = description, Parameters = parameters }
        };
    }

    public OpenAiTool Definition { get; }
    public string SystemPrompt { get; }
    public int MaxCallsPerTurn { get; }

    /// <summary>本轮工具调用要求在回复后执行的服务，按调用顺序。</summary>
    public List<PluginToolFollowUp> FollowUps { get; } = [];

    /// <summary>列出其他插件当前提供的工具；名称与 <paramref name="reserved"/> 或彼此冲突、或描述不完整的跳过。</summary>
    public static List<PluginChatTool> Discover(IPluginServices services, JsonObject context,
        ISet<string> reserved, IReadOnlyCollection<string> disabled)
    {
        var tools = new List<PluginChatTool>();
        var names = new HashSet<string>(reserved, StringComparer.Ordinal);
        foreach (var service in services.Find(ServicePrefix))
        {
            var name = service.Name[ServicePrefix.Length..];
            if (!ToolName().IsMatch(name) || disabled.Contains(name, StringComparer.OrdinalIgnoreCase) || !names.Add(name))
                continue;
            var metadata = service.Metadata as JsonObject;
            var description = StringValue(metadata?["description"]);
            if (string.IsNullOrWhiteSpace(description)) continue;
            var parameters = metadata?["parameters"] is JsonObject schema
                ? McpChatTool.Schema(schema)
                : new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
            var maxCalls = metadata?["max_calls_per_turn"] is JsonValue value && value.TryGetValue<int>(out var count) ? count : 1;
            tools.Add(new PluginChatTool(services, service.Name, name,
                $"[{service.ProviderId}] " + (description.Length > 1000 ? description[..1000] + "…" : description),
                parameters, StringValue(metadata?["prompt"]) ?? "", Math.Clamp(maxCalls, 1, 5), context));
        }
        return tools;
    }

    public async Task<string> ExecuteAsync(string arguments, CancellationToken ct = default)
    {
        JsonObject args;
        try
        {
            args = string.IsNullOrWhiteSpace(arguments) ? new JsonObject()
                : JsonNode.Parse(arguments) as JsonObject ?? throw new JsonException();
        }
        catch (JsonException)
        {
            return "Tool arguments must be a JSON object.";
        }

        JsonNode? result;
        try
        {
            result = await _services.CallAsync(_service, new JsonObject
            {
                ["arguments"] = args,
                ["context"] = _context.DeepClone()
            }, ct).ConfigureAwait(false);
        }
        catch (PluginServiceUnavailableException)
        {
            return "This tool is not available right now. Reply with text instead.";
        }
        catch (PluginServiceException ex)
        {
            return $"Tool failed: {ex.RemoteMessage}. Reply with text instead.";
        }

        if (result?["after_reply"] is JsonObject after && StringValue(after["service"]) is { Length: > 0 } service)
            FollowUps.Add(new PluginToolFollowUp(service, after["arguments"]?.DeepClone()));
        return result?["content"] switch
        {
            null => "ok",
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            var node => node.ToJsonString()
        };
    }

    private static string? StringValue(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    // OpenAI 函数名：字母、数字、下划线和连字符，最长 64。
    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex ToolName();

    public void Dispose() { }
}
