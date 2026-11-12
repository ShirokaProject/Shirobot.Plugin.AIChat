using ShiroBot.AiChatPlugin.OpenAi;

namespace ShiroBot.AiChatPlugin.Tools;

internal interface IChatTool : IDisposable
{
    OpenAiTool Definition { get; }
    string SystemPrompt { get; }
    // Legacy metadata only; scheduling uses the unified global round limit.
    int MaxCallsPerTurn { get; }
    // Pure scheduler hint; opt in only if this call can overlap arbitrary other parallel calls.
    bool IsConcurrencySafe(string arguments) => false;
    Task<string> ExecuteAsync(string arguments, CancellationToken ct = default);
}

/// <summary>只读查询工具，可在定时推送生成期间使用。</summary>
internal interface IReadOnlyChatTool : IChatTool { }

/// <summary>Reference images appended after all tool results, never between assistant tool calls and their results.</summary>
internal interface IToolAttachments
{
    IReadOnlyList<OpenAiMessage> TakeAttachments();
}
