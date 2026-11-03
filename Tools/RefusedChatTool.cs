using ShiroBot.AiChatPlugin.OpenAi;

namespace ShiroBot.AiChatPlugin.Tools;

/// <summary>
/// 保留工具的定义和提示词，但拒绝本次请求里的调用。同一会话每次请求的工具列表和 system prompt 保持不变，
/// 上游的前缀缓存才能命中；主动插话、非管理员等不允许使用的情况在调用时拒绝，而不是从列表里去掉工具。
/// </summary>
internal sealed class RefusedChatTool(IChatTool inner, string reason) : IChatTool
{
    public OpenAiTool Definition => inner.Definition;
    public string SystemPrompt => inner.SystemPrompt;
    public int MaxCallsPerTurn => inner.MaxCallsPerTurn;

    public Task<string> ExecuteAsync(string arguments, CancellationToken ct = default) => Task.FromResult(reason);

    /// <summary>不拥有被包装的工具，由创建它的地方释放。</summary>
    public void Dispose() { }

    /// <summary><paramref name="reason"/> 为 null 时原样返回工具。</summary>
    public static IChatTool? Wrap(IChatTool? tool, string? reason) => tool is null || reason is null ? tool : new RefusedChatTool(tool, reason);
}
