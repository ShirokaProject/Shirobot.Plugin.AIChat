using System.Reflection;
using ShiroBot.AiChatPlugin;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.Delivery;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

internal static class ReplyModeChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static async Task RunAsync()
    {
        var chat = ParsedReply.Parse("[chat]\n哈哈哈\n\n笑死我了\n", 4);
        Check(chat.Mode == ReplyMode.Chat && !chat.Quote && chat.Messages.SequenceEqual(["哈哈哈", "笑死我了"]) &&
            chat.Text == "哈哈哈\n笑死我了", "Chat mode must split non-empty lines and strip the tag.");
        var quoted = ParsedReply.Parse("[CHAT+REPLY] 在的\n怎么了", 4);
        Check(quoted.Quote && quoted.Messages.SequenceEqual(["在的", "怎么了"]), "Tag must be case-insensitive and allow inline content.");
        var capped = ParsedReply.Parse("[chat]\n1\n2\n3\n4", 2);
        Check(capped.Messages.SequenceEqual(["1", "2\n3\n4"]), "Extra chat lines must merge into the last message.");
        var answer = ParsedReply.Parse("[answer]\n第一段\n\n第二段", 4);
        Check(answer.Mode == ReplyMode.Answer && answer.Messages.Single() == "第一段\n\n第二段", "Answer mode must keep the full text.");
        var untagged = ParsedReply.Parse("没有标记\n第二行", 4);
        Check(untagged.Mode == ReplyMode.Answer && untagged.Text == "没有标记\n第二行", "Missing tag must fall back to a single message.");
        var tooled = ParsedReply.Parse("[chat]\n我查一下\n\n[answer]\n结果如下", 4);
        Check(tooled.Mode == ReplyMode.Answer && tooled.Text == "我查一下\n\n结果如下", "The last tag wins and every tag line is removed.");
        Check(ParsedReply.Parse("[chat]", 4).Text == "[chat]", "A tag-only reply must not become empty.");
        // 强制聊天模式：没有标记或写了 [answer] 也分条发送，不引用；[chat+reply] 仍可引用。
        var forced = ParsedReply.Parse("没有标记\n\n第二行", 4, forceChat: true);
        Check(forced.Mode == ReplyMode.Chat && !forced.Quote && forced.Messages.SequenceEqual(["没有标记", "第二行"]), "Forced chat must split untagged replies without quoting.");
        var forcedAnswer = ParsedReply.Parse("[answer]\n一\n二\n三\n四", 3, forceChat: true);
        Check(forcedAnswer.Mode == ReplyMode.Chat && !forcedAnswer.Quote && forcedAnswer.Messages.Count == 3 && forcedAnswer.Messages[0] == "一",
            "Forced chat must also split answer-tagged replies and respect the cap.");
        Check(ParsedReply.Parse("[chat+reply]\n在", 4, forceChat: true).Quote, "Forced chat must keep the model's quote choice.");
        Check(new BehaviourSection { ReplyMode = "聊天" }.ReplyStyle() == "chat" && new BehaviourSection { ReplyMode = " AUTO " }.ReplyStyle() == "auto"
            && new BehaviourSection { ReplyMode = "bogus" }.ReplyStyle() == "answer" && new BehaviourSection().ReplyStyle() == "answer",
            "Reply mode values must normalise, defaulting to answer.");
        Console.WriteLine("PASS: reply mode tags, quote choice, message cap, fallback, forced chat and tool prefixes");

        Check(ParsedReply.StreamPreview("[cha") is null && ParsedReply.StreamPreview("[chat]\n你好") is null,
            "Chat mode and incomplete tags must not stream.");
        Check(ParsedReply.StreamPreview("[answer]\n首先") == "首先" && ParsedReply.StreamPreview("普通回答") == "普通回答",
            "Answer and untagged replies must stream without the tag.");
        var nodes = ParsedReply.SplitForForward(string.Concat(Enumerable.Repeat("句子。", 40)) + "\n\n短段落", 60);
        Check(nodes.Count > 1 && nodes.All(n => n.EnumerateRunes().Count() <= 60) &&
            string.Concat(nodes).Replace("\n", "") == string.Concat(Enumerable.Repeat("句子。", 40)) + "短段落" &&
            nodes.Take(nodes.Count - 1).All(n => n.EndsWith('。')), "Forward nodes must be bounded, break at sentence ends and keep all text.");
        Console.WriteLine("PASS: answer-only stream preview and bounded forward nodes");

        var config = new PluginConfig();
        config.Behaviour.ChatMessageDelayMs = 0;
        var plugin = new AiChatPlugin();
        typeof(AiChatPlugin).GetField("_config", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(plugin, config);
        var sends = new List<(string Method, object? Content)>();
        var messageContext = DispatchProxy.Create<IMessageContext, TestProxy>();
        ((TestProxy)(object)messageContext).Call = (method, args) =>
        {
            sends.Add((method.Name, args![1]));
            return Task.FromResult(new SentMessage("sent"));
        };
        var context = DispatchProxy.Create<IBotContext, TestProxy>();
        ((TestProxy)(object)context).Call = (method, _) => method.Name switch
        {
            "get_Message" => messageContext,
            "GetAdapterExtension" => null,
            _ => throw new NotSupportedException(method.Name)
        };
        typeof(PluginBase).GetProperty("Context", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, context);
        var message = new MessageEvent
        {
            Platform = "qq", SelfId = "123", MessageId = "1", Channel = Channel.Group("456"), Sender = new User("789"), Segments = []
        };
        var send = typeof(AiChatPlugin).GetMethod("SendChatMessagesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await using (var delivery = new OfficialReplyDelivery(context, message, config.Behaviour))
            await (Task)send.Invoke(plugin, [message, true, delivery, ParsedReply.Parse("[chat+reply]\n在\n咋了", 4)])!;
        Check(sends.Select(s => s.Method).SequenceEqual(["QuoteReplyAsync", "SendMessageAsync"]),
            "Only the first chat message may quote the original message.");
        sends.Clear();
        await using (var delivery = new OfficialReplyDelivery(context, message, config.Behaviour))
            await (Task)send.Invoke(plugin, [message, true, delivery, ParsedReply.Parse("[chat]\n草\n真的假的", 4)])!;
        Check(sends.All(s => s.Method == "SendMessageAsync") && sends.Count == 2, "Plain chat must not quote any message.");

        sends.Clear();
        config.Behaviour.ForwardNodeMaxChars = 50;
        var fold = typeof(AiChatPlugin).GetMethod("ReplyFoldableAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)fold.Invoke(plugin, [message, true, string.Concat(Enumerable.Repeat("很长的解释。", 30)), 100])!;
        var forward = ((IReadOnlyList<MessageSegment>)sends.Single().Content!).OfType<RawSegment>().Single().Payload as QOutgoingForward;
        Check(forward is not null && forward.Messages.Count > 1, "Long answers must fold into multiple forward nodes.");
        Console.WriteLine("PASS: chat messages quote only when chosen, plain chat sends, long answers fold into nodes");
    }
}
