using System.Text.Json;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.Meme.Contracts;
using ShiroBot.SDK.Models;

namespace ShiroBot.AiChatPlugin.Tools;

/// <summary>
/// 让模型从 MemePlugin 的本地表情库中挑选一张表情。工具只负责选中，不直接发送；
/// 回复管线在最终文本确定后统一发送，避免工具副作用和空回复。
/// </summary>
internal sealed class MemeTool(IMemeService service, MessageEvent message, string usePolicy = "auto") : IChatTool
{
    public MemeSelection? Selected { get; private set; }
    public string? SuggestedCaption { get; private set; }

    public int MaxCallsPerTurn => 1;

    public string SystemPrompt => string.Equals(usePolicy, "explicit", StringComparison.OrdinalIgnoreCase)
        ? "你有一个本地表情库。只有在用户明确要求发表情、斗图或使用表情时才调用 send_meme；" +
          "用户没有明确要求时不要主动发。每次回复最多一张，不要连续发。调用后最终回复只写一句很短的点评或接梗，" +
          "不要描述图片内容、标签或 URL；如果工具返回没有合适表情，就正常用文字回复。"
        : "你有一个本地表情库。回复别人时，如果语气轻松、在接梗、吐槽、卖萌、打招呼或表达情绪，应主动调用 send_meme 配一张表情，" +
          "不需要等用户明确要求。严肃回答、求助、工作/学习问题不要发；每次回复最多一张，不要连续发。" +
          "调用后最终回复只写一句很短的点评或接梗，不要描述图片内容、标签或 URL；如果工具返回没有合适表情，就正常用文字回复。";

    public OpenAiTool Definition { get; } = new()
    {
        Function = new OpenAiToolFunction
        {
            Name = "send_meme",
            Description = "Select one suitable meme from the local meme library for the current chat. Use proactively in casual conversation, banter, teasing, or emotional replies; do not use for serious answers. The tool only selects; the reply pipeline sends it.",
            Parameters = new
            {
                type = "object",
                additionalProperties = false,
                required = new[] { "query" },
                properties = new
                {
                    query = new { type = "string", description = "Short Chinese keyword(s) describing the desired meme, e.g. 猫, 无语, 大笑, 摸鱼." },
                    emotion = new { type = "string", @enum = new[] { "happy", "sad", "angry", "confused", "shy", "daily" }, description = "Optional emotion category." },
                    caption = new { type = "string", description = "Optional short caption to send with the meme; normally leave empty and write the caption in the final reply." },
                },
            },
        },
    };

    public async Task<string> ExecuteAsync(string arguments, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (Selected is not null)
            return "A meme has already been selected for this reply. Do not call send_meme again.";

        using var doc = JsonDocument.Parse(arguments);
        var root = doc.RootElement;
        var query = GetString(root, "query")?.Trim() ?? "";
        var emotion = GetString(root, "emotion")?.Trim();
        var caption = GetString(root, "caption")?.Trim();

        if (query.Length == 0 && string.IsNullOrWhiteSpace(emotion))
            return "query or emotion is required.";

        try
        {
            var selection = await service.SelectAsync(
                new MemeQuery(query, emotion, message.Channel.Id, message.Sender.Id, 8), ct).ConfigureAwait(false);
            if (selection is null)
                return "No suitable meme is available right now, or this conversation is in cooldown. Reply with text instead.";

            Selected = selection;
            SuggestedCaption = caption;
            return PluginJson.Serialize(new
            {
                status = "selected",
                id = selection.Id,
                caption = selection.Caption,
                tags = selection.Tags,
                note = "The meme will be sent by the reply pipeline. Keep the final reply to one short sentence; do not describe the image.",
            });
        }
        catch (Exception ex)
        {
            return $"Meme service failed: {ex.Message}. Reply with text instead.";
        }
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public void Dispose() { }
}
