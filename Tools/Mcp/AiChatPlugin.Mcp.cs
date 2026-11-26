using ShiroBot.AiChatPlugin.Tools;
using ShiroBot.AiChatPlugin.Tools.Mcp;
using ShiroBot.AiChatPlugin.Proactive;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Models;

namespace ShiroBot.AiChatPlugin;

/// <summary>MCP 服务和 #mcp 命令。</summary>
public sealed partial class AiChatPlugin
{
    // -------- MCP --------

    /// <summary>配置变化时重建 MCP 连接；旧连接在后台关闭，卸载时等待。</summary>
    private void UpdateMcpHub()
    {
        var config = _config.ToolUse.Mcp;
        var key = _config.ToolUse.Enabled && config.Enabled ? PluginJson.Serialize(config) : null;
        if (key == _mcpConfigKey) return;
        _mcpConfigKey = key;
        if (_mcp is { } old)
        {
            _mcp = null;
            lock (_backgroundGate)
            {
                var task = Task.Run(async () => await old.DisposeAsync().ConfigureAwait(false));
                _recallTasks.Add(task);
                _ = task.ContinueWith(completed =>
                {
                    lock (_backgroundGate) _recallTasks.Remove(completed);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        if (key is null) return;
        BotLog.Warning("[AiChat] ⚠️ 已启用 MCP 工具：模型可以调用机器人所在电脑上配置的 MCP 服务，可能读写文件、执行命令或访问网络。" +
            (config.AllowEveryone ? "当前所有人都能让模型调用，风险很高！" : "当前仅所有者/管理员发起的对话可用。"));
        _mcp = new McpHub(config, typeof(AiChatPlugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");
    }

    /// <summary>本次请求可用的 MCP 工具：主动插话不提供；默认只对所有者/管理员提供。</summary>
    /// <summary>
    /// 启用的 MCP 工具。列表不随发言人变化，前缀缓存才能命中；本次是否允许调用由 <see cref="McpRefusal"/> 决定。
    /// </summary>
    private List<IChatTool> McpTools()
    {
        var config = _config.ToolUse.Mcp;
        if (_mcp is not { } hub || !_config.ToolUse.Enabled || !config.Enabled) return [];
        var maxCalls = int.MaxValue;
        return hub.Tools.Select(t => (IChatTool)new McpChatTool(hub, t.ToolName, t.Server, t.Tool, maxCalls)).ToList();
    }

    /// <summary>本次请求不能调用 MCP 工具的原因：主动插话从不允许；没有开启 allow_everyone 时只有管理员可以调用。</summary>
    private string? McpRefusal(MessageEvent message, ProactiveRequest? proactive) =>
        proactive is not null ? "主动插话时不能使用 MCP 工具，请直接回复。"
        : !_config.ToolUse.Mcp.AllowEveryone && !Context.IsAdmin(message.Sender.Id) ? "只有机器人所有者或管理员可以使用 MCP 工具，请告诉用户这一点。"
        : null;

    private async Task HandleMcpAsync(MessageEvent message)
    {
        if (!_config.ToolUse.Enabled || !_config.ToolUse.Mcp.Enabled) return;
        var isGroup = !message.IsDirect;
        if (!Context.IsAdmin(message.Sender.Id))
        {
            await ReplyAsync(message, isGroup, "仅机器人所有者或管理员可以查看 MCP 服务。").ConfigureAwait(false);
            return;
        }
        var config = _config.ToolUse.Mcp;
        var text = _mcp is null
            ? "MCP 工具未启用（tool_use.enabled 与 tool_use.mcp.enabled 都需要开启）。"
            : "MCP 服务" + (config.AllowEveryone ? "（⚠️ 所有人可用）" : "（仅所有者/管理员可用）") + "：\n" + _mcp.Describe();
        await ReplyAsync(message, isGroup, text).ConfigureAwait(false);
    }
}
