using System.Text.Json.Serialization;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.Conversation;
using ShiroBot.AiChatPlugin.Memory;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.AiChatPlugin.Timers;
using ShiroBot.AiChatPlugin.UserState;

namespace ShiroBot.AiChatPlugin;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(PluginConfig))]
[JsonSerializable(typeof(ChatTurn))]
[JsonSerializable(typeof(ConversationMetadata))]
[JsonSerializable(typeof(Dictionary<string, UserSettings>))]
[JsonSerializable(typeof(List<TimerJob>))]
[JsonSerializable(typeof(List<TimerDeliveryCount>))]
[JsonSerializable(typeof(List<MemoryEntry>))]
[JsonSerializable(typeof(OpenAiChatRequest))]
[JsonSerializable(typeof(OpenAiChatResponse))]
internal partial class PluginJsonContext : JsonSerializerContext { }
