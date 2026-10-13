using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ShiroBot.AiChatPlugin;
using ShiroBot.AiChatPlugin.Conversation;
using ShiroBot.AiChatPlugin.OpenAi;

internal static class MemoryStreamingChecks
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static async Task RunAsync()
    {
        var disposedContent = CreateDisposedContent(out var payload);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Check(!payload.IsAlive, "Disposing request content must release its payload even if the content is retained.");
        GC.KeepAlive(disposedContent);
        Console.WriteLine("PASS: DisposeReleasesPayloadWhileContentRemainsAlive");
        var pending = new PendingAiRequests();
        var active = pending.Register(new ShiroBot.SDK.Models.MessageEvent
        {
            InstanceId = "test", Platform = "qq", MessageId = "active", SelfId = "bot",
            Channel = ShiroBot.SDK.Models.Channel.Direct("user"), Sender = new ShiroBot.SDK.Models.User("user"), Segments = []
        });
        Check(active.TryBeginReply(), "Active reply should begin.");
        var stop = pending.StopAsync();
        Check(active.Token.IsCancellationRequested && !stop.IsCompleted, "Unload must cancel even active replies and wait for cleanup.");
        active.Dispose();
        await stop;
        Check(pending.Count == 0, "Unload must drain pending requests.");
        Console.WriteLine("PASS: UnloadCancelsAndDrainsActiveRequests");
        var root = Path.Combine(Path.GetTempPath(), "aichat-memory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bytes = new byte[4 * 1024 * 1024 + 1];
            new Random(42).NextBytes(bytes);
            var expected = "data:image/jpeg;base64," + Convert.ToBase64String(bytes);
            var path = Path.Combine(root, "image.b64");
            await File.WriteAllTextAsync(path, expected, new UTF8Encoding(false));
            var plugin = new AiChatPlugin();
            var history = new List<ChatTurn> { new("user", [new ImagePart(Path.Combine(root, "image.jpg"), "image/jpeg", bytes.Length)], 1) };
            var current = new ChatTurn("user", [new TextPart("follow up: \"quoted\"\n中文")], 2);
            var messages = (List<OpenAiMessage>)typeof(AiChatPlugin).GetMethod("BuildOpenAiMessages", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(plugin, new object[] { "system", history, current, true })!;
            Check(messages[1].Content is ChatMessageDescriptor { HasImage: true }, "Historical images must remain file descriptors.");
            var request = new OpenAiChatRequest { Model = "test", Messages = messages, Stream = true, Temperature = .5, MaxCompletionTokens = 123,
                Tools = [new OpenAiTool { Function = new OpenAiToolFunction { Name = "test_tool", Description = "test", Parameters = new { type = "object" } } }] };
            request.Messages.Add(new OpenAiMessage { Role = "assistant", Content = "checking", ReasoningContent = "reason",
                ToolCalls = [new OpenAiToolCall { Id = "call1", Function = new() { Name = "test_tool", Arguments = "{}" } }] });
            request.Messages.Add(new OpenAiMessage { Role = "tool", ToolCallId = "call1", Content = "done" });
            using (var content = new StreamingChatContent(request))
            using (var buffer = new MemoryStream())
            {
                await content.CopyToAsync(buffer);
                using var json = JsonDocument.Parse(buffer.ToArray());
                var r = json.RootElement;
                Check(r.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("image_url").GetProperty("url").GetString() == expected,
                    "Historical image payload must be byte-equivalent.");
                Check(r.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString() == "test_tool"
                    && r.GetProperty("messages")[4].GetProperty("tool_call_id").GetString() == "call1"
                    && r.GetProperty("messages")[3].GetProperty("reasoning_content").GetString() == "reason"
                    && r.GetProperty("temperature").GetDouble() == .5 && r.GetProperty("max_completion_tokens").GetInt32() == 123
                    && r.GetProperty("stream").GetBoolean(), "Tools, reasoning and request options must survive streaming.");
            }
            Console.WriteLine("PASS: HistoricalImagesAndToolsStreamWithoutMaterialization");
            var inline = new OpenAiChatRequest { Model = "test", Messages = [new OpenAiMessage { Content = new ChatMessageDescriptor {
                Role = "user", Segments = [new ContentSegment { Kind = SegmentKind.Image, InlineBytes = bytes, Mime = "image/jpeg" }] } }] };
            using (var content = new StreamingChatContent(inline))
            using (var buffer = new MemoryStream())
            {
                await content.CopyToAsync(buffer);
                using var json = JsonDocument.Parse(buffer.ToArray());
                Check(json.RootElement.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("image_url").GetProperty("url").GetString() == expected,
                    "Inline chunks must preserve final base64 padding.");
            }
            Console.WriteLine("PASS: InlineFallbackPreservesBase64Padding");
            using (var cts = new CancellationTokenSource())
            using (var content = new StreamingChatContent(request))
            {
                try { await content.CopyToAsync(new CancelSink(cts), cts.Token); throw new Exception("Cancellation ignored."); }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
            }
            // A cancelled send must close its file handle.
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            Console.WriteLine("PASS: CancelledImageSendClosesFile");
            using (var warmup = new StreamingChatContent(request)) await warmup.CopyToAsync(Stream.Null);
            long streamed = 0, buffered = 0;
            for (var i = 0; i < 3; i++)
            {
                var before = GC.GetTotalAllocatedBytes(true);
                using (var content = new StreamingChatContent(request)) await content.CopyToAsync(Stream.Null);
                streamed += GC.GetTotalAllocatedBytes(true) - before;
                before = GC.GetTotalAllocatedBytes(true);
                var legacy = new { model = "test", messages = new[] { new { role = "user", content = new[] {
                    new { type = "image_url", image_url = new { url = File.ReadAllText(path) } } } } } };
                using (var content = new StringContent(JsonSerializer.Serialize(legacy), Encoding.UTF8, "application/json"))
                    await content.CopyToAsync(Stream.Null);
                buffered += GC.GetTotalAllocatedBytes(true) - before;
            }
            Check(streamed < buffered / 10, $"Expected >90% fewer allocations; streaming={streamed}, buffered={buffered}");
            Console.WriteLine($"PASS: LargeImageRequestAllocationsAreBounded; 3 rounds buffered={buffered:N0} B streamed={streamed:N0} B");
        }
        finally { Directory.Delete(root, true); }
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static StreamingChatContent CreateDisposedContent(out WeakReference payload)
    {
        var bytes = new byte[2 * 1024 * 1024];
        payload = new WeakReference(bytes);
        var content = new StreamingChatContent(new OpenAiChatRequest
        {
            Messages = [new OpenAiMessage { Content = new ChatMessageDescriptor { Role = "user",
                Segments = [new ContentSegment { Kind = SegmentKind.Image, InlineBytes = bytes, Mime = "image/png" }] } }]
        });
        content.Dispose();
        return content;
    }

    private sealed class CancelSink(CancellationTokenSource cancellation) : Stream
    {
        public override bool CanRead => false; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => 0; public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { } public override int Read(byte[] b,int o,int c)=>throw new NotSupportedException();
        public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException(); public override void SetLength(long v)=>throw new NotSupportedException();
        public override void Write(byte[] b,int o,int c) { if (c > 1024) cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> b,CancellationToken ct=default)
        { if (b.Length > 1024) cancellation.Cancel(); ct.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
    }
}
