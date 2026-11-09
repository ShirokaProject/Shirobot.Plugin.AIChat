using ShiroBot.AiChatPlugin.OpenAi;
using SkiaSharp;

namespace ShiroBot.AiChatPlugin.Resources;

internal static class ReferenceImage
{
    /// <summary>Keep GIF/PNG/JPEG/WebP reference bytes intact; normalize other decodable rasters to PNG.</summary>
    internal static ImageGenerationInput Normalize(byte[] bytes)
    {
        if (bytes.Length is 0 or > 20_000_000) throw new ArgumentException("参考图片为空或超过20MB。");
        var mime = MimeGuesser.SniffImageMime(bytes);
        if (mime is "image/png" or "image/jpeg" or "image/webp" or "image/gif") return new(bytes, mime);
        return ToPng(bytes);
    }

    internal static ImageGenerationInput ToPng(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null) throw new ArgumentException("参考图无法解码，请重新发送PNG、JPEG、WebP、GIF或BMP图片。");
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > 16_777_216)
            throw new ArgumentException("参考图像素过大，自动转换最多支持1677万像素。");
        var outputInfo = new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(outputInfo);
        if (codec.GetPixels(outputInfo, bitmap.GetPixels()) != SKCodecResult.Success)
            throw new ArgumentException("参考图解码失败，请重新发送完整图片。");
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        if (png is null || png.Size > 20_000_000) throw new ArgumentException("参考图转换PNG后超过20MB，请缩小图片。");
        return new(png.ToArray(), "image/png");
    }
}
