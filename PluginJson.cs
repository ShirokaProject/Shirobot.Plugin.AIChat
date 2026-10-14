using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace ShiroBot.AiChatPlugin;

/// <summary>Keep reflection metadata inside the collectible plugin instead of JsonSerializerOptions.Default.</summary>
internal static class PluginJson
{
    public static readonly IJsonTypeInfoResolver Resolver = JsonTypeInfoResolver.Combine(PluginJsonContext.Default, new DefaultJsonTypeInfoResolver());
    private static readonly JsonSerializerOptions Options = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions { TypeInfoResolver = Resolver };
        options.Converters.Add(new AnonymousJsonConverterFactory());
        return options;
    }
    public static string Serialize<T>(T value, JsonSerializerOptions? options = null)
    {
        // Preserve the declared DTO type. Serializing a generated DTO as object also
        // probes its interfaces (including record IEquatable<T>) through the global
        // reflection member-accessor cache, which can retain a collectible assembly.
        if (PluginJsonContext.Default.GetTypeInfo(typeof(T)) is not null)
            return JsonSerializer.Serialize(value, options ?? Options);
        return JsonSerializer.Serialize(ToJsonValue(value), options ?? Options);
    }
    internal static object? ToJsonValue(object? value)
    {
        if (value is null || value is string) return value;
        var type = value.GetType();
        if (type.Name.Contains("AnonymousType", StringComparison.Ordinal))
            return type.GetProperties().ToDictionary(p => p.Name, p => ToJsonValue(p.GetValue(value)));
        if (value is System.Collections.IDictionary dictionary)
        {
            var result = new Dictionary<string, object?>();
            foreach (System.Collections.DictionaryEntry item in dictionary)
                result.Add((string)item.Key, ToJsonValue(item.Value));
            return result;
        }
        if (value is System.Collections.IEnumerable sequence && type.IsCollectible)
            return sequence.Cast<object?>().Select(ToJsonValue).ToArray();
        return value;
    }

    public static T? Deserialize<T>(string json, JsonSerializerOptions? options = null)
        => JsonSerializer.Deserialize<T>(json, options ?? Options);
}
