using System.Collections.Concurrent;
using ShiroBot.SDK.Models;

namespace ShiroBot.AiChatPlugin.Conversation;

/// <summary>按原消息跟踪请求；适配器实例和会话参与匹配，避免误取消其他账号或群的请求。</summary>
internal sealed class PendingAiRequests
{
    private readonly ConcurrentDictionary<RequestKey, PendingAiRequest> _requests = new();
    public int Count => _requests.Count;

    public PendingAiRequest Register(MessageEvent message)
    {
        var key = new RequestKey(message.InstanceId, message.Platform, message.Channel.Type,
            message.Channel.Id, message.MessageId);
        PendingAiRequest? request = null;
        request = new PendingAiRequest(() => _requests.TryRemove(new KeyValuePair<RequestKey, PendingAiRequest>(key, request!)));
        if (_requests.TryAdd(key, request)) return request;
        request.Dispose();
        throw new InvalidOperationException("同一消息已有待处理的 AI 请求。");
    }

    public Task CancelBeforeReplyAsync(MessageDeletedEvent message)
    {
        var key = new RequestKey(message.InstanceId, message.Platform, message.Channel.Type,
            message.Channel.Id, message.MessageId);
        return _requests.TryGetValue(key, out var request)
            ? request.CancelBeforeReplyAsync() : Task.CompletedTask;
    }

    private readonly record struct RequestKey(string? InstanceId, string Platform,
        ChannelType ChannelType, string ChannelId, string MessageId);
}

internal sealed class PendingAiRequest : IDisposable
{
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private bool _replyStarted;
    private bool _disposed;
    private readonly Action _onDispose;
    public CancellationToken Token { get; }

    public PendingAiRequest(Action onDispose)
    {
        _onDispose = onDispose;
        Token = _cancellation.Token;
    }

    public bool TryBeginReply()
    {
        lock (_gate)
        {
            if (_disposed || _cancellation.IsCancellationRequested) return false;
            _replyStarted = true;
            return true;
        }
    }

    public Task CancelBeforeReplyAsync()
    {
        lock (_gate)
        {
            if (_disposed || _replyStarted || _cancellation.IsCancellationRequested) return Task.CompletedTask;
            return _cancellation.CancelAsync();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _onDispose();
            _cancellation.Dispose();
        }
    }
}
