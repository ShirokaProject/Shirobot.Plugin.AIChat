using ShiroBot.AiChatPlugin.Config;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.AiChatPlugin.Delivery;

/// <summary>单次 AI 回复的官方发送能力和流式生命周期，不依赖具体 QQ 适配器。</summary>
internal sealed class OfficialReplyDelivery : IAsyncDisposable
{
    private IQOfficialMessageApi? _markdownApi;
    private IQOfficialMessageStream? _stream;
    private readonly QOfficialMessageTarget? _target;
    private readonly QOfficialMessageReply _reply;
    private readonly string _markdownPrefix = "";
    private readonly Func<bool>? _tryBeginReply;
    private readonly CancellationToken _cancellationToken;
    private DateTimeOffset _lastUpdate;
    private string? _lastText;
    private bool _plainTextOnly;

    public bool IsStreaming => _stream is not null;
    /// <summary>流式回复是否已经向用户推送过内容。</summary>
    public bool HasStreamed => _stream is not null && _lastText is not null;
    /// <summary>把累计生成文本转换为可推送的预览；返回 null 时暂不推送。</summary>
    public Func<string, string?>? PreviewFilter { get; init; }
    public bool MarkdownAvailable => !_plainTextOnly && _markdownApi is not null;

    public OfficialReplyDelivery(IBotContext context, MessageEvent message, BehaviourSection behaviour,
        Func<bool>? tryBeginReply = null, CancellationToken cancellationToken = default)
    {
        _tryBeginReply = tryBeginReply;
        _cancellationToken = cancellationToken;
        _reply = new QOfficialMessageReply { MessageId = message.MessageId };
        _target = message.Channel.Type switch
        {
            ChannelType.Direct => new(QOfficialMessageScene.Direct, message.Channel.Id),
            ChannelType.Group => new(QOfficialMessageScene.Group, message.Channel.Id),
            _ => null
        };
        if (_target is null) return;
        if (!message.IsDirect && behaviour.ReplyWithMention)
        {
            var id = message.Sender.Id;
            if (id.Length is > 0 and <= 128 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
                _markdownPrefix = $"<qqbot-at-user id=\"{id}\" />\n\n";
            else
                // 无法安全表示官方 @ 时，交给通用 MentionSegment 发送路径处理。
                _plainTextOnly = true;
        }
        try
        {
            if (behaviour.UseMarkdown)
            {
                var api = context.GetAdapterExtension<IQOfficialMessageApi>();
                if (api?.CanSendMarkdown(_target, new QCustomMarkdown("AI 回复")) == true)
                    _markdownApi = api;
            }
            if (behaviour.UseStreaming && message.IsDirect && !string.IsNullOrWhiteSpace(message.MessageId))
            {
                var api = context.GetAdapterExtension<IQOfficialDirectMessageApi>();
                _stream = api?.BeginStream(_target, _reply, MarkdownAvailable
                    ? QOfficialStreamContentType.Markdown : QOfficialStreamContentType.Text);
            }
        }
        catch (Exception ex)
        {
            _plainTextOnly = true;
            BotLog.Warning($"[AiChat] 官方回复能力不可用，使用普通文本: {ex.Message}");
        }
    }

    public static string ApplyPromptPolicy(string prompt, bool useMarkdown) =>
        useMarkdown ? prompt : prompt.TrimEnd() + "\n不要使用markdown";

    public async Task AppendAsync(string cumulativeText)
    {
        if (PreviewFilter is not null) cumulativeText = PreviewFilter(cumulativeText) ?? "";
        if (_stream is null || string.IsNullOrWhiteSpace(cumulativeText) || cumulativeText == _lastText) return;
        // 合并快速到达的 token，避免每个 token 都占用一个官方流式帧。
        if (_lastText is not null && DateTimeOffset.UtcNow - _lastUpdate < TimeSpan.FromMilliseconds(600)) return;
        _cancellationToken.ThrowIfCancellationRequested();
        if (_tryBeginReply?.Invoke() == false) throw new OperationCanceledException(_cancellationToken);
        try
        {
            await _stream.AppendAsync(cumulativeText, _cancellationToken).ConfigureAwait(false);
            _lastText = cumulativeText;
            _lastUpdate = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            BotLog.Warning($"[AiChat] 流式发送失败，完成生成后改用普通文本: {ex.Message}");
            await DisableStreamingAsync().ConfigureAwait(false);
        }
    }

    public async Task DisableStreamingAsync()
    {
        _plainTextOnly = true;
        await CloseStreamAsync().ConfigureAwait(false);
    }

    /// <summary>放弃尚未推送内容的流式回复，保留 Markdown 等其他官方发送能力。</summary>
    public Task AbandonStreamAsync() => HasStreamed ? Task.CompletedTask : CloseStreamAsync();

    private async Task CloseStreamAsync()
    {
        var stream = _stream;
        _stream = null;
        if (stream is null) return;
        // 已发送的前缀不能复用到另一轮模型重试或备用模型。
        try
        {
            if (stream.HasStarted) await stream.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception ex) { BotLog.Warning($"[AiChat] 结束中断流式回复失败: {ex.Message}"); }
        try { await stream.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { BotLog.Warning($"[AiChat] 释放流式回复失败: {ex.Message}"); }
    }

    /// <summary>返回 true 表示已通过官方接口发送完毕，否则由调用方发送普通文本。</summary>
    public async Task<bool> CompleteAsync(string text, bool mention = true)
    {
        if (_stream is not null)
        {
            try
            {
                if (_lastText != text) await _stream.AppendAsync(text).ConfigureAwait(false);
                var id = await _stream.CompleteAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("流式回复未返回消息 ID。");
                return true;
            }
            catch (Exception ex)
            {
                BotLog.Warning($"[AiChat] 完成流式回复失败，改用普通文本: {ex.Message}");
                await DisableStreamingAsync().ConfigureAwait(false);
                return false;
            }
        }
        if (MarkdownAvailable)
        {
            try
            {
                var markdown = new QCustomMarkdown((mention ? _markdownPrefix : "") + text);
                if (!_markdownApi!.CanSendMarkdown(_target!, markdown)) return false;
                var id = await _markdownApi.SendMarkdownAsync(_target!, markdown, reply: _reply).ConfigureAwait(false);
                return !string.IsNullOrWhiteSpace(id);
            }
            catch (Exception ex) { BotLog.Warning($"[AiChat] Markdown 发送失败，改用普通文本: {ex.Message}"); }
        }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_stream is null) return;
        try { await _stream.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { BotLog.Warning($"[AiChat] 释放流式回复失败: {ex.Message}"); }
        _stream = null;
    }
}
