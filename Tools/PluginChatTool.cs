using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ShiroBot.AiChatPlugin.Contracts;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.AiChatPlugin.Tools;

/// <summary>
/// 其他插件通过 <see cref="ChatTools.Tools"/> 提供的工具。AIChat 不引用提供方的任何类型，
/// 提供方可以随时卸载或更新；调用时它不在，就让模型改用文字回复。
/// </summary>
internal sealed partial class PluginChatTool : IChatTool
{
    private readonly IBotContext _context;
    private readonly PluginServiceEntry<ChatToolCall, ChatToolResult, ChatToolInfo> _entry;
    private readonly ChatToolContext _chat;

    private PluginChatTool(IBotContext context, PluginServiceEntry<ChatToolCall, ChatToolResult, ChatToolInfo> entry,
        ChatToolInfo info, ChatToolContext chat)
    {
        _context = context;
        _entry = entry;
        _chat = chat;
        SystemPrompt = info.Prompt ?? "";
        MaxCallsPerTurn = Math.Clamp(info.MaxCallsPerTurn, 1, 5);
        var description = info.Description.Length > 1000 ? info.Description[..1000] + "…" : info.Description;
        Definition = new OpenAiTool
        {
            Function = new OpenAiToolFunction
            {
                Name = entry.ShortName,
                Description = $"[{entry.ProviderId}] {description}",
                Parameters = info.Parameters is { } schema
                    ? McpChatTool.Schema(schema)
                    : new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
            }
        };
    }

    public OpenAiTool Definition { get; }
    public string SystemPrompt { get; }
    public int MaxCallsPerTurn { get; }

    /// <summary>本轮工具调用要求在回复后执行的动作，按调用顺序。</summary>
    public List<ChatAfterReply> FollowUps { get; } = [];

    /// <summary>列出其他插件当前提供的工具；名称与 <paramref name="reserved"/> 或彼此冲突、或说明为空的跳过。</summary>
    public static List<PluginChatTool> Discover(IBotContext context, ChatToolContext chat,
        ISet<string> reserved, IReadOnlyCollection<string> disabled)
    {
        var tools = new List<PluginChatTool>();
        var names = new HashSet<string>(reserved, StringComparer.Ordinal);
        foreach (var entry in ChatTools.Tools.Find(context))
        {
            if (!ToolName().IsMatch(entry.ShortName) || disabled.Contains(entry.ShortName, StringComparer.OrdinalIgnoreCase)
                || entry.Metadata is not { Description.Length: > 0 } info || !names.Add(entry.ShortName))
                continue;
            tools.Add(new PluginChatTool(context, entry, info, chat));
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

        ChatToolResult? result;
        try
        {
            result = await _entry.TryCallAsync(_context, new ChatToolCall(args, _chat), ct).ConfigureAwait(false);
        }
        catch (PluginServiceException ex)
        {
            return $"Tool failed: {ex.RemoteMessage}. Reply with text instead.";
        }
        if (result is null) return "This tool is not available right now. Reply with text instead.";

        if (result.AfterReply is { Name.Length: > 0 } after) FollowUps.Add(after);
        return result.Content;
    }

    // OpenAI 函数名：字母、数字、下划线和连字符，最长 64。
    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex ToolName();

    public void Dispose() { }
}
