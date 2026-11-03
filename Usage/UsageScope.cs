namespace ShiroBot.AiChatPlugin.Usage;

/// <summary>
/// 标记接下来的模型请求属于哪个会话、做什么用，由发送请求的 <see cref="OpenAi.OpenAiClient"/> 读取后记入统计。
/// 只在执行上下文里保存字符串：插件自己的类型进入执行上下文后，可能被宿主长期持有的任务捕获，拉住插件程序集。
/// </summary>
internal static class UsageScope
{
    private const char Separator = '\n';
    private static readonly AsyncLocal<string?> Current = new();

    /// <summary>当前的来源（会话）和用途；没有标记时为 "-" 和 "other"。</summary>
    public static (string Source, string Purpose) Tag
    {
        get
        {
            var value = Current.Value;
            var index = value?.IndexOf(Separator) ?? -1;
            return index < 0 ? ("-", "other") : (value![..index], value[(index + 1)..]);
        }
    }

    public static IDisposable Begin(string source, string purpose)
    {
        var previous = Current.Value;
        Current.Value = source + Separator + purpose;
        return new Restore(previous);
    }

    /// <summary>只改用途、保留来源，例如对话中途的网页摘要。</summary>
    public static IDisposable Purpose(string purpose) => Begin(Tag.Source, purpose);

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
