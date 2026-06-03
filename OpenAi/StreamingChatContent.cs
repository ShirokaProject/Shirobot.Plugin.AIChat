using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ShiroBot.SDK.Abstractions;

namespace ShiroBot.AiChatPlugin.OpenAi;

/// <summary>
/// 自定义 HttpContent，流式写 JSON body。图片 data URL 从 .b64 文件直接流式拷贝到 HTTP 流，
/// 不在托管堆上产生完整的大 string。
/// </summary>
internal sealed class StreamingChatContent : HttpContent
{
    private readonly string _model;
    private readonly List<ChatMessageDescriptor> _messages;

    public StreamingChatContent(string model, List<ChatMessageDescriptor> messages)
    {
        _model = model;
        _messages = messages;
        Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        // 用不带 BOM 的 UTF-8 编码
        var writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 8192, leaveOpen: true);

        await writer.WriteAsync("{\"model\":").ConfigureAwait(false);
        await writer.WriteAsync(JsonEncode(_model)).ConfigureAwait(false);
        await writer.WriteAsync(",\"stream\":false,\"messages\":[").ConfigureAwait(false);

        for (var i = 0; i < _messages.Count; i++)
        {
            if (i > 0) await writer.WriteAsync(',').ConfigureAwait(false);
            await WriteMessageAsync(writer, stream, _messages[i]).ConfigureAwait(false);
        }

        await writer.WriteAsync("]}").ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = -1;
        return false; // chunked transfer
    }

    private static async Task WriteMessageAsync(StreamWriter writer, Stream rawStream, ChatMessageDescriptor msg)
    {
        await writer.WriteAsync("{\"role\":").ConfigureAwait(false);
        await writer.WriteAsync(JsonEncode(msg.Role)).ConfigureAwait(false);
        await writer.WriteAsync(",\"content\":").ConfigureAwait(false);

        if (!msg.HasImage && msg.Segments.Count == 1 && msg.Segments[0].Kind == SegmentKind.Text)
        {
            // 纯文本消息：content 为 string
            await writer.WriteAsync(JsonEncode(msg.Segments[0].Text ?? "")).ConfigureAwait(false);
        }
        else
        {
            // 多模态消息：content 为数组
            await writer.WriteAsync('[').ConfigureAwait(false);
            var first = true;
            foreach (var seg in msg.Segments)
            {
                if (!first) await writer.WriteAsync(',').ConfigureAwait(false);
                first = false;

                if (seg.Kind == SegmentKind.Text)
                {
                    await writer.WriteAsync("{\"type\":\"text\",\"text\":").ConfigureAwait(false);
                    await writer.WriteAsync(JsonEncode(seg.Text ?? "")).ConfigureAwait(false);
                    await writer.WriteAsync('}').ConfigureAwait(false);
                }
                else // Image
                {
                    await writer.WriteAsync("{\"type\":\"image_url\",\"image_url\":{\"url\":\"").ConfigureAwait(false);

                    // Flush writer buffer 到底层 stream，然后直接写文件内容
                    await writer.FlushAsync().ConfigureAwait(false);

                    if (seg.B64FilePath is not null && File.Exists(seg.B64FilePath))
                    {
                        // 从 .b64 文件流式拷贝（文件内容就是 data:mime;base64,...）
                        // data URL 字符集不需要 JSON 转义，可以直接写入
                        await using var fs = new FileStream(seg.B64FilePath, FileMode.Open, FileAccess.Read,
                            FileShare.Read, bufferSize: 16384, useAsync: true);
                        await fs.CopyToAsync(rawStream).ConfigureAwait(false);
                        await rawStream.FlushAsync().ConfigureAwait(false);
                    }
                    else if (seg.InlineDataUrl is not null)
                    {
                        // fallback: 已经在内存里的 data URL（仅缓存写入失败时）
                        var bytes = Encoding.UTF8.GetBytes(seg.InlineDataUrl);
                        await rawStream.WriteAsync(bytes).ConfigureAwait(false);
                    }

                    // 继续用 writer 写闭合部分（writer 内部 buffer 已 flush，位置正确）
                    await writer.WriteAsync("\"}}").ConfigureAwait(false);
                }
            }
            await writer.WriteAsync(']').ConfigureAwait(false);
        }

        await writer.WriteAsync('}').ConfigureAwait(false);
    }

    /// <summary>
    /// 简单 JSON string 编码（带引号）。处理转义字符。
    /// </summary>
    private static string JsonEncode(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20)
                        sb.Append($"\\u{(int)c:X4}");
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
