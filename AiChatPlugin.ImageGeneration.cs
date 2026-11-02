using ShiroBot.AiChatPlugin.Conversation;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Abstractions;

namespace ShiroBot.AiChatPlugin;

public sealed partial class AiChatPlugin
{
    private readonly SemaphoreSlim _imageGenerationGate = new(1, 1);

    private async Task HandleImageGenAsync(MessageEvent message)
    {
        var text = message.GetPlainText().Trim();
        if (text.Length > 9 && !char.IsWhiteSpace(text[9])) return;
        var prompt = text.Length > 9 ? text[9..].Trim() : "";
        if (prompt.Length == 0) { await ReplyAsync(message, !message.IsDirect, "用法：#imagegen 提示词"); return; }
        using var pending = _pendingRequests.Register(message);
        try
        {
            var result = await GenerateAndSendImageAsync(message, prompt, pending, pending.Token).ConfigureAwait(false);
            if (result != "图片已生成并发送。" && pending.TryBeginReply())
                await ReplyAsync(message, !message.IsDirect, result).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (pending.Token.IsCancellationRequested) { }
    }

    private async Task<string> GenerateAndSendImageAsync(MessageEvent message, string prompt, PendingAiRequest pending, CancellationToken ct, string? size = null, string? quality = null)
    {
        var defaults = _config.ToolUse.ImageGen;
        var config = new Config.ImageGenerationSection { Enabled = defaults.Enabled, Model = defaults.Model,
            TimeoutSeconds = defaults.TimeoutSeconds, Size = size ?? defaults.Size, Quality = quality ?? defaults.Quality };
        if (!config.Enabled) return "生图功能未启用。";
        if (prompt.Length > 32000) return "生图提示词不能超过 32000 字符。";
        var model = ResolveModel(config.Model);
        if (model is null) return "请先配置已注册的生图模型。";
        var provider = _config.Providers.FirstOrDefault(p => string.Equals(p.Name, model.Provider, StringComparison.OrdinalIgnoreCase));
        var url = provider?.BaseUrl ?? model.BaseUrl;
        var key = provider?.ApiKey ?? model.ApiKey;
        if (string.IsNullOrWhiteSpace(url)) return "生图模型的接口地址未配置。";
        await _imageGenerationGate.WaitAsync(ct).ConfigureAwait(false);
        Func<Task>? clear = null;
        IDisposable? instance = null;
        try
        {
            instance = string.IsNullOrWhiteSpace(message.InstanceId) ? null : Context.UseInstance(message.InstanceId);
            clear = await ShowImageGenerationProgressAsync(message, ct).ConfigureAwait(false);
            var image = await ImageGenerationClient.GenerateAsync(url, key ?? "", model.Name, prompt, config,
                _config.Network.HttpProxy, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!pending.TryBeginReply()) throw new OperationCanceledException(ct);
            var sent = await Context.Message.SendMessageAsync(message.Channel,
                [new ImageSegment("base64://" + Convert.ToBase64String(image))], ct).ConfigureAwait(false);
            return sent.IsSuccess ? "图片已生成并发送。" : "图片已生成，但发送失败，请检查适配器的图片支持。";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return "生成图片超时，请稍后重新尝试。"; }
        catch (Exception ex)
        {
            BotLog.Warning($"[AiChat] 生图失败：{ex.GetType().Name}");
            return "生成图片失败，请检查生图接口、模型和图片返回格式。";
        }
        finally
        {
            if (clear is not null) try { await clear().ConfigureAwait(false); } catch { }
            instance?.Dispose();
            _imageGenerationGate.Release();
        }
    }

    private async Task<Func<Task>?> ShowImageGenerationProgressAsync(MessageEvent message, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var service = Context.GetAdapterExtension<IMessageReactionService>();
            if (service is not null && message.InstanceId is { Length: > 0 } id &&
                service.GetReactionCapabilities(message.Channel).HasFlag(ReactionCapabilities.Unicode))
            {
                var reference = new MessageReference(id, message.Channel, message.MessageId);
                var emoji = new UnicodeReactionEmoji("🎨");
                await service.SetReactionAsync(reference, emoji, true, timeout.Token).ConfigureAwait(false);
                return async () => { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await service.SetReactionAsync(reference, emoji, false, stop.Token).ConfigureAwait(false); };
            }
            if (!message.IsDirect && message.Platform.Equals("qq", StringComparison.OrdinalIgnoreCase) &&
                Context.GetAdapterExtension<IQGroupApi>() is { } qq)
            {
                await qq.SendMessageReactionAsync(message.Channel.Id, message.MessageId, "66", true, timeout.Token).ConfigureAwait(false);
                return async () => { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await qq.SendMessageReactionAsync(message.Channel.Id, message.MessageId, "66", false, stop.Token).ConfigureAwait(false); };
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested) { }
        ct.ThrowIfCancellationRequested();
        await Context.Message.SendMessageAsync(message.Channel, [new TextSegment("生成图片中...")], ct).ConfigureAwait(false);
        return null;
    }
}
