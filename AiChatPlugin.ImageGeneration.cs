using ShiroBot.AiChatPlugin.Conversation;
using ShiroBot.AiChatPlugin.Resources;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Abstractions;

namespace ShiroBot.AiChatPlugin;

public sealed partial class AiChatPlugin
{
    private readonly SemaphoreSlim _imageGenerationGate = new(1, 1);

    private readonly PendingAiRequests _imageRequests = new();

    private Task HandleImageGenAsync(MessageEvent message)
    {
        var text = message.GetPlainText().Trim();
        if (text.Length > 9 && !char.IsWhiteSpace(text[9])) return Task.CompletedTask;
        var prompt = text.Length > 9 ? text[9..].Trim() : "";
        if (prompt.Length == 0) return ReplyAsync(message, !message.IsDirect, "用法：#imagegen 提示词");
        var result = QueueImageGeneration(message, prompt, null, null);
        return result == ImageJobQueued ? Task.CompletedTask : ReplyAsync(message, !message.IsDirect, result);
    }

    private const string ImageJobQueued = "生图任务已提交后台，完成后会单独发送图片。请勿声称已经生成成功；其他聊天可以继续。";

    internal static string CurrentImagePrompt(MessageEvent message)
    {
        // Deliberately use the current user's own text, not the tool's history-influenced rewrite.
        var text = message.GetPlainText().Trim();
        foreach (var prefix in new[] { "#imagegen", "#ai" })
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                (text.Length == prefix.Length || char.IsWhiteSpace(text[prefix.Length])))
                return text[prefix.Length..].Trim();
        return text;
    }

    private string QueueImageGeneration(MessageEvent message, string prompt, string? size, string? quality, IReadOnlyList<ImagePart>? references = null, ConversationKey? conversationKey = null)
    {
        if (!_config.ToolUse.ImageGen.Enabled) return "生图功能未启用。";
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 32000) return "请在当前消息写清生图要求（最多 32000 字符）。";
        PendingAiRequest pending;
        try { pending = _imageRequests.Register(message); }
        catch (InvalidOperationException) { return "这条消息已有生图任务，请等待完成。"; }
        try
        {
            _ = Context.Lifetime.Run(async stopping =>
            {
                using (pending)
                using (var stop = CancellationTokenSource.CreateLinkedTokenSource(stopping, pending.Token))
                {
                    try
                    {
                        using var instance = string.IsNullOrWhiteSpace(message.InstanceId) ? null : Context.UseInstance(message.InstanceId);
                        // Downloads are part of the background job, not the chat turn or adapter dispatch.
                        var parts = await new ResourceCollector(Context, _fetcher, _cache, _config.Resources)
                            .CollectAsync(message, stop.Token).ConfigureAwait(false);
                        var result = await GenerateAndSendImageAsync(message, prompt, pending, stop.Token, size, quality,
                            [.. parts.OfType<ImagePart>(), .. references ?? []], conversationKey).ConfigureAwait(false);
                        if (result != "图片已生成并发送。" && pending.TryBeginReply())
                            await ReplyAsync(message, !message.IsDirect, result).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                    catch (Exception ex)
                    {
                        BotLog.Warning($"[AiChat] 后台生图失败：{ex.GetType().Name}");
                        if (!stop.IsCancellationRequested && pending.TryBeginReply())
                            await ReplyAsync(message, !message.IsDirect, "生图任务失败，请重新附图或检查接口。").ConfigureAwait(false);
                    }
                }
            });
        }
        catch { pending.Dispose(); throw; }
        return ImageJobQueued;
    }

    private async Task<string> GenerateAndSendImageAsync(MessageEvent message, string prompt, PendingAiRequest pending, CancellationToken ct, string? size = null, string? quality = null, IReadOnlyList<ImagePart>? images = null, ConversationKey? conversationKey = null)
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
            if (images?.Count > 4) return "一次最多编辑 4 张参考图片，请减少附图数量。";
            var inputs = new List<ImageGenerationInput>();
            foreach (var part in images ?? [])
            {
                byte[] bytes;
                if (part.InlineBytes is { Length: > 0 } inline) bytes = inline;
                else if (GetB64FilePath(part.CachePath) is { } path)
                {
                    if (new FileInfo(path).Length > 28_000_000) return "参考图片过大。";
                    var data = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                    bytes = Convert.FromBase64String(data[(data.IndexOf(',') + 1)..]);
                }
                else return "参考图片已失效，请重新附图。";
                if (bytes.Length > 20_000_000) return "参考图片过大。";
                var mime = MimeGuesser.SniffImageMime(bytes);
                if (mime is not ("image/png" or "image/jpeg" or "image/webp")) return "编辑参考图支持 PNG、JPEG、WebP，请转换格式后重试。";
                inputs.Add(new ImageGenerationInput(bytes, mime));
            }
            clear = await ShowImageGenerationProgressAsync(message, ct).ConfigureAwait(false);
            var image = await ImageGenerationClient.GenerateAsync(url, key ?? "", model.Name, prompt, config,
                _config.Network.HttpProxy, ct, inputs).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!pending.TryBeginReply()) throw new OperationCanceledException(ct);
            var directory = Context.CreateTempDirectory(TimeSpan.FromHours(1));
            var outputMime = MimeGuesser.SniffImageMime(image) ?? "image/png";
            var outputPath = Path.Combine(directory.Path, "generated" + MimeGuesser.ExtensionForMime(outputMime));
            await File.WriteAllBytesAsync(outputPath, image, ct).ConfigureAwait(false);
            try
            {
                var sent = await Context.Message.SendMessageAsync(message.Channel,
                    [new ImageSegment(new Uri(Path.GetFullPath(outputPath)).AbsoluteUri) { FileName = Path.GetFileName(outputPath) }], ct).ConfigureAwait(false);
                if (!sent.IsSuccess)
                {
                    BotLog.Warning($"[AiChat] 生图已完成但发送失败，原图暂存：{outputPath}");
                    return "图片已生成，但发送失败；原图已暂存一小时，请管理员检查适配器上传。";
                }
                if (conversationKey is { } historyKey)
                {
                    try
                    {
                        var cached = await _cache.StoreAsync(image, MimeGuesser.ExtensionForMime(outputMime), outputMime).ConfigureAwait(false);
                        if (cached is not null)
                        {
                            var state = await _conversations.AcquireAsync(historyKey, ct).ConfigureAwait(false);
                            try
                            {
                                state.Turns.Add(new ChatTurn("assistant", [new TextPart("[生成图片：" + prompt + "]"),
                                    new ImagePart(cached, outputMime, image.Length)], DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                                await _conversations.RewriteAsync(state).ConfigureAwait(false);
                            }
                            finally { _conversations.Release(historyKey); }
                        }
                    }
                    catch (Exception ex) { BotLog.Warning($"[AiChat] 保存生成图片上下文失败：{ex.GetType().Name}"); }
                }
                try { File.Delete(outputPath); }
                catch (Exception ex) { BotLog.Warning($"[AiChat] 清理已发送图片失败：{ex.GetType().Name}，宿主会自动过期清理。"); }
                return "图片已生成并发送。";
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                BotLog.Warning($"[AiChat] 生图上传失败：{ex.GetType().Name}，原图暂存：{outputPath}");
                return "图片已生成，但上传失败；原图已暂存一小时，请管理员检查适配器上传。";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return "生成图片超时，请稍后重新尝试。"; }
        catch (ImageGenerationException ex)
        {
            BotLog.Warning($"[AiChat] {ex.Message}");
            return ex.Message;
        }
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
        var progress = await Context.Message.SendMessageAsync(message.Channel, [new TextSegment("生成图片中...")], ct).ConfigureAwait(false);
        if (!progress.IsSuccess || string.IsNullOrWhiteSpace(progress.MessageId)) return null;
        return async () =>
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await Context.Message.DeleteMessageAsync(message.Channel, progress.MessageId, stop.Token).ConfigureAwait(false); }
            catch (Exception ex) { BotLog.Warning($"[AiChat] 撤回生图等待消息失败：{ex.GetType().Name}"); }
        };
    }
}
