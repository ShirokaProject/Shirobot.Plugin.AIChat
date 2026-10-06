using ShiroBot.AiChatPlugin.OpenAi;

namespace ShiroBot.AiChatPlugin.Tools;

internal interface IChatTool : IDisposable
{
    OpenAiTool Definition { get; }
    string SystemPrompt { get; }
    int MaxCallsPerTurn { get; }
    Task<string> ExecuteAsync(string arguments, CancellationToken ct = default);
}

/// <summary>只读查询工具，可在定时推送生成期间使用。</summary>
internal interface IReadOnlyChatTool : IChatTool { }
