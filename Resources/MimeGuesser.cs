namespace ShiroBot.AiChatPlugin.Resources;

internal static class MimeGuesser
{
    private static readonly Dictionary<string, string> ExtToMime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".svg"] = "image/svg+xml",
        [".ico"] = "image/x-icon",
        [".heic"] = "image/heic",
        [".heif"] = "image/heif",
    };

    /// <summary>
    /// 通过文件头魔数嗅探图片 mime。无法识别时返回 null。
    /// </summary>
    public static string? SniffImageMime(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4)
        {
            return null;
        }

        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {
            return "image/png";
        }

        // JPEG: FF D8 FF
        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        // GIF: "GIF87a" or "GIF89a"
        if (bytes.Length >= 6 &&
            bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 &&
            bytes[3] == 0x38 && (bytes[4] == 0x37 || bytes[4] == 0x39) && bytes[5] == 0x61)
        {
            return "image/gif";
        }

        // WebP: "RIFF????WEBP"
        if (bytes.Length >= 12 &&
            bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
            bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            return "image/webp";
        }

        // BMP: "BM"
        if (bytes[0] == 0x42 && bytes[1] == 0x4D)
        {
            return "image/bmp";
        }

        // HEIC/HEIF: ?? ?? ?? ?? "ftyp" then brand
        if (bytes.Length >= 12 &&
            bytes[4] == 0x66 && bytes[5] == 0x74 && bytes[6] == 0x79 && bytes[7] == 0x70)
        {
            // brand at offset 8..12
            var brand = System.Text.Encoding.ASCII.GetString(bytes.Slice(8, 4));
            return brand switch
            {
                "heic" or "heix" or "hevc" or "hevx" => "image/heic",
                "mif1" or "msf1" or "heim" or "heis" => "image/heif",
                _ => null
            };
        }

        return null;
    }

    /// <summary>
    /// 用响应字节 + Content-Type + 备用扩展名三重信息推断 mime，保证返回 image/* 之一。
    /// </summary>
    public static string ResolveImageMime(ReadOnlySpan<byte> bytes, string? contentType, string? fallbackExt = null)
    {
        // 1. 魔数嗅探最可靠
        var sniffed = SniffImageMime(bytes);
        if (sniffed is not null)
        {
            return sniffed;
        }

        // 2. Content-Type 头
        if (!string.IsNullOrEmpty(contentType))
        {
            var ct = contentType.Split(';')[0].Trim().ToLowerInvariant();
            if (ct.StartsWith("image/", StringComparison.Ordinal))
            {
                return ct;
            }
        }

        // 3. 扩展名提示
        if (!string.IsNullOrEmpty(fallbackExt))
        {
            var ext = fallbackExt.StartsWith('.') ? fallbackExt : "." + fallbackExt;
            if (ExtToMime.TryGetValue(ext, out var byExt))
            {
                return byExt;
            }
        }

        // 4. 兜底：QQ 图片绝大多数是 png/jpeg，给个 png 让模型尽量也接受。
        return "image/png";
    }

    /// <summary>
    /// 根据 mime 反推一个保存到磁盘时用的扩展名。
    /// </summary>
    public static string ExtensionForMime(string mime)
    {
        return mime switch
        {
            "image/png" => ".png",
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            "image/x-icon" => ".ico",
            "image/heic" => ".heic",
            "image/heif" => ".heif",
            "image/svg+xml" => ".svg",
            _ => ".png"
        };
    }

    public static bool IsLikelyTextExtension(string fileName, IReadOnlyCollection<string> textExtensions)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(ext))
        {
            return false;
        }

        foreach (var allowed in textExtensions)
        {
            if (string.Equals(ext, allowed.TrimStart('.'), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
