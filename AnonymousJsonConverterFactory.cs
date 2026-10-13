using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShiroBot.AiChatPlugin;

/// <summary>Anonymous tool schemas/results are write-only; reflect their values without compiling cached accessors.</summary>
internal sealed class AnonymousJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type type) => type.IsDefined(typeof(CompilerGeneratedAttribute), false)
        && type.Name.Contains("AnonymousType", StringComparison.Ordinal);
    public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options)
        => (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(type))!;
    private sealed class Converter<T> : JsonConverter<T>
    {
        public override T Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var property in typeof(T).GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                var item = property.GetValue(value);
                writer.WritePropertyName(property.Name);
                JsonSerializer.Serialize(writer, item, item?.GetType() ?? typeof(object), options);
            }
            writer.WriteEndObject();
        }
    }
}
