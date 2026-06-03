namespace ShiroBot.AiChatPlugin.Config;

/// <summary>
/// AiChat 插件配置。
/// </summary>
public sealed class AiChatPluginConfig
{
    public DefaultSection Default { get; set; } = new();
    public List<ProviderEntry> Providers { get; set; } = [];
    public List<ModelEntry> Models { get; set; } = [];
    public HistorySection History { get; set; } = new();
    public ResourcesSection Resources { get; set; } = new();
    public BehaviourSection Behaviour { get; set; } = new();
    public PermissionsSection Permissions { get; set; } = new();
}

public sealed class DefaultSection
{
    public string Model { get; set; } = "gpt-4o-mini";
    public string DefaultPrompt { get; set; } = "你是一个乐于助人的助手。";
    public int TimeoutSeconds { get; set; } = 120;
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>默认对话模式：single / multi / shared。</summary>
    public string DefaultChatMode { get; set; } = "multi";
}

public sealed class HistorySection
{
    /// <summary>
    /// 单会话保留的最大轮次（user+assistant 各算一轮）。
    /// </summary>
    public int MaxTurns { get; set; } = 20;

    /// <summary>
    /// 单次请求的软上限 token，超出则按最早 turn 裁剪。
    /// </summary>
    public int MaxTokens { get; set; } = 16000;

    /// <summary>
    /// 是否落盘到 JSONL。
    /// </summary>
    public bool Persist { get; set; } = true;
}

public sealed class ResourcesSection
{
    /// <summary>单张图片最大字节，超出会被丢弃。</summary>
    public long MaxImageBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>读取文本类文件的最大字节。</summary>
    public long MaxInlineTextBytes { get; set; } = 512 * 1024;

    /// <summary>单文件最多塞进 prompt 的字符数（超出截断）。</summary>
    public int MaxFileExcerptChars { get; set; } = 16000;

    /// <summary>会被识别为文本类的扩展名。</summary>
    public List<string> InlineTextExtensions { get; set; } =
    [
        "txt", "md", "json", "csv", "log", "xml",
        "yml", "yaml", "ini", "toml", "html", "htm",
        "cs", "ts", "js", "py", "go", "rs",
        "java", "kt", "cpp", "c", "h", "hpp",
        "sh", "ps1", "sql", "lua", "rb", "php"
    ];

    /// <summary>资源缓存目录，相对插件目录。</summary>
    public string CacheDir { get; set; } = "data/resource_cache";

    /// <summary>引用/合并转发递归展开深度。</summary>
    public int RecursiveDepth { get; set; } = 3;

    /// <summary>HTTP 下载超时（秒）。</summary>
    public int DownloadTimeoutSeconds { get; set; } = 60;
}

public sealed class BehaviourSection
{
    /// <summary>群聊回复时是否使用引用回复。</summary>
    public bool ReplyWithQuote { get; set; } = true;

    /// <summary>当前模型不支持视觉但用户带了图片时是否提醒。</summary>
    public bool WarnOnNoVision { get; set; } = true;

    /// <summary>当 #ai 后没有任何内容时是否提示用法。</summary>
    public bool HintOnEmptyInput { get; set; } = true;
}

public sealed class PermissionsSection
{
    /// <summary>白名单。空=所有人。命中此列表才能用插件。</summary>
    public long[] AllowUsers { get; set; } = [];

    /// <summary>黑名单。在列表里直接拒绝。</summary>
    public long[] DenyUsers { get; set; } = [];

    /// <summary>这些模型仅 owner/admin 可切换。</summary>
    public string[] AdminOnlyModels { get; set; } = [];
}
