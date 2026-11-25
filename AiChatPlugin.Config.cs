using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.Conversation;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.AiChatPlugin.Resources;
using ShiroBot.AiChatPlugin.UserState;
using ShiroBot.AiChatPlugin.Tools;
using ShiroBot.AiChatPlugin.Tools.Skills;
using ShiroBot.AiChatPlugin.Tools.Memory;
using ShiroBot.AiChatPlugin.Tools.Timers;
using ShiroBot.AiChatPlugin.Usage;
using ShiroBot.SDK.Abstractions;

namespace ShiroBot.AiChatPlugin;

/// <summary>配置：默认模板、旧配置升级和配置变化后重建的服务。</summary>
public sealed partial class AiChatPlugin
{
    /// <summary>
    /// 如果配置文件不存在，写入带注释的默认模板（而不是让 ConfigManager 序列化空对象）。
    /// </summary>
    private void EnsureDefaultConfigTemplate()
    {
        var configPath = Context.Config.ConfigPath;
        if (File.Exists(configPath)) return;

        var dir = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(configPath, DefaultConfigTemplate);
        BotLog.Info($"[AiChat] 已生成默认配置模板: {configPath}");
    }

    private const string DefaultConfigTemplate = """
        # ═══════════════════════════════════════════════════════════════
        # AiChat 插件配置
        # ═══════════════════════════════════════════════════════════════

        [default]
        # 默认模型名（需在 [[models]] 或 provider fetch 中存在）
        model = "gpt-4o-mini"
        # 对话或历史压缩失败时使用的备用模型（支持 provider-model）；留空禁用
        fallback_model = ""
        # 默认 system prompt。保持稳定以提高上游前缀缓存命中率；动态时间和会话信息会附在每次请求的用户消息之后。
        default_prompt = "你是一个乐于助人的助手。"
        # shared 模式额外追加的稳定 system prompt；留空则不追加。
        shared_prompt_suffix = "你正在群聊中对话。用户消息会带有说话者标识（如 [张三/123456]）。请根据说话者区分上下文和指代。闲聊时像群聊成员一样自然、简短地回复；有人认真提问、需要详细说明时可以完整回答。"
        # 请求超时（秒）
        timeout_seconds = 60
        # 可重试请求的总尝试次数（含首次请求），适用于对话、历史压缩、生图和编辑
        retry_on_timeout_max_attempts = 3
        # 全局最大并发请求数
        max_concurrency = 4
        # 每次模型请求的最大输出 token 数（含工具调用轮次）；0 表示不限制
        max_output_tokens = 0
        # 流式请求附带 stream_options.include_usage，用于统计 token 用量和缓存命中；上游不支持时改为 false
        stream_usage = true
        # 默认对话模式: single(单次) / multi(用户独立多轮) / shared(群聊共享)
        default_chat_mode = "shared"

        [owner_prompt]
        # 主人介绍等文字，原样追加到聊天提示词；留空不追加
        template = ""

        [history]
        # 历史估算 Token 预算：超过 75% 时在回复后并入历史摘要，超过 100% 时在回复前压缩；
        # 压缩后保留的原文和分块大小都按它推算。不要超过聊天模型的上下文窗口
        max_tokens = 128000
        # 后续请求中重新发送图片的最近用户轮次数；0 表示只保留文字摘要
        image_memory_turns = 1
        # 群聊被 @ 或 #ai 时附带最近多少条群消息（优先用适配器历史消息接口；仅本次请求，不写入历史）；0 表示不附带
        group_context_messages = 0
        # 生成历史摘要使用的模型；留空使用当前对话的模型
        compact_model = ""
        # 生成历史摘要使用的 system prompt；留空使用内置默认值
        compact_prompt = "你负责维护聊天机器人在这个会话中的历史摘要。把【已有历史摘要】和【新增对话】合并成更新后的完整摘要，只输出摘要本身，不要回复对话。\n按以下分类用简短条目记录，没有内容的分类省略：\n人物：每个人的称呼和 ID、身份、偏好、提到的个人信息\n事实与约定：已确认的事实、做出的决定、对机器人的要求或设定\n进行中：未完成的话题、待办、答应过的事\n近期话题：最近聊过的主要内容，每项一句话\n保留名字、数字、时间等具体细节，删除寒暄和已经过时的内容。总长度控制在 800 字以内。"

        [resources]
        # 单张图片最大字节
        max_image_bytes = 8388608
        # 读取文本类文件的最大字节
        max_inline_text_bytes = 524288
        # 单个文件写入提示词的最大字符数，超出截断
        max_file_excerpt_chars = 16000
        # 作为文本读取的文件扩展名（不含点）；不写使用内置列表（txt、md、json、代码文件等），例如：
        # inline_text_extensions = ["txt", "md", "json", "py"]
        # 递归展开引用消息和合并转发的最大层数
        recursive_depth = 3
        # 缓存保留天数；小于等于 0 表示不按时间清理
        cache_max_age_days = 7

        [behaviour]
        # 私聊普通消息无需 #ai 即可回复；以 # 或 / 开头的命令除外
        auto_reply_direct_messages = false
        # 原消息撤回且尚未开始回复时取消请求（需要适配器上报撤回事件）
        cancel_on_recall = false
        # 优先使用官方 Markdown 消息；能力不可用或发送失败时回退普通文本
        use_markdown = false
        # 优先使用官方私聊流式回复；不支持时回退完整回复
        use_streaming = false
        # QQ 上的 AI 回复超过此字数时收进合并转发聊天记录，适用于所有回复模式和主动回复；0 不折叠
        fold_above_chars = 400
        # 回复模式：answer 一问一答整条发送（默认）；chat 总是分条短消息，模型自行决定是否引用回复；
        # auto 模型每次在聊天和解答（完整长内容）之间自选
        reply_mode = "answer"
        # 聊天模式单次最多发送的消息条数
        chat_max_messages = 3
        # 聊天模式消息间的基础间隔（毫秒），另按字数增加模拟打字时间
        chat_message_delay_ms = 800
        # 合并转发中单条消息的最大字数
        forward_node_max_chars = 600
        # 群聊回复时是否使用引用回复
        reply_with_quote = true
        # 群聊回复时 @ 原消息发送者；可与引用回复同时开启，私聊不添加 @
        reply_with_mention = false

        [permissions]
        # 仅限制群聊 shared 会话的 #clear；single/multi 和私聊始终可由用户本人清空
        clear_admin_only = true
        # 这些模型仅 owner/admin 可切换
        admin_only_models = []

        [tool_use]
        conversation_tools = true
        # 是否启用工具调用；关闭后不暴露任何工具及其提示词
        enabled = true
        # 工具调用轮数上限（1-8），达到后要求模型输出最终回答
        max_rounds = 3
        # 独立查询工具的并发数（1-16），修改状态的工具保留顺序
        max_parallel_calls = 4

        [skills]
        enabled = true
        # 技能保存在插件目录 data/skills/<名称>/SKILL.md，在 Dashboard 的“AI 技能”页面新建、编辑、导入 SKILL.md。
        # 旧版写在这里的 [[skills.entries]] 会在启动时自动迁移过去，迁移后可以删掉。

        [tool_use.tavily]
        # Tavily API key。填写后启用网络搜索；留空时不会向模型暴露搜索工具。
        api_key = ""
        # 单次搜索返回的最大结果数（1-5）
        max_results = 5
        # 搜索深度: basic / advanced
        search_depth = "basic"

        [tool_use.http_fetch]
        # 读取 HTTP/HTTPS 网页或文本直链
        enabled = true
        # 单次请求超时（秒）；顺序读取多个页面受全局 max_rounds 限制
        timeout_seconds = 20
        # GET/HEAD/OPTIONS 遇到连接失败、超时、DNS/TLS 失败、408/429/5xx 时的总尝试次数（1-5）；写入请求不自动重试
        retry_attempts = 3
        retry_delay_ms = 800
        max_bytes = 524288
        max_text_chars = 12000
        summarize_long_text = true
        summary_input_chars = 40000
        extract_images = true

        [tool_use.timer_tool]
        # 到指定时间使用默认模型生成消息并 @ 请求者
        enabled = true
        max_pending_per_user = 20
        # 到期时允许使用已启用的只读查询工具，不递归预约
        use_tools_on_delivery = true
        max_days_ahead = 30
        # 允许 cron 重复提醒及工具查看/取消；关闭时工具与原来一样只接受一次性 at/text
        allow_repeat = false
        # 重复提醒（cron）相邻两次的最小间隔（分钟）
        min_repeat_interval_minutes = 60
        # 单个重复提醒每天最多触发次数
        max_runs_per_day = 4
        # 每用户最多重复提醒数
        max_recurring_per_user = 3
        # 重复提醒最长持续天数，到期自动结束
        max_recurring_days = 90
        # 每个会话（群/私聊）合计最多待推送任务数
        max_pending_per_conversation = 30
        # 每个会话每天最多推送条数（同一分钟合并的推送算一条）
        max_deliveries_per_day = 30
        # 允许为当前消息里 @ 到的其他群成员设置提醒
        allow_mention_others = false
        # 一次性提醒停机/暂停超过这么久（分钟）才轮到推送时不再补发，标记为已错过
        missed_grace_minutes = 60

        [tool_use.mcp]
        # ⚠️ 风险提示：MCP 工具运行在机器人所在电脑或远程服务上，可能读写文件、执行命令、访问网络或账号；
        # 群成员可能通过聊天诱导模型调用（提示词注入）。只接入完全信任的服务，尽量只开放只读工具。默认关闭。
        enabled = false
        # 关闭时只有所有者/管理员发起的对话才提供 MCP 工具；开启后所有人都能让模型调用，风险很高
        allow_everyone = false
        # 连接、列出工具和单次调用的超时（秒）
        timeout_seconds = 60
        # 单次工具结果交给模型的最大字符数
        max_result_chars = 8000
        # 服务示例（去掉行首 # 使用）：
        # stdio：command 建议写绝对路径；Windows 上 npx 写成 command = "cmd"，args = ["/c", "npx", ...]
        # [[tool_use.mcp.servers]]
        # name = "fs"
        # command = "/opt/homebrew/bin/npx"
        # args = ["-y", "@modelcontextprotocol/server-filesystem", "/Users/you/Documents"]
        # env = []                     # 每项 "KEY=VALUE"
        # working_directory = ""       # 进程工作目录；留空使用宿主的工作目录
        # allowed_tools = ["read_text_file", "list_directory"]   # 留空提供全部，建议只放只读工具
        #
        # Streamable HTTP：
        # [[tool_use.mcp.servers]]
        # name = "remote"
        # url = "http://127.0.0.1:8000/mcp"
        # headers = ["Authorization: Bearer xxx"]

        [tool_use.memory]
        # 模型自主维护每个会话的记忆本（增改删条目），历史压缩时也会自动补记长期信息；默认关闭
        enabled = false
        # 每天本地 0 点用默认模型整理记忆本，删除过时/重复条目；需同时开启 enabled
        daily_review = false
        max_entries = 50
        max_entry_chars = 200

        [tool_use.image_gen]
        enabled = false
        model = "gpt-image-2.5-sunburst"
        # 聊天模型编写生图提示词的规则，例如语言、标签格式、默认画风；留空使用内置规则
        # 只指导聊天模型整理 prompt；#imagegen 提示词仍原样提交
        prompt_instructions = ""
        # 生图或编辑每次请求的独立超时（秒）
        timeout_seconds = 600
        size = "1024x1024"
        quality = "auto"
        background = "auto"
        output_format = "png"
        output_compression = 100

        [tool_use.plugin_tools]
        # 向模型提供其他插件以 chat.tool.* 注册的工具（例如 MemePlugin 的 send_meme）；需要宿主 1.0.3
        enabled = true
        # 主动插话时也提供；默认关闭，避免刷屏
        allow_proactive = false
        # 不提供的工具名称，例如 ["send_meme"]
        disabled = []

        [plugin_api]
        # 让其他插件借用这里配置的模型，例如 MemePlugin 的 AI 标注；需要宿主 1.0.3
        enabled = true
        # 其他插件不指定模型时使用的模型；留空使用 [default] 的模型，需要识图时改用一个支持识图的模型
        default_model = ""
        # 其他插件可以使用的模型；留空表示全部已注册的模型
        models = []

        [proactive]
        # 留空沿用默认模型；仅用于判断是否接话，不影响正式回复模型
        judge_model = ""
        # 群聊主动回复：没被 @ 时在频率限制允许的情况下交给模型，由模型决定说不说；默认关闭
        # 每次判断都会调用一次模型（包括最终选择沉默的）
        enabled = false
        # 允许主动回复的群号，留空表示不在任何群主动回复，例如 ["123456"]
        groups = []
        # 称呼关键词：消息提到这些词（不需要 @）时视为在叫机器人；机器人当前的群昵称/名称始终算作称呼
        keywords = []
        # 主动插话距机器人上次回复结束的最小间隔（秒）；冷却期间的消息会在冷却结束后补判（最多等 2 分钟）
        cooldown_seconds = 10
        # 主动插话前，自上次判断以来群里至少要有的其他消息数
        min_messages_between = 1
        # 交给模型参考的群聊最近消息条数（优先用适配器历史消息接口）
        context_messages = 20
        # 没被提到时插话的积极程度：off（不主动插话，只在被提到时判断）/ low（只在有人找它时）/
        # medium（被问到、求助、能补充、好笑或能自然接上的话题）/ high（除非完全无关都接）
        eagerness = "medium"
        # 自定义插话提示词，填写后代替活跃度的内置提示词；没写 [skip] 会自动补上沉默说明；留空使用内置
        prompt = ""

        [network]
        # 模型、搜索和 http_fetch 共用的可选 HTTP 代理（http_fetch 仍在本地核验目标 IP 后经 CONNECT 隧道转发，代理不可用时直连），例如 http://127.0.0.1:7890；留空直连
        http_proxy = ""

        # ─── Provider 定义 ───────────────────────────────────────────
        # 每个 provider 有独立的 base_url 和 api_key。
        # model 通过 provider = "名称" 引用。
        # fetch_models = true 时启动自动从 /v1/models 拉取可用模型。

        [[providers]]
        name = "openai"
        base_url = "https://api.openai.com/v1"
        api_key = ""
        fetch_models = false

        [[providers]]
        name = "deepseek"
        base_url = "https://api.deepseek.com/v1"
        api_key = ""
        fetch_models = true
        model_filter = ["deepseek-chat", "deepseek-reasoner"]

        # ─── 模型注册 ────────────────────────────────────────────────
        # 手动注册的模型。provider 字段引用上面的 [[providers]]。
        # fetch_models = true 的 provider 会自动补充未手动注册的模型。

        [[models]]
        name = "gpt-4o-mini"
        provider = "openai"
        supports_vision = true
        display_name = "GPT-4o Mini"

        [[models]]
        name = "deepseek-chat"
        provider = "deepseek"
        supports_vision = false
        display_name = "DeepSeek Chat"
        """;


    /// <summary>旧版本的默认提示词升级成当前默认值；用户自定义的提示词保持不变。</summary>
    internal static void UpgradeLegacyDefaults(PluginConfig config)
    {
        // 旧默认配置升级后也由 Markdown 开关统一控制格式。
        if (config.Default.SharedPromptSuffix is DefaultSection.LegacySharedPromptSuffix or DefaultSection.ShortOnlySharedPromptSuffix)
            config.Default.SharedPromptSuffix = DefaultSection.DefaultSharedPromptSuffix;
        if (config.History.CompactPrompt is HistorySection.LegacyCompactPrompt or HistorySection.PreviousCompactPrompt
            or HistorySection.LongTermMemoryCompactPrompt)
            config.History.CompactPrompt = HistorySection.DefaultCompactPrompt;
    }

    private void ApplyConfigDerivatives(bool initial)
    {
        var pluginRoot = PluginRoot;
        UpgradeLegacyDefaults(_config);

        // 仅在首次加载时构造长期持有状态的服务，避免热重载冲掉内存里的会话/限流。
        if (initial)
        {
            _conversations = new ConversationStore(pluginRoot, persist: true);
            _userSettings = new UserSettingsStore(pluginRoot);
            _memory = new MemoryNotebookStore(pluginRoot);
            _usage = new UsageStats(Path.Combine(pluginRoot, "usage-stats.json"));
            _skillStore = new SkillStore(Path.Combine(pluginRoot, "data", "skills"));
            // 页面上改了技能后只替换 skill 工具，不重建其他工具，进行中的对话不受影响。
            _skillStore.Changed += () =>
            {
                if (_config.ToolUse.Enabled && _config.Skills.Enabled) _tools.ReplaceSkills(new SkillTool(_skillStore.All));
            };
            _timerScheduler = new TimerScheduler(pluginRoot,
                () => _config.ToolUse.Enabled && _config.ToolUse.TimerTool.Enabled, SendTimerAsync,
                () => _config.ToolUse.TimerTool.MaxDeliveriesPerDay,
                () => TimeSpan.FromMinutes(Math.Clamp(_config.ToolUse.TimerTool.MissedGraceMinutes, 1, 10080)));

            var maxConcurrency = Math.Max(1, _config.Default.MaxConcurrency);
            _globalConcurrency = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        }

        // 这些是无状态的，重建无副作用。
        _openAi?.Dispose();
        _openAi = new OpenAiClient(_config.Default.TimeoutSeconds, _config.Network.HttpProxy, _usage, _config.Default.StreamUsage);
        _openAi.RegisterProviders(_config.Providers);
        _fetcher?.Dispose();
        _fetcher = new ResourceFetcher(
            _config.Resources.DownloadTimeoutSeconds,
            _config.Resources.MaxImageBytes,
            _config.Resources.MaxInlineTextBytes,
            _config.Network.HttpProxy);
        _cache = new ResourceCache(
            pluginRoot,
            _config.Resources.CacheDir,
            _config.Resources.CacheMaxAgeDays);
        _tools.Dispose();
        var migratedSkills = _skillStore.MigrateLegacy(_config.Skills.Entries);
        if (migratedSkills > 0)
            BotLog.Info($"[AiChat] 已把 config.toml 中的 {migratedSkills} 个技能迁移到 {_skillStore.DirectoryPath}，之后在 Dashboard 的技能页面管理；配置文件里的 [[skills.entries]] 可以删掉。");
        _tools = ChatToolRegistry.Create(_config, SummarizeFetchedPageAsync, _skillStore.All);
        UpdateMcpHub();
        _ = _cache.CleanupAsync();

        if (initial)
        {
            // 校验：如果默认 model 没在 models 列表里，提示一下但不报错。
            if (_config.Models.Count > 0 && ResolveModel(_config.Default.Model) is null)
            {
                BotLog.Warning($"[AiChat] 默认模型 {_config.Default.Model} 未在 [[models]] 中注册。");
            }
        }
    }

    /// <summary>
    /// 从配置了 fetch_models = true 的 provider 自动拉取模型列表，
    /// 合并到 _config.Models 中（不覆盖已手动配置的同名 model）。
    /// </summary>
    private async Task<int> FetchAndMergeProviderModelsAsync()
    {
        var config = _config;
        var client = _openAi;
        var totalAdded = _providerModels.Restore(config);
        foreach (var provider in config.Providers.Where(p => p.FetchModels && !string.IsNullOrWhiteSpace(p.ApiKey)))
        {
            try
            {
                var modelIds = await client.FetchModelsAsync(provider).ConfigureAwait(false);
                if (!ReferenceEquals(config, _config)) return totalAdded;
                var added = _providerModels.Remember(config, provider, modelIds);
                totalAdded += added;
                if (added > 0)
                    BotLog.Info($"[AiChat] 从 {provider.Name} 拉取到 {modelIds.Count} 个模型，新增 {added} 个。");
            }
            catch (Exception ex)
            {
                BotLog.Warning($"[AiChat] 从 {provider.Name} 拉取模型列表失败，保留已发现模型: {ex.Message}");
            }
        }

        return totalAdded;
    }
}
