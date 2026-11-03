using System.Security.Cryptography;

namespace ShiroBot.AiChatPlugin.Usage;

internal sealed record PrefixPoint(long Bytes, string Hash);
internal sealed record PrefixSnapshot(long Bytes, PrefixPoint[] Points)
{
    public long CommonBytes(PrefixSnapshot? previous)
    {
        if (previous is null) return 0;
        long common = 0;
        for (var i = 0; i < Points.Length && i < previous.Points.Length; i++)
        {
            if (Points[i] != previous.Points[i]) break;
            common = Points[i].Bytes;
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
    public void Checkpoint()
    {
        // 极长上下文只保留前 4096 个完整边界，复用量仍是保守值。
        if (_points.Count < 4096) _points.Add(new(_bytes, Convert.ToHexString(_hash.GetCurrentHash())));
    }
    public PrefixSnapshot Snapshot() => new(_bytes, _points.ToArray());
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
