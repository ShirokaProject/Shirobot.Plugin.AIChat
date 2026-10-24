using ShiroBot.SDK.Config;

namespace ShiroBot.AiChatPlugin.Config;

/// <summary>
/// AiChat 插件配置。
/// </summary>
public sealed class PluginConfig
{
    [ConfigField("默认模型、提示词、请求重试和并发设置。", Label = "默认设置")]
    public DefaultSection Default { get; set; } = new();
    [ConfigField("可选的主人提示词文本，默认关闭，不改变实际管理权限。", Label = "主人提示词")]
    public OwnerPromptSection OwnerPrompt { get; set; } = new();
    [ConfigField("配置各服务商的接口地址、密钥和模型自动发现。", Label = "模型服务商")]
    public List<ProviderEntry> Providers { get; set; } = [];
    [ConfigField("手动注册模型及其所属服务商和视觉能力。", Label = "模型列表")]
    public List<ModelEntry> Models { get; set; } = [];
    [ConfigField("对话历史、图片记忆和 compact 摘要设置。", Label = "历史与压缩")]
    public HistorySection History { get; set; } = new();
    [ConfigField("图片、文件、缓存和引用消息的处理限制。", Label = "资源处理")]
    public ResourcesSection Resources { get; set; } = new();
    [ConfigField("长回复折叠、引用回复和使用提示。", Label = "回复行为")]
    public BehaviourSection Behaviour { get; set; } = new();
    [ConfigField("管理员清空会话和模型切换限制；用户使用范围由宿主路由控制。", Label = "操作权限")]
    public PermissionsSection Permissions { get; set; } = new();
    [ConfigField("全局工具开关及各工具的独立配置。", Label = "工具调用")]
    public ToolUseSection ToolUse { get; set; } = new();
    [ConfigField("模型和搜索请求共用的代理设置；安全网页抓取使用公网直连。", Label = "网络设置")]
    public NetworkSection Network { get; set; } = new();
}

public sealed class DefaultSection
{
    public const string DefaultSharedPromptSuffix = "你正在群聊中对话。用户消息会带有说话者标识（如 [张三/123456]）。请根据说话者区分上下文和指代，像群聊成员一样自然、简短地回复，不要写成大段说明。";
    public const string LegacySharedPromptSuffix = "你正在群聊中对话。用户消息会带有说话者标识（如 [张三/123456]）。请根据说话者区分上下文和指代，像群聊成员一样自然、简短地回复，不要写成大段说明，不要使用Markdown格式，除非用户明确要求。";
    [ConfigField("填写已注册模型名或 provider-model。", Label = "默认模型")]
    public string Model { get; set; } = "gpt-4o-mini";
    /// <summary>文本请求重试后仍失败时使用的模型；留空禁用，不改变用户模型偏好。</summary>
    [ConfigField("文本请求重试后仍失败时调用；留空禁用，不改变用户模型选择。", Label = "备用模型")]
    public string FallbackModel { get; set; } = "";
    [ConfigField("默认对话的 system prompt。", Label = "默认提示词", Type = "text")]
    public string DefaultPrompt { get; set; } = "你是一个乐于助人的助手。";
    [ConfigField("共享模式额外追加的 system prompt；留空不追加。", Label = "群聊共享提示词补充", Type = "text")]
    public string SharedPromptSuffix { get; set; } = DefaultSharedPromptSuffix;
    /// <summary>单次上游请求的超时秒数。</summary>
    [ConfigField("单次上游请求的超时时间。", Label = "请求超时（秒）", Min = 5)]
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>超时或瞬时网络失败时的总尝试次数，包含首次请求。</summary>
    [ConfigField("超时、临时网络错误或空响应时的总尝试次数，包含首次请求。", Label = "请求最大尝试次数", Min = 1, Max = 5)]
    public int RetryOnTimeoutMaxAttempts { get; set; } = 3;

    /// <summary>重试前的基础等待毫秒数；后续重试线性递增。</summary>
    [ConfigField("重试前的基础等待时间；后续重试线性递增。", Label = "重试等待（毫秒）", Min = 100, Max = 10000)]
    public int RetryOnTimeoutDelayMs { get; set; } = 1000;
    [ConfigField("允许同时处理的 AI 请求数量；修改后重载插件生效。", Label = "最大并发请求数", Min = 1)]
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>单次模型请求允许生成的最大输出 token 数。</summary>
    [ConfigField("每次模型请求的最大输出 token 数，适用于普通回复和工具调用轮次。", Label = "最大输出 Token", Min = 1)]
    public int MaxOutputTokens { get; set; } = 8192;

    /// <summary>默认对话模式：single / multi / shared。</summary>
    [ConfigField("single：单次；multi：用户独立多轮；shared：群聊共享。", Label = "默认对话模式", Options = new[] { "single", "multi", "shared" })]
    public string DefaultChatMode { get; set; } = "shared";
}

public sealed class HistorySection
{
    public const string DefaultCompactPrompt =
        "你负责维护聊天机器人在这个会话中的长期记忆。把【已有长期记忆】和【新增对话】合并成更新后的完整记忆，只输出记忆本身，不要回复对话。\n" +
        "按以下分类用简短条目记录，没有内容的分类省略：\n" +
        "人物：每个人的称呼和 ID、身份、偏好、提到的个人信息\n" +
        "事实与约定：已确认的事实、做出的决定、对机器人的要求或设定\n" +
        "进行中：未完成的话题、待办、答应过的事\n" +
        "近期话题：最近聊过的主要内容，每项一句话\n" +
        "保留名字、数字、时间等具体细节，删除寒暄和已经过时的内容。总长度控制在 800 字以内。";
    /// <summary>旧版本的默认值，加载时升级为新的结构化记忆提示词。</summary>
    public const string PreviousCompactPrompt = "请用简洁的中文总结以下对话的关键信息和结论，保留重要的事实、决定和上下文。";
    public const string LegacyCompactPrompt = PreviousCompactPrompt + "不要使用Markdown格式。";

    /// <summary>压缩历史时的 system prompt；留空使用默认提示词。</summary>
    [ConfigField("compact 摘要请求的 system prompt；留空使用内置提示词。", Label = "上下文压缩提示词", Type = "text")]
    public string CompactPrompt { get; set; } = DefaultCompactPrompt;
    [ConfigField("合并长期记忆使用的模型名称，可填更便宜的模型；留空使用当前对话的模型。", Label = "上下文压缩模型")]
    public string CompactModel { get; set; } = "";
    /// <summary>
    /// 单会话保留的最大轮次（user+assistant 各算一轮）。
    /// </summary>
    [ConfigField("保留的对话轮数，每轮包含用户消息和 AI 回复；接近上限时自动压缩。", Label = "最大历史轮数", Min = 2)]
    public int MaxTurns { get; set; } = 20;

    /// <summary>
    /// 单次请求的软上限 token，超出则按最早 turn 裁剪。
    /// </summary>
    [ConfigField("单次请求的估算 token 上限：超过时本次请求跳过最早的历史；历史超过其 75% 时自动并入长期记忆。", Label = "上下文 Token 软上限", Min = 1000)]
    public int MaxTokens { get; set; } = 16000;

    /// <summary>
    /// 是否落盘到 JSONL。
    /// </summary>
    [ConfigField("将对话历史保存到磁盘 JSONL 文件；修改后重载插件生效。", Label = "持久化历史")]
    public bool Persist { get; set; } = true;

    /// <summary>在后续请求中重新发送图片的最近用户轮次数。0 表示只保留文字摘要。</summary>
    [ConfigField("后续请求重新发送图片的最近用户轮数；0 仅保留文字摘要。", Label = "图片记忆轮数", Min = 0)]
    public int ImageMemoryTurns { get; set; } = 1;
}

public sealed class ResourcesSection
{
    /// <summary>单张图片最大字节，超出会被丢弃。</summary>
    [ConfigField("超过大小上限的图片将被丢弃。", Label = "单张图片大小上限（字节）", Min = 1)]
    public long MaxImageBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>读取文本类文件的最大字节。</summary>
    [ConfigField("读取文本类文件的最大字节数。", Label = "文本文件读取上限（字节）", Min = 1)]
    public long MaxInlineTextBytes { get; set; } = 512 * 1024;

    /// <summary>单文件最多塞进 prompt 的字符数（超出截断）。</summary>
    [ConfigField("单个文件写入提示词的最大字符数，超出时截断。", Label = "文件摘录字符上限", Min = 1)]
    public int MaxFileExcerptChars { get; set; } = 16000;

    /// <summary>会被识别为文本类的扩展名。</summary>
    [ConfigField("会作为文本读取的文件扩展名，不包含开头的点。", Label = "文本文件扩展名")]
    public List<string> InlineTextExtensions { get; set; } =
    [
        "txt", "md", "json", "csv", "log", "xml",
        "yml", "yaml", "ini", "toml", "html", "htm",
        "cs", "ts", "js", "py", "go", "rs",
        "java", "kt", "cpp", "c", "h", "hpp",
        "sh", "ps1", "sql", "lua", "rb", "php"
    ];

    /// <summary>资源缓存目录，相对插件目录。</summary>
    [ConfigField("相对于插件目录的资源缓存路径。", Label = "资源缓存目录")]
    public string CacheDir { get; set; } = "data/resource_cache";

    /// <summary>缓存保留天数。小于等于 0 表示不按时间清理。</summary>
    [ConfigField("小于等于 0 表示不按时间清理。", Label = "缓存保留天数")]
    public int CacheMaxAgeDays { get; set; } = 7;

    /// <summary>引用/合并转发递归展开深度。</summary>
    [ConfigField("递归展开引用消息和合并转发的最大层数。", Label = "引用与转发展开深度", Min = 0)]
    public int RecursiveDepth { get; set; } = 3;

    /// <summary>HTTP 下载超时（秒）。</summary>
    [ConfigField("下载图片和文件的 HTTP 超时时间。", Label = "资源下载超时（秒）", Min = 1)]
    public int DownloadTimeoutSeconds { get; set; } = 60;
}

public sealed class BehaviourSection
{
    [ConfigField("开启后私聊普通消息无需 #ai 即可触发 AI 回复，仍检查使用权限。以 # 或 / 开头的命令不自动回复。", Label = "私聊直接回复")]
    public bool AutoReplyDirectMessages { get; set; }

    [ConfigField("收到原消息撤回事件且尚未开始回复时，取消资源处理、排队、模型和工具请求，不再重试或调用备用模型，也不保存这轮历史。已开始流式回复后不取消。需要适配器上报撤回事件。", Label = "撤回时取消请求")]
    public bool CancelOnRecall { get; set; }

    [ConfigField("优先使用官方 Markdown 消息发送 AI 回复；能力不可用或发送失败时回退普通文本。关闭时在对话提示词末尾追加“不要使用markdown”。", Label = "使用 Markdown 发送")]
    public bool UseMarkdown { get; set; }

    [ConfigField("优先使用 QQ 官方私聊流式回复；适配器、聊天场景或上游不支持时回退完整文本。", Label = "使用流式发送")]
    public bool UseStreaming { get; set; }

    /// <summary>AI 回复超过此字符数时使用 QQ 合并转发折叠；小于等于 0 禁用。</summary>
    [ConfigField("QQ 回复超过此字符数时使用合并转发折叠；0 禁用。", Label = "长回复折叠字数", Min = 0)]
    public int FoldAboveChars { get; set; }

    [ConfigField("让模型每次自选回复模式：聊天模式像真人一样分几条短消息发送，并自行决定是否引用回复；解答模式输出完整长内容，超长时收进聊天记录。关闭后整条发送并使用“长回复折叠字数”。需要模型稳定输出首行标记，小模型建议保持关闭。", Label = "模型自选回复模式")]
    public bool ReplyModes { get; set; }

    [ConfigField("聊天模式单次最多发送的消息条数，多出的内容并入最后一条。", Label = "聊天模式最多条数", Min = 1, Max = 10)]
    public int ChatMaxMessages { get; set; } = 3;

    [ConfigField("聊天模式两条消息之间的基础间隔，实际还会按下一条的字数增加模拟打字时间（最多再加 3 秒）；0 表示不等待。", Label = "聊天模式发送间隔（毫秒）", Min = 0, Max = 10000)]
    public int ChatMessageDelayMs { get; set; } = 800;

    [ConfigField("解答模式超过此字数时收进 QQ 合并转发聊天记录；0 表示不折叠。", Label = "解答模式折叠字数", Min = 0)]
    public int AnswerFoldAboveChars { get; set; } = 400;

    [ConfigField("合并转发聊天记录中单条消息的最大字数，超出时按段落和句子拆成多条。", Label = "聊天记录单条字数", Min = 50)]
    public int ForwardNodeMaxChars { get; set; } = 600;

    /// <summary>群聊回复时是否使用引用回复。</summary>
    [ConfigField("在群聊中引用用户原消息回复，折叠消息除外。", Label = "群聊引用回复")]
    public bool ReplyWithQuote { get; set; } = true;

    [ConfigField("群聊回复时 @ 原消息发送者，可与引用回复同时开启；私聊不添加 @。", Label = "@发送者回复")]
    public bool ReplyWithMention { get; set; }

    /// <summary>当前模型不支持视觉但用户带了图片时是否提醒。</summary>
    [ConfigField("模型不支持图片时提示用户图片已被忽略。", Label = "无视觉能力提醒")]
    public bool WarnOnNoVision { get; set; } = true;

    /// <summary>当 #ai 后没有任何内容时是否提示用法。</summary>
    [ConfigField("当 #ai 后没有可处理内容时显示用法提示。", Label = "空输入使用提示")]
    public bool HintOnEmptyInput { get; set; } = true;
}

public sealed class PermissionsSection
{
    [ConfigField("仅限制 shared 模式的群聊共享会话：开启后仅机器人所有者或管理员可清空；关闭后群成员均可清空。single/multi 个人会话和私聊会话始终由用户本人清空。", Label = "shared 会话仅管理员可清空")]
    public bool ClearAdminOnly { get; set; } = true;


    /// <summary>这些模型仅 owner/admin 可切换。</summary>
    [ConfigField("填写模型名，不是管理员 ID。管理员身份由 SDK Context.IsAdmin 判断，宿主 owner 自动拥有 admin 权限。", Label = "需要管理员权限的模型")]
    public string[] AdminOnlyModels { get; set; } = [];
}

public sealed class ToolUseSection
{
    [ConfigField("关闭后不向模型发送任何工具定义及工具提示词。", Label = "启用工具调用")]
    public bool Enabled { get; set; } = true;
    /// <summary>每次请求最多允许的工具调用轮数（1-8），之后要求模型给出最终回答。</summary>
    [ConfigField("所有工具共用的调用轮数。先读首页再读详情页需要多轮；与各工具的次数上限共同生效，达到后要求模型输出最终回答。", Label = "工具调用轮数上限", Min = 1, Max = 8)]
    public int MaxRounds { get; set; } = 3;
    [ConfigField("独立配置 Tavily 搜索工具。", Label = "Tavily 网络搜索")]
    public TavilySection Tavily { get; set; } = new();
    [ConfigField("读取网页和文本直链。", Label = "HTTP 页面读取")]
    public HttpFetchSection HttpFetch { get; set; } = new();
    [ConfigField("预约消息，到点使用默认模型生成回复并 @ 用户。", Label = "定时消息工具")]
    public TimerToolSection TimerTool { get; set; } = new();
    [ConfigField("让模型自主维护每个会话的长期记忆本，可选每日自动整理。", Label = "记忆本工具")]
    public MemoryToolSection Memory { get; set; } = new();
}

public sealed class TavilySection
{
    [ConfigField("启用且填写 API 密钥时才向模型提供搜索工具。", Label = "启用 Tavily 搜索")]
    public bool Enabled { get; set; } = true;
    /// <summary>留空时不注册网络搜索工具，也不向模型透露该能力。</summary>
    [ConfigField("留空时不注册搜索工具。", Label = "Tavily API 密钥", Type = "password")]
    public string ApiKey { get; set; } = "";
    [ConfigField("每次 AI 回复最多允许的 Tavily 搜索次数，仍受全局工具轮数限制。", Label = "每次回复搜索次数上限", Min = 1, Max = 10)]
    public int MaxCallsPerTurn { get; set; } = 3;

    [ConfigField("单次搜索返回的最大结果数。", Label = "搜索结果数量", Min = 1, Max = 5)]
    public int MaxResults { get; set; } = 5;

    [ConfigField("basic：基础搜索；advanced：深度搜索。", Label = "搜索深度", Options = new[] { "basic", "advanced" })]
    public string SearchDepth { get; set; } = "basic";

}

public sealed class NetworkSection
{
    /// <summary>所有上游 HTTP 请求共用的可选 HTTP/HTTPS 代理地址。</summary>
    [ConfigField("模型和搜索请求的 HTTP/HTTPS 代理地址；留空直连。安全网页抓取不使用代理。", Label = "HTTP 代理", Placeholder = "http://127.0.0.1:7890")]
    public string HttpProxy { get; set; } = "";
}

public sealed class HttpFetchSection
{
    [ConfigField("允许模型读取 HTTP/HTTPS 网页或文本直链；不会执行页面脚本。", Label = "启用 HTTP 页面读取")]
    public bool Enabled { get; set; } = true;
    [ConfigField("每次 AI 回复最多允许调用 http_fetch 的次数。若要根据首页结果继续抓取详情页，还需提高工具调用分组顶部的全局轮数上限。", Label = "每次回复抓取次数上限", Min = 1, Max = 10)]
    public int MaxCallsPerTurn { get; set; } = 3;
    [ConfigField("单次读取超时秒数。", Label = "读取超时秒数", Min = 5, Max = 120)]
    public int TimeoutSeconds { get; set; } = 20;
    [ConfigField("限制下载体积，超过限制时返回工具错误。", Label = "最大下载字节数", Min = 1024, Max = 2097152)]
    public int MaxBytes { get; set; } = 524288;
    [ConfigField("返回给模型的文本字符数上限，超过时截断。", Label = "最大文本字符数", Min = 100, Max = 40000)]
    public int MaxTextChars { get; set; } = 12000;
    [ConfigField("正文超过最大文本字符数时，通过独立且不带工具的默认模型请求摘要，再返回主对话。摘要失败时截断原文。", Label = "长文本先摘要")]
    public bool SummarizeLongText { get; set; } = true;
    [ConfigField("单次独立摘要的原文输入上限；超出部分不发送给模型。", Label = "摘要输入字符上限", Min = 1000, Max = 100000)]
    public int SummaryInputChars { get; set; } = 40000;
    [ConfigField("将网页中的公网图片 URL 和替代文字返回给模型，不执行脚本，不自动下载图片。", Label = "提取网页图片链接")]
    public bool ExtractImages { get; set; } = true;
}

public sealed class TimerToolSection
{
    [ConfigField("允许模型按用户要求创建定时推送；关闭时已有任务暂停，重新开启后恢复。", Label = "启用定时消息工具")]
    public bool Enabled { get; set; } = true;
    [ConfigField("同一用户在同一会话中最多保留的待推送任务数。", Label = "每用户任务上限", Min = 1, Max = 100)]
    public int MaxPendingPerUser { get; set; } = 20;
    [ConfigField("到点生成提醒时允许使用已启用的 Tavily 和 HTTP 页面读取工具，受全局轮数及各工具次数限制；不允许创建新的定时任务。", Label = "定时推送时允许使用查询工具")]
    public bool UseToolsOnDelivery { get; set; } = true;
    [ConfigField("一次性提醒及重复提醒首次触发允许的最远天数。模型需提供带时区偏移的 ISO 8601 时间。", Label = "最远预约天数", Min = 1, Max = 365)]
    public int MaxDaysAhead { get; set; } = 30;
    [ConfigField("允许模型用 cron 表达式创建重复提醒，并通过工具查看、取消自己的提醒。关闭时工具只接受一次性的 at 和 text，适合工具调用能力较弱的小模型；已有重复任务照常推送，#timer cancel 不受影响。", Label = "允许重复提醒")]
    public bool AllowRepeat { get; set; }
    [ConfigField("重复提醒（cron）相邻两次之间的最小间隔，防止刷屏。", Label = "重复提醒最小间隔（分钟）", Min = 5, Max = 1440)]
    public int MinRepeatIntervalMinutes { get; set; } = 60;
    [ConfigField("单个重复提醒在一天内最多触发的次数，超过的 cron 规则会被拒绝。", Label = "重复提醒每日最多次数", Min = 1, Max = 48)]
    public int MaxRunsPerDay { get; set; } = 4;
    [ConfigField("同一用户在同一会话中最多保留的重复提醒数，计入每用户任务上限。", Label = "每用户重复提醒上限", Min = 0, Max = 100)]
    public int MaxRecurringPerUser { get; set; } = 3;
    [ConfigField("重复提醒没有截止时间或截止时间更晚时，从首次触发起最多持续的天数，到期自动结束。", Label = "重复提醒最长天数", Min = 1, Max = 3650)]
    public int MaxRecurringDays { get; set; } = 90;
    [ConfigField("同一会话（一个群或一个私聊）中所有用户合计最多保留的待推送任务数。", Label = "每会话任务上限", Min = 1, Max = 1000)]
    public int MaxPendingPerConversation { get; set; } = 30;
    [ConfigField("同一会话每天（本地日期）最多推送的定时消息条数，同一分钟合并的推送算一条；超出后一次性提醒标记为已跳过，重复提醒顺延到下一次。计数保存在磁盘，重启后不清零。", Label = "每会话每日推送上限", Min = 1, Max = 1000)]
    public int MaxDeliveriesPerDay { get; set; } = 30;
    [ConfigField("允许模型在群聊中为当前消息里 @ 到的其他成员设置提醒，到点 @ 这些成员。关闭时只能提醒请求者本人，工具定义与原来一致。", Label = "允许提醒其他成员")]
    public bool AllowMentionOthers { get; set; }
    [ConfigField("一次性提醒因停机或暂停超过这么久才轮到推送时不再补发，标记为已错过并保留在 #timer 列表中；重复提醒错过 10 分钟即跳到下一次。", Label = "一次性提醒补发时限（分钟）", Min = 1, Max = 10080)]
    public int MissedGraceMinutes { get; set; } = 60;
}

public sealed class MemoryToolSection
{
    [ConfigField("允许模型通过 memory_note 工具新增、修改、删除当前会话记忆本的条目，记忆本内容会附在对话提示词中。shared 模式一个群一本，multi 模式每人一本，single 模式不可用。", Label = "启用记忆本工具")]
    public bool Enabled { get; set; }
    [ConfigField("每天本地时间 0 点用默认模型整理一次所有记忆本：删除过时、重复或无用的条目，合并相近内容。需同时启用记忆本工具。", Label = "每日整理记忆本")]
    public bool DailyReview { get; set; }
    [ConfigField("单个记忆本最多保存的条目数。", Label = "记忆本条目上限", Min = 1, Max = 200)]
    public int MaxEntries { get; set; } = 50;
    [ConfigField("单条记忆的最大字数。", Label = "单条记忆字数上限", Min = 20, Max = 1000)]
    public int MaxEntryChars { get; set; } = 200;
}

public sealed class OwnerPromptSection
{
    [ConfigField("开启后将下方文本追加到聊天及定时推送提示词中，不影响实际管理权限。", Label = "启用主人提示词")]
    public bool Enabled { get; set; }
    [ConfigField("直接填写主人介绍、QQ 号、机器人框架等文字，开启后原样追加；留空不追加。", Label = "主人提示词", Type = "text")]
    public string Template { get; set; } = "";
}
