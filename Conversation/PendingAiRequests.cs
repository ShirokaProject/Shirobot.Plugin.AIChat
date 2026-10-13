using System.Collections.Concurrent;
using ShiroBot.SDK.Models;

namespace ShiroBot.AiChatPlugin.Conversation;

/// <summary>按原消息跟踪请求；适配器实例和会话参与匹配，避免误取消其他账号或群的请求。</summary>
internal sealed class PendingAiRequests
{
    private readonly ConcurrentDictionary<RequestKey, PendingAiRequest> _requests = new();
    private readonly Lock _gate = new();
    private bool _stopping;
    public int Count => _requests.Count;

    public async Task StopAsync()
    {
        PendingAiRequest[] requests;
        lock (_gate) { _stopping = true; requests = _requests.Values.ToArray(); }
        await Task.WhenAll(requests.Select(r => r.CancelForUnloadAsync())).ConfigureAwait(false);
        await Task.WhenAll(requests.Select(r => r.Completion)).ConfigureAwait(false);
    }

    public PendingAiRequest Register(MessageEvent message)
    {
        var key = new RequestKey(message.InstanceId, message.Platform, message.Channel.Type,
            message.Channel.Id, message.MessageId);
        PendingAiRequest? request = null;
        request = new PendingAiRequest(() => _requests.TryRemove(new KeyValuePair<RequestKey, PendingAiRequest>(key, request!)));
        lock (_gate)
        {
            if (!_stopping && _requests.TryAdd(key, request)) return request;
        }
        request.Dispose();
        if (_stopping) throw new OperationCanceledException("Plugin is unloading.");
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
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Completion => _completion.Task;
    public CancellationToken Token { get; }

    public Task CancelForUnloadAsync()
    {
        lock (_gate)
            return _disposed || _cancellation.IsCancellationRequested ? Task.CompletedTask : _cancellation.CancelAsync();
    }

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
            _completion.TrySetResult();
        }
    }
}
