using System.Reflection;
using ShiroBot.AiChatPlugin;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.SDK.Models;

internal static class DisabledToolCommandChecks
{
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
    private static void Check(bool ok, string text) { if (!ok) throw new Exception(text); }

    public static async Task RunAsync()
    {
        // No bot context, stores, scheduler or model client: disabled commands
        // must stop before permissions, reads, writes, replies or model calls.
        var plugin = new AiChatPlugin();
        var config = new PluginConfig();
        typeof(AiChatPlugin).GetField("_config", Flags)!.SetValue(plugin, config);
        string Help() => (string)typeof(AiChatPlugin).GetMethod("BuildHelpText", Flags)!.Invoke(plugin, null)!;
        var commands = new[]
        {
            ("HandleImageGenAsync", "#imagegen"), ("HandleImageGenAsync", "#imagegen 画猫"),
            ("HandleMemoryAsync", "#memory"), ("HandleMemoryAsync", "#memory clear"),
            ("HandleTimerListAsync", "#timer"), ("HandleTimerListAsync", "#timer cancel 1"),
            ("HandleTimerListAsync", "#timer clear"), ("HandleMcpAsync", "#mcp")
        };
        foreach (bool globalOff in new[] { true, false })
        {
            config.ToolUse.Enabled = !globalOff;
            config.ToolUse.ImageGen.Enabled = globalOff;
            config.ToolUse.Memory.Enabled = globalOff;
            config.ToolUse.TimerTool.Enabled = globalOff;
            config.ToolUse.Mcp.Enabled = globalOff;
            foreach (bool direct in new[] { true, false })
            foreach (var (handler, text) in commands)
            {
                var message = new MessageEvent { Platform = "test", InstanceId = "i", SelfId = "bot", MessageId = "disabled",
                    Channel = direct ? Channel.Direct("u") : Channel.Group("g"), Sender = new User("u"), Segments = [new TextSegment(text)] };
                await (Task)typeof(AiChatPlugin).GetMethod(handler, Flags)!.Invoke(plugin, [message])!;
            }
            Check(commands.All(command => !Help().Contains(command.Item2.Split(' ')[0])),
                "Disabled tool commands must not appear in help, whether disabled individually or globally.");
            Check(Help().Contains("#ai <") && Help().Contains("#model") && Help().Contains("#clear"),
                "Disabling tools must preserve ordinary chat and conversation commands.");
        }
        // Help and handlers inspect the current configuration, including toggles
        // made after initial registration; no plugin reload is needed.
        config.ToolUse.Enabled = true;
        config.ToolUse.ImageGen.Enabled = true;
        config.ToolUse.Memory.Enabled = true;
        config.ToolUse.TimerTool.Enabled = true;
        config.ToolUse.Mcp.Enabled = true;
        Check(commands.All(command => Help().Contains(command.Item2.Split(' ')[0])), "Re-enabling tools must restore their help entries.");
        config.ToolUse.Memory.Enabled = false;
        Check(!Help().Contains("#memory") && Help().Contains("#imagegen") && Help().Contains("#timer") && Help().Contains("#mcp"),
            "One feature switch must not hide the other enabled tools.");
        Console.WriteLine("PASS: disabled tool commands stay silent before all I/O in groups and DMs, and help follows global and per-tool toggles");
    }
}
