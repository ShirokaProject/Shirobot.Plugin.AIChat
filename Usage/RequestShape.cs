using System.Security.Cryptography;
using System.Text;
using ShiroBot.AiChatPlugin.OpenAi;

namespace ShiroBot.AiChatPlugin.Usage;

/// <summary>
/// 请求里影响上游前缀缓存的部分的摘要。前缀缓存要求从头开始逐字节一致：
/// 工具定义、system prompt、较早的消息任何一处变化，之后的内容都无法命中。
/// </summary>
/// <param name="Chain">逐条消息的累积摘要：第 i 项覆盖第 0..i 条消息，两次请求从第几项开始不同，就是从第几条消息开始无法复用缓存。</param>
internal sealed record RequestShape(int Images, string ToolsHash, string[] Chain)
{
    public static RequestShape Of(OpenAiChatRequest request)
    {
        var images = 0;
        var chain = new string[request.Messages.Count];
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            for (var i = 0; i < request.Messages.Count; i++)
            {
                var message = request.Messages[i];
                hash.AppendData(Encoding.UTF8.GetBytes(message.Role + "\0" + Describe(message, ref images) + "\u0001"));
                chain[i] = Short(hash.GetCurrentHash());
            }
        }

        if (request.SentPrefix is { } sent && sent.Points.Length >= request.Messages.Count + 2)
            return new RequestShape(images, sent.Points[0].Hash,
                sent.Points.Skip(1).Take(request.Messages.Count).Select(point => point.Hash).ToArray());

        var tools = new StringBuilder();
        foreach (var tool in request.Tools ?? [])
            tools.Append(PluginJson.Serialize(new { tool.Type, tool.Function.Name, tool.Function.Description,
                Parameters = PluginJson.ToJsonValue(tool.Function.Parameters) })).Append('\u0001');
        return new RequestShape(images, tools.Length == 0 ? "" : Short(SHA256.HashData(Encoding.UTF8.GetBytes(tools.ToString()))), chain);
    }

    /// <summary>
    /// 和同一会话上一次请求比较，说明前缀从哪里开始变化。正常的下一轮只在末尾追加：
    /// 上一次的最后一条（本次请求才附带的上下文）会被替换，之前的消息应该全部相同。
    /// </summary>
    public (int CommonMessages, string Change) CompareWith(RequestShape? previous)
    {
        if (previous is null) return (0, "首次");
        var common = 0;
        while (common < Chain.Length && common < previous.Chain.Length && Chain[common] == previous.Chain[common]) common++;
        if (ToolsHash != previous.ToolsHash) return (common, "工具定义变化");
        if (common == 0) return (0, "system 变化");
        return common >= previous.Chain.Length - 1 ? (common, "仅追加") : (common, $"第 {common} 条起变化");
    }

    /// <summary>消息内容的可比较文本；图片用文件路径代替内容，不读取图片。</summary>
    private static string Describe(OpenAiMessage message, ref int images)
    {
        var builder = new StringBuilder();
        switch (message.Content)
        {
            case string text:
                builder.Append(text);
                break;
            case ChatMessageDescriptor descriptor:
                foreach (var segment in descriptor.Segments)
                {
                    if (segment.Kind == SegmentKind.Image)
                    {
                        images++;
                        builder.Append("[image:").Append(segment.B64FilePath ?? segment.InlineBytes?.Length.ToString()).Append(']');
                    }
                    else builder.Append(segment.Text);
                }
                break;
            case { } other:
                builder.Append(other.GetType().Name);
                break;
        }
        if (message.ToolCallId is { } id) builder.Append("\0tool:").Append(id);
        foreach (var call in message.ToolCalls ?? [])
            builder.Append("\0call:").Append(call.Id).Append(call.Function?.Name).Append(call.Function?.Arguments);
        return builder.ToString();
    }

    private static string Short(byte[] hash) => Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
}
