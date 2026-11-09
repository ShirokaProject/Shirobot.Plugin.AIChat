using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ShiroBot.AiChatPlugin.Config;

namespace ShiroBot.AiChatPlugin.OpenAi;

internal static class ImageGenerationCommand
{
    internal const string Help = "用法：#imagegen 提示词 [--format png|jpeg|webp|gif] [--size auto|宽x高] [--quality auto|low|medium|high|xhigh|max] [--background auto|opaque|transparent] [--compression 0–100] [--frames 2–16] [--duration 20–2000]\n例如：#imagegen 小猫挥手循环动图 --format gif --size auto --frames 9 --duration 200\n参数可放提示词前或后，支持 --format=gif；-- 后的内容均作为提示词。";

    internal static string HelpFor(bool enableGif) => enableGif ? Help :
        "用法：#imagegen 提示词 [--format png|jpeg|webp] [--size auto|宽x高] [--quality auto|low|medium|high|xhigh|max] [--background auto|opaque|transparent] [--compression 0–100]\nGIF功能暂时关闭。";

    internal static (string Prompt, ImageGenerationOptions Options) Parse(string input, ImageGenerationSection defaults)
    {
        var tokens = Regex.Matches(input, @"\S+");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var removed = new List<(int Start, int End)>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Value == "--") { removed.Add((token.Index, token.Index + token.Length)); break; }
            if (!token.Value.StartsWith("--", StringComparison.Ordinal)) continue;
            var split = token.Value[2..].Split('=', 2);
            var name = split[0].ToLowerInvariant().Replace('_', '-');
            name = name switch { "output-format" => "format", "output-compression" => "compression", "frame-count" => "frames", "frame-duration-ms" => "duration", _ => name };
            if (name is not ("format" or "size" or "quality" or "background" or "compression" or "frames" or "duration"))
                throw new ArgumentException("未知生图参数：--" + name + "。如需原样放进提示词，请在前面加 -- 分隔。");
            string value;
            var end = token.Index + token.Length;
            if (split.Length == 2) value = split[1];
            else
            {
                if (++i >= tokens.Count || tokens[i].Value.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("参数 --" + name + " 缺少值。");
                value = tokens[i].Value;
                end = tokens[i].Index + tokens[i].Length;
            }
            if (value.Length == 0 || !values.TryAdd(name, value.ToLowerInvariant())) throw new ArgumentException("参数 --" + name + " 为空或重复。");
            removed.Add((token.Index, end));
        }
        var prompt = new StringBuilder();
        var cursor = 0;
        foreach (var (start, end) in removed) { prompt.Append(input.AsSpan(cursor, start - cursor)); cursor = end; }
        prompt.Append(input.AsSpan(cursor));
        var text = prompt.ToString().Trim();
        if (text.Length is < 1 or > 32000) throw new ArgumentException("生图提示词不能为空，且最多32000字符。");
        string? Get(string name) => values.GetValueOrDefault(name);
        int? Number(string name, int min, int max)
        {
            if (Get(name) is not { } value) return null;
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < min || number > max)
                throw new ArgumentException($"参数 --{name} 必须为 {min}–{max} 的整数。");
            return number;
        }
        var options = new ImageGenerationOptions(Get("size"), Get("quality"), Get("background"), Get("format"),
            Number("compression", 0, 100), Number("frames", 2, 16), Number("duration", 20, 2000));
        var effective = options.Resolve(defaults);
        if (effective.OutputFormat != "gif" && (options.FrameCount is not null || options.FrameDurationMs is not null))
            throw new ArgumentException("--frames 和 --duration 仅用于GIF，请同时设置 --format gif。");
        if (effective.OutputFormat == "gif" && options.OutputCompression is not null)
            throw new ArgumentException("GIF不支持压缩参数，请移除 --compression。");
        ImageGenerationClient.ValidateOptions(effective);
        return (text, options);
    }
}
