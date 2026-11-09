using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.AiChatPlugin.Tools;

internal static class ImageCommandChecks
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static async Task RunAsync()
    {
        var rejectedGif = false;
        try { ImageGenerationCommand.Parse("cat --format gif", new()); }
        catch (ImageGenerationException) { rejectedGif = true; }
        Check(rejectedGif, "GIF command must be disabled by default.");
        using (var disabledTool = new ImageGenerationTool((_, _, _) => throw new Exception("Disabled GIF reached generation.")))
        {
            Check((await disabledTool.ExecuteAsync("{\"prompt\":\"cat\",\"output_format\":\"gif\"}")).Contains("disabled"), "Disabled GIF tool was callable.");
            using var definition = System.Text.Json.JsonDocument.Parse(ShiroBot.AiChatPlugin.PluginJson.Serialize(disabledTool.Definition));
            var props = definition.RootElement.GetProperty("function").GetProperty("parameters").GetProperty("properties");
            Check(!props.TryGetProperty("frame_count", out _) && !props.GetProperty("output_format").GetProperty("enum").EnumerateArray().Any(v => v.GetString() == "gif"), "Disabled GIF fields advertised to model.");
        }
        var defaults = new ImageGenerationSection { EnableGif = true };
        var gif = ImageGenerationCommand.Parse("小猫挥手 --format gif --size auto --frames 9 --duration 200", defaults);
        Check(gif.Prompt == "小猫挥手" && gif.Options is { OutputFormat: "gif", Size: "auto", FrameCount: 9, FrameDurationMs: 200 }, "GIF options lost or leaked into prompt.");
        var aliases = ImageGenerationCommand.Parse("--output_format=webp --output-compression 0 --background transparent 透明贴纸", defaults);
        Check(aliases.Prompt == "透明贴纸" && aliases.Options is { OutputFormat: "webp", OutputCompression: 0, Background: "transparent" }, "Aliases or zero compression failed.");
        var raw = "画一只猫，  保留  两个空格";
        Check(ImageGenerationCommand.Parse(raw, defaults).Prompt == raw, "Ordinary command prompt changed.");
        Check(ImageGenerationCommand.Parse("--format png -- 画一张 --format gif 的示意图", defaults).Prompt == "画一张 --format gif 的示意图", "Delimiter failed.");
        Check(defaults.OutputFormat == "png" && defaults.FrameCount == 9, "Command mutated defaults.");
        Check(ImageGenerationCommand.Parse("跳舞 --frames 12", new() { EnableGif = true, OutputFormat = "gif" }).Options.FrameCount == 12, "Configured GIF default ignored.");
        foreach (var input in new[] { "--format gif", "cat --format", "cat --format=", "cat --format gif --format png", "cat --unknown x", "cat --frames 1", "cat --duration 2001", "cat --frames 9", "cat --format gif --compression 90", "cat --format gif --background transparent", "cat --size 17x16", "cat --format svg", "cat --compression -1" })
        {
            var rejected = false;
            try { ImageGenerationCommand.Parse(input, defaults); }
            catch (Exception ex) when (ex is ArgumentException or ImageGenerationException) { rejected = true; }
            Check(rejected, "Invalid command was accepted: " + input);
        }
        ImageGenerationOptions? chosen = null;
        using var tool = new ImageGenerationTool((_, options, _) => { chosen = options; return Task.FromResult("queued"); }, enableGif: true);
        Check(await tool.ExecuteAsync("{\"prompt\":\"cat waving loop\",\"output_format\":\"gif\",\"size\":\"auto\",\"frame_count\":12,\"frame_duration_ms\":100}") == "queued" && chosen is { OutputFormat: "gif", FrameCount: 12, FrameDurationMs: 100 }, "AI-selected GIF parameters unavailable.");
        Console.WriteLine("PASS: direct image command parameters, aliases, delimiter, default preservation, validation and model-selected GIF tool options");
    }
}
