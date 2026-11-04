using System.Net;
using System.Text;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.OpenAi;

internal static class ImageResponseMemoryChecks
{
    internal static async Task RunAsync()
    {
        var tiny = new byte[] {137,80,78,71,13,10,26,10};
        var encoded = Convert.ToBase64String(tiny);
        var nested = Encoding.UTF8.GetBytes("{\"data\":[{\"b64_json\":{\"b64_json\":\"" + encoded + "\"}}]}");
        try { ImageGenerationClient.DecodeResponse(nested); throw new Exception("Nested image property was accepted"); }
        catch (InvalidOperationException) { }
        var valid = Encoding.UTF8.GetBytes("{\"ignored\":{},\"data\":[{\"ignored\":[{}],\"b64_json\":\"" + encoded + "\"},{}]}");
        if (!ImageGenerationClient.DecodeResponse(valid).AsSpan().SequenceEqual(tiny))
            throw new Exception("Image response with unknown fields changed bytes");
        var image = new byte[2_000_000];
        new byte[] {137,80,78,71,13,10,26,10}.CopyTo(image,0);
        var body = Encoding.UTF8.GetBytes("{\"data\":[{\"b64_json\":\"" + Convert.ToBase64String(image) + "\"}]}");
        using var listener = new HttpListener();
        int port = Random.Shared.Next(20000,40000); string url=$"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(url);listener.Start();
        var serve = Task.Run(async () =>
        {
            for(int request=0;request<3;request++)
            {
                var context=await listener.GetContextAsync();
                await context.Request.InputStream.CopyToAsync(Stream.Null);
                context.Response.ContentLength64=body.Length;
                await context.Response.OutputStream.WriteAsync(body);context.Response.Close();
            }
        });
        var config=new ImageGenerationSection();
        _ = await ImageGenerationClient.GenerateAsync(url,"local-fixture","fixture","fixture",config,"",default);
        long before=GC.GetTotalAllocatedBytes(true);
        var actual=await ImageGenerationClient.GenerateAsync(url,"local-fixture","fixture","fixture",config,"",default);
        long allocated=GC.GetTotalAllocatedBytes(true)-before;
        if(!actual.AsSpan().SequenceEqual(image)||allocated>8_000_000)
            throw new Exception($"Image response copied too many buffers or changed bytes: {allocated}");
        actual=await ImageGenerationClient.GenerateAsync(url,"local-fixture","fixture","fixture",config,"",default);
        if(!actual.AsSpan().SequenceEqual(image))throw new Exception("Repeated image response changed bytes");
        await serve;listener.Stop();
        Console.WriteLine($"PASS: 2 MB image response preserves bytes and allocates {allocated} bytes without a whole Base64 string or JSON copy");
    }
}
