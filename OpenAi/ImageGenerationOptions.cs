using ShiroBot.AiChatPlugin.Config;

namespace ShiroBot.AiChatPlugin.OpenAi;

internal sealed record ImageGenerationOptions(string? Size = null, string? Quality = null,
    string? Background = null, string? OutputFormat = null, int? OutputCompression = null, int? FrameCount = null, int? FrameDurationMs = null)
{
    internal ImageGenerationSection Resolve(ImageGenerationSection defaults) => new()
    {
        Enabled = defaults.Enabled, EnableGif = defaults.EnableGif, Model = defaults.Model, TimeoutSeconds = defaults.TimeoutSeconds,
        Size = Size ?? defaults.Size, Quality = Quality ?? defaults.Quality,
        Background = Background ?? defaults.Background, OutputFormat = OutputFormat ?? defaults.OutputFormat,
        OutputCompression = OutputCompression ?? defaults.OutputCompression,
        FrameCount = FrameCount ?? defaults.FrameCount, FrameDurationMs = FrameDurationMs ?? defaults.FrameDurationMs
    };
}
