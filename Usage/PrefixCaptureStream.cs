using System.Security.Cryptography;

namespace ShiroBot.AiChatPlugin.Usage;

/// <param name="Tokens">到这个边界为止的估算 token（图片按固定数量计），见 <see cref="TokenEstimate"/>。</param>
internal sealed record PrefixPoint(long Bytes, string Hash, long Tokens = 0);
internal sealed record PrefixSnapshot(long Bytes, PrefixPoint[] Points, long Tokens = 0)
{
    public long CommonBytes(PrefixSnapshot? previous) => Common(previous)?.Bytes ?? 0;

    public long CommonTokens(PrefixSnapshot? previous) => Common(previous)?.Tokens ?? 0;

    /// <summary>和上一次请求相同的最后一个完整边界。</summary>
    private PrefixPoint? Common(PrefixSnapshot? previous)
    {
        if (previous is null) return null;
        PrefixPoint? common = null;
        for (var i = 0; i < Points.Length && i < previous.Points.Length; i++)
        {
            if (Points[i] != previous.Points[i]) break;
            common = Points[i];
        }
        return common;
    }
}

/// <summary>在实际发送时对工具和消息流做摘要；不额外读取图片，也不保留正文。</summary>
internal sealed class PrefixCaptureStream(Stream inner) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly List<PrefixPoint> _points = [];
    private long _bytes;
    private long _tokens;
    /// <param name="tokens">上一个边界到这里的内容估算 token。</param>
    public void Checkpoint(long tokens = 0)
    {
        _tokens += tokens;
        // 极长上下文只保留前 4096 个完整边界，复用量仍是保守值。
        if (_points.Count < 4096) _points.Add(new(_bytes, Convert.ToHexString(_hash.GetCurrentHash()), _tokens));
    }
    public PrefixSnapshot Snapshot() => new(_bytes, _points.ToArray(), _tokens);
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await inner.WriteAsync(buffer, ct).ConfigureAwait(false);
        _hash.AppendData(buffer.Span); _bytes += buffer.Length;
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override void Write(byte[] buffer, int offset, int count) { inner.Write(buffer, offset, count); _hash.AppendData(buffer, offset, count); _bytes += count; }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
