using System.Text.Json;
using System.Text.Json.Nodes;
using ShiroBot.AiChatPlugin.Mcp;
using ShiroBot.AiChatPlugin.OpenAi;

namespace ShiroBot.AiChatPlugin.Tools;

/// <summary>把一个 MCP 工具包装成模型可调用的函数；连接由 McpHub 持有，这里不释放任何资源。</summary>
internal sealed class McpChatTool(McpHub hub, string toolName, string server, McpToolInfo tool, int maxCallsPerTurn) : IChatTool
{
    internal const string Prompt = "名称以 mcp_ 开头的工具来自机器人所在电脑上配置的 MCP 服务，可能会读写文件、执行命令或访问外部服务。" +
        "只在用户明确需要、且其他方式无法完成时调用；涉及删除、覆盖、发送、付款等不可撤销或有副作用的操作前，必须先向用户确认，不要自作主张。";

    public int MaxCallsPerTurn => maxCallsPerTurn;
    public string SystemPrompt => "";

    public OpenAiTool Definition { get; } = new()
    {
        Function = new OpenAiToolFunction
        {
            Name = toolName,
            Description = $"[MCP {server}] " + (tool.Description.Length > 1000 ? tool.Description[..1000] + "…" : tool.Description),
            Parameters = Schema(tool.InputSchema)
        }
    };

    /// <summary>参数必须是 object 类型；去掉部分上游不接受的 $schema。</summary>
    internal static JsonObject Schema(JsonObject input)
    {
        var schema = (JsonObject)input.DeepClone();
        schema.Remove("$schema");
        schema["type"] = "object";
        if (schema["properties"] is not JsonObject) schema["properties"] = new JsonObject();
        return schema;
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
        try
        {
            return await hub.CallAsync(server, tool.Name, args, ct).ConfigureAwait(false);
        }
        catch (McpException ex)
        {
            return $"MCP call failed: {ex.Message}";
        }
    }

    public void Dispose() { }
}
