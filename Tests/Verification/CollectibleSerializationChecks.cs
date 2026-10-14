using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;

internal static class CollectibleSerializationChecks
{
    public static void Run()
    {
        for (int round = 0; round < 8; round++)
        {
            var weak = Exercise();
            for (int attempt = 0; weak.IsAlive && attempt < 30; attempt++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                Thread.Sleep(10);
            }
            if (weak.IsAlive) throw new Exception($"History serialization retained collectible assembly in round {round}.");
        }
        Console.WriteLine("PASS: HistorySerializationPreservesPolymorphicPartsAndReleasesAssembly; 8 reloads");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Exercise()
    {
        var context = new AssemblyLoadContext("aichat-history-probe", isCollectible: true);
        context.Resolving += (_, name) => AssemblyLoadContext.Default.Assemblies
            .FirstOrDefault(assembly => assembly.GetName().Name == name.Name);
        var assembly = context.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory, "ShiroBot.AiChatPlugin.dll"));
        var json = assembly.GetType("ShiroBot.AiChatPlugin.PluginJson")!;
        var turnType = assembly.GetType("ShiroBot.AiChatPlugin.Conversation.ChatTurn")!;
        const string fixture = """
            {"Role":"user","Parts":[{"kind":"text","Text":"quoted \" 中文"},{"kind":"image","CachePath":"image.jpg","Mime":"image/jpeg","Bytes":123},{"kind":"file_text","Name":"note.txt","Size":4,"Excerpt":"text","Truncated":false},{"kind":"file_meta","Name":"file.bin","Size":5,"Sha256":"fixture"}],"Timestamp":1}
            """;
        var deserialize = json.GetMethod("Deserialize")!.MakeGenericMethod(turnType);
        var serialize = json.GetMethod("Serialize")!.MakeGenericMethod(turnType);
        for (int i = 0; i < 3; i++)
        {
            var turn = deserialize.Invoke(null, [fixture, null]);
            var serialized = (string)serialize.Invoke(null, [turn, null])!;
            using var document = JsonDocument.Parse(serialized);
            var parts = document.RootElement.GetProperty("Parts");
            if (parts.GetArrayLength() != 4 || parts[0].GetProperty("Text").GetString() != "quoted \" 中文" ||
                parts[1].GetProperty("kind").GetString() != "image" || parts[1].TryGetProperty("InlineBytes", out _) ||
                document.RootElement.GetProperty("Timestamp").GetInt64() != 1)
                throw new Exception("Collectible history serialization changed persisted values or part discriminators.");
        }
        context.Unload();
        return new WeakReference(context);
    }
}
