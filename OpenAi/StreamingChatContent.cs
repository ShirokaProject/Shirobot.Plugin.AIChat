using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShiroBot.AiChatPlugin.OpenAi;

/// <summary>Streams all request variants, including tools and historical images.</summary>
internal sealed class StreamingChatContent : HttpContent
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        TypeInfoResolver = PluginJson.Resolver,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    static StreamingChatContent() => JsonOptions.Converters.Add(new AnonymousJsonConverterFactory());
    private OpenAiChatRequest? _request;

    public StreamingChatContent(OpenAiChatRequest request)
    {
        _request = request;
        Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
    }

    public StreamingChatContent(string model, List<ChatMessageDescriptor> messages,
        bool stream = false, int? maxCompletionTokens = null)
        : this(new OpenAiChatRequest
        {
            Model = model, Stream = stream, MaxCompletionTokens = maxCompletionTokens,
            Messages = messages.Select(m => new OpenAiMessage { Role = m.Role, Content = m }).ToList()
        }) { }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
    {
        var request = _request ?? throw new ObjectDisposedException(nameof(StreamingChatContent));
        // Small metadata is serialized separately; an image never enters a complete JSON buffer.
        await WriteAsync(stream, "{\"model\":", ct);
        await JsonSerializer.SerializeAsync(stream, request.Model, JsonOptions, ct);
        await WriteAsync(stream, request.Stream ? ",\"stream\":true" : ",\"stream\":false", ct);
        if (request.Temperature is {} temperature)
        {
            await WriteAsync(stream, ",\"temperature\":", ct);
            await JsonSerializer.SerializeAsync(stream, temperature, JsonOptions, ct);
        }
        if (request.MaxCompletionTokens is {} tokens)
        {
            await WriteAsync(stream, ",\"max_completion_tokens\":", ct);
            await JsonSerializer.SerializeAsync(stream, tokens, JsonOptions, ct);
        }
        if (request.Tools is {} tools)
        {
            await WriteAsync(stream, ",\"tools\":", ct);
            var schemas = tools.Select(tool => new OpenAiTool
            {
                Type = tool.Type,
                Function = new OpenAiToolFunction
                {
                    Name = tool.Function.Name, Description = tool.Function.Description,
                    Parameters = PluginJson.ToJsonValue(tool.Function.Parameters)!
                }
            }).ToList();
            await JsonSerializer.SerializeAsync(stream, schemas, JsonOptions, ct);
        }
        await WriteAsync(stream, ",\"messages\":[", ct);
        for (int i = 0; i < request.Messages.Count; i++)
        {
            if (i != 0) await WriteAsync(stream, ",", ct);
            var message = request.Messages[i];
            await WriteAsync(stream, "{\"role\":", ct);
            await JsonSerializer.SerializeAsync(stream, message.Role, JsonOptions, ct);
            await WriteAsync(stream, ",\"content\":", ct);
            if (message.Content is ChatMessageDescriptor descriptor)
                await WriteContentAsync(stream, descriptor, ct);
            else
                await JsonSerializer.SerializeAsync(stream, message.Content, JsonOptions, ct);
            if (message.ToolCallId is {} callId)
            {
                await WriteAsync(stream, ",\"tool_call_id\":", ct);
                await JsonSerializer.SerializeAsync(stream, callId, JsonOptions, ct);
            }
            if (message.ReasoningContent is {} reasoning)
            {
                await WriteAsync(stream, ",\"reasoning_content\":", ct);
                await JsonSerializer.SerializeAsync(stream, reasoning, JsonOptions, ct);
            }
            if (message.ToolCalls is {} calls)
            {
                await WriteAsync(stream, ",\"tool_calls\":", ct);
                await JsonSerializer.SerializeAsync(stream, calls, JsonOptions, ct);
            }
            await WriteAsync(stream, "}", ct);
        }
        await WriteAsync(stream, "]}", ct);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _request = null;
        base.Dispose(disposing);
    }

    protected override bool TryComputeLength(out long length) { length = 0; return false; }

    private static Task WriteAsync(Stream stream, string text, CancellationToken ct)
        => stream.WriteAsync(Encoding.UTF8.GetBytes(text), ct).AsTask();

    private static async Task WriteContentAsync(Stream stream, ChatMessageDescriptor message, CancellationToken ct)
    {
        if (message.Segments is [{ Kind: SegmentKind.Text } textSegment])
        {
            await JsonSerializer.SerializeAsync(stream, textSegment.Text ?? "", JsonOptions, ct);
            return;
        }
        await WriteAsync(stream, "[", ct);
        for (int i = 0; i < message.Segments.Count; i++)
        {
            if (i != 0) await WriteAsync(stream, ",", ct);
            var segment = message.Segments[i];
            if (segment.Kind == SegmentKind.Text)
            {
                await WriteAsync(stream, "{\"type\":\"text\",\"text\":", ct);
                await JsonSerializer.SerializeAsync(stream, segment.Text ?? "", JsonOptions, ct);
                await WriteAsync(stream, "}", ct);
            }
            else
            {
                await WriteAsync(stream, "{\"type\":\"image_url\",\"image_url\":{\"url\":\"", ct);
                if (segment.B64FilePath is {} path)
                {
                    // These files are generated internally and contain only a data-URL prefix and base64.
                    await using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await file.CopyToAsync(stream, 16384, ct);
                }
                else if (segment.InlineBytes is {} bytes)
                {
                    await WriteAsync(stream, $"data:{segment.Mime};base64,", ct);
                    var buffer = ArrayPool<byte>.Shared.Rent(16384);
                    try
                    {
                        const int chunk = 12 * 1024; // divisible by three; 16 KiB base64 output
                        for (int offset = 0; offset < bytes.Length; offset += chunk)
                        {
                            ct.ThrowIfCancellationRequested();
                            var count = Math.Min(chunk, bytes.Length - offset);
                            System.Buffers.Text.Base64.EncodeToUtf8(bytes.AsSpan(offset, count), buffer, out _, out int written);
                            await stream.WriteAsync(buffer.AsMemory(0, written), ct);
                        }
                    }
                    finally { ArrayPool<byte>.Shared.Return(buffer); }
                }
                else throw new InvalidDataException("Image has no cached or inline data.");
                await WriteAsync(stream, "\"}}", ct);
            }
        }
        await WriteAsync(stream, "]", ct);
    }
}
