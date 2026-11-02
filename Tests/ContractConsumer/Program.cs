using System.Reflection;
using ShiroBot.AiChatPlugin.Contracts;
using ShiroBot.SDK.Plugin;

static void Check(bool ok, string message)
{
    if (!ok) throw new InvalidOperationException(message);
}

// Compile against the exported contract in the final DLL, with no ProjectReference or runtime reference.
Check(ChatModels.Complete.Name == "aichat.model.complete" && ChatModels.List.Name == "aichat.model.list",
    "The published DLL must export model services.");
Check(ChatTools.Tools.Prefix == "chat.tool." && ChatTools.AfterReply.Prefix == "chat.after_reply.",
    "The published DLL must export tool and after-reply extension points.");
var request = new ChatModelRequest([new ChatModelMessage("user", "test", [new ChatModelImage("image/png", "AA==")])], NeedsVision: true);
var json = ChatModels.Complete.ToCallJson(request);
Check((bool?)json["arguments"]?["needs_vision"] == true, "Model requests must retain the vision flag.");
var decoded = PluginJson.Deserialize<ChatModelRequest>(PluginJson.Serialize(request));
Check(decoded is { NeedsVision: true, Messages.Count: 1 } && decoded.Messages[0].Images?[0].Mime == "image/png",
    "Imported model messages must round-trip.");
var tool = new ChatToolResult("selected", new ChatAfterReply("send_meme", new() { ["id"] = "full-id" }));
var decodedTool = PluginJson.Deserialize<ChatToolResult>(PluginJson.Serialize(tool));
Check(decodedTool?.AfterReply?.Name == "send_meme" && (string?)decodedTool.AfterReply.Arguments?["id"] == "full-id",
    "Imported tool messages must preserve after-reply actions.");
Check(!typeof(ChatModels).IsPublic && !typeof(ChatTools).IsPublic, "Imported contracts must remain private to the consumer.");
Check(!Assembly.GetExecutingAssembly().GetReferencedAssemblies().Any(a => a.Name == "ShiroBot.AiChatPlugin"),
    "The consumer must not depend on AIChat's assembly at runtime.");
var dll = Path.GetFullPath(args.Single());
Check(Directory.GetFiles(Path.GetDirectoryName(dll)!, "*.dll").Length == 1, "Release output must contain exactly one plugin DLL.");
Console.WriteLine("Published AIChat DLL: model/tool contracts import, JSON round-trip and isolated consumer passed.");
