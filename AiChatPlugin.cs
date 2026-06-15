
using System.Text;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.Conversation;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.AiChatPlugin.Resources;
using ShiroBot.AiChatPlugin.UserState;
using ShiroBot.Model.Common;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.AiChatPlugin;


[BotPlugin(id: "AiChatPlugin",
    Name = "AI聊天插件",
    Description = "支持上下文/图片/文件/历史/模型切换的 OpenAI 兼容聊天插件。",
    Version = "1.0.0",
    GithubRepo = "greepar/ShiroBot.Plugin.AiChat",
    IsPluginSingleFile = false)]
public sealed class AiChatPlugin : PluginBase
{
    public override string Name => "AiChatPlugin";

    private AiChatPluginConfig _config = new();
    private IDisposable? _configWatchSubscription;
    private long _selfId;

    private ConversationStore _conversations = null!;
    private UserSettingsStore _userSettings = null!;
    private OpenAiClient _openAi = null!;
    private ResourceFetcher _fetcher = null!;
    private ResourceCache _cache = null!;
    private SemaphoreSlim _globalConcurrency = null!;

    private string PluginRoot => Path.GetDirectoryName(Context.Config.ConfigPath)
                                 ?? AppContext.BaseDirectory;

    protected override async Task LoadAsync()
    {
        EnsureDefaultConfigTemplate();
        _config = Context.Config.Load<AiChatPluginConfig>();
        ApplyConfigDerivatives(initial: true);

        _configWatchSubscription = Context.Config.Watch<AiChatPluginConfig>(updated =>
        {
            _config = updated;
            ApplyConfigDerivatives(initial: false);
            _ = Task.Run(FetchAndMergeProviderModelsAsync);
            BotLog.Info("[AiChat] 配置已热重载。");
        });

        // 从配置了 fetch_models = true 的 provider 自动拉取模型列表
        await FetchAndMergeProviderModelsAsync().ConfigureAwait(false);

        var loginInfo = await Context.System.GetLoginInfoAsync();
        _selfId = loginInfo.Uin;
        BotLog.Info($"[AiChat] 登录账号 {loginInfo.Nickname} ({_selfId})");

        // 私聊
        FriendCommands.MapExact("#aihelp", HandleFriendHelpAsync);
        FriendCommands.MapPrefix("#aimode", HandleFriendAiModeAsync);
        FriendCommands.MapPrefix("#ai", HandleFriendAiAsync);
        FriendCommands.MapPrefix("#prompt", HandleFriendPromptAsync);
        FriendCommands.MapExact("#clear", HandleFriendClearAsync);
        FriendCommands.MapPrefix("#model", HandleFriendModelAsync);
        FriendCommands.MapExact("#refresh", HandleFriendRefreshAsync);

        // 群聊
        GroupCommands.MapExact("#aihelp", HandleGroupHelpAsync);
        GroupCommands.MapPrefix("#aimode", HandleGroupAiModeAsync);
        GroupCommands.MapPrefix("#ai", HandleGroupAiAsync);
        GroupCommands.MapPrefix("#prompt", HandleGroupPromptAsync);
        GroupCommands.MapExact("#clear", HandleGroupClearAsync);
        GroupCommands.MapPrefix("#model", HandleGroupModelAsync);
        GroupCommands.MapExact("#refresh", HandleGroupRefreshAsync);
        GroupCommands.MapMention(_selfId, HandleGroupMentionAsync);

        BotLog.Info("[AiChat] 插件已加载。");
    }

    protected override Task OnUnloadAsync()
    {
        _configWatchSubscription?.Dispose();
        _configWatchSubscription = null;
        BotLog.Info("[AiChat] 插件已卸载。");
        return Task.CompletedTask;
    }

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
        # 默认 system prompt。支持占位符: {time}, {date}, {groupname}, {groupid}, {userid}, {nickname}, {card}
        default_prompt = "当前时间: {time}\n你是一个乐于助人的助手。"
        # shared 模式额外追加的 system prompt。支持同样的占位符；留空则不追加。
        shared_prompt_suffix = "你正在群聊 {groupname} 中对话。用户消息会带有说话者标识（如 [张三/123456]）。请根据说话者区分上下文和指代，像群聊成员一样自然、简短地回复，不要写成大段说明，不要使用Markdown格式，除非用户明确要求。"
        # 请求超时（秒）
        timeout_seconds = 120
        # 全局最大并发请求数
        max_concurrency = 4
        # 默认对话模式: single(单次) / multi(用户独立多轮) / shared(群聊共享)
        default_chat_mode = "multi"

        [history]
        # 单会话保留的最大轮次（user+assistant 各算一轮）
        max_turns = 20
        # 单次请求的软上限 token，超出后按最早 turn 裁剪/压缩上下文
        max_tokens = 16000
        # 是否落盘到 JSONL
        persist = true

        [resources]
        # 单张图片最大字节
        max_image_bytes = 8388608
        # 读取文本类文件的最大字节
        max_inline_text_bytes = 524288
        # 资源缓存目录（相对插件目录）
        cache_dir = "data/resource_cache"
        # 缓存保留天数；小于等于 0 表示不按时间清理
        cache_max_age_days = 7
        # HTTP 下载超时（秒）
        download_timeout_seconds = 60

        [behaviour]
        # 群聊回复时是否使用引用回复
        reply_with_quote = true
        # 当前模型不支持视觉但用户带了图片时是否提醒
        warn_on_no_vision = true
        # 当 #ai 后没有任何内容时是否提示用法
        hint_on_empty_input = true

        [permissions]
        # 白名单（空=所有人可用）
        allow_users = []
        # 黑名单
        deny_users = []
        # 这些模型仅 owner/admin 可切换
        admin_only_models = []

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


    private void ApplyConfigDerivatives(bool initial)
    {
        var pluginRoot = PluginRoot;

        // 仅在首次加载时构造长期持有状态的服务，避免热重载冲掉内存里的会话/限流。
        if (initial)
        {
            _conversations = new ConversationStore(pluginRoot, _config.History.Persist, _config.History.MaxTurns);
            _userSettings = new UserSettingsStore(pluginRoot);

            var maxConcurrency = Math.Max(1, _config.Default.MaxConcurrency);
            _globalConcurrency = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        }

        // 这些是无状态的，重建无副作用。
        _openAi = new OpenAiClient(_config.Default.TimeoutSeconds);
        _openAi.RegisterProviders(_config.Providers);
        _fetcher = new ResourceFetcher(
            _config.Resources.DownloadTimeoutSeconds,
            _config.Resources.MaxImageBytes,
            _config.Resources.MaxInlineTextBytes);
        _cache = new ResourceCache(
            pluginRoot,
            _config.Resources.CacheDir,
            _config.Resources.CacheMaxAgeDays);
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
        var totalAdded = 0;
        foreach (var provider in _config.Providers.Where(p => p.FetchModels))
        {
            try
            {
                var modelIds = await _openAi.FetchModelsAsync(provider).ConfigureAwait(false);
                if (modelIds.Count == 0) continue;

                // 应用过滤
                if (provider.ModelFilter is { Length: > 0 } filters)
                {
                    modelIds = modelIds.Where(id =>
                        filters.Any(f => id.StartsWith(f, StringComparison.OrdinalIgnoreCase))).ToList();
                }

                var existingNames = new HashSet<string>(
                    _config.Models.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);

                var added = 0;
                foreach (var modelId in modelIds)
                {
                    if (existingNames.Contains(modelId)) continue;

                    _config.Models.Add(new ModelEntry
                    {
                        Name = modelId,
                        Provider = provider.Name,
                        SupportsVision = true
                    });
                    existingNames.Add(modelId);
                    added++;
                }

                totalAdded += added;

                if (added > 0)
                    BotLog.Info($"[AiChat] 从 {provider.Name} 拉取到 {modelIds.Count} 个模型，新增 {added} 个。");
            }
            catch (Exception ex)
            {
                BotLog.Warning($"[AiChat] 从 {provider.Name} 拉取模型列表失败: {ex.Message}");
            }
        }

        return totalAdded;
    }

    // -------- 帮助 --------

    private Task HandleFriendHelpAsync(FriendIncomingMessage message)
        => Context.Message.ReplyAsync(message, BuildHelpText());

    private async Task HandleGroupHelpAsync(GroupIncomingMessage message)
    {
        var resp = await Context.Message.ReplyAsync(message, BuildHelpText()).ConfigureAwait(false);
        ScheduleRecall(message.Group.GroupId, resp.MessageSeq, 30);
    }

    private static string BuildHelpText() =>
        """
        AiChat 命令:
          #ai <内容>     与AI对话
          @机器人 <内容>  群里另一种触发方式
          #aimode        查看当前对话模式
          #aimode <模式>  切换对话模式
            single  单次对话，不保留上下文
            multi   多轮对话，按用户独立记录（默认）
            shared  群聊共享，同群所有人共享上下文
          #prompt <文本>  设置自己的 system prompt(跨会话)
          #prompt        查看当前 prompt
          #prompt -      重置为默认
          #clear         清空当前会话上下文
          #model         查看可用模型
          #model <name>  切换偏好模型(部分模型仅管理员)
          #refresh       重新拉取模型列表
          #aihelp        显示本帮助
        提示: 在消息里引用图片/文件/或合并转发, AI会一并读取这些资源。
        """;

    // -------- #ai --------

    private async Task HandleFriendAiModeAsync(FriendIncomingMessage message)
    {
        var arg = StripPrefix(message.GetPlainText(), "#aimode");
        await ApplyAiModeAsync(message.SenderId, arg,
            reply => Context.Message.ReplyAsync(message, reply)).ConfigureAwait(false);
    }

    private async Task HandleGroupAiModeAsync(GroupIncomingMessage message)
    {
        var arg = StripPrefix(message.GetPlainText(), "#aimode");
        await ApplyAiModeAsync(message.SenderId, arg,
            reply => ReplyToGroup(message, reply)).ConfigureAwait(false);
    }

    private async Task ApplyAiModeAsync(long userId, string arg, Func<string, Task> reply)
    {
        var settings = _userSettings.GetOrCreate(userId);

        if (string.IsNullOrEmpty(arg))
        {
            var current = settings.ChatMode ?? _config.Default.DefaultChatMode;
            await reply($"当前对话模式: {current}\n可选: single(单次无记录) / multi(用户独立多轮) / shared(群聊共享)").ConfigureAwait(false);
            return;
        }

        var normalized = arg.ToLowerInvariant().Trim();
        if (normalized is not ("single" or "multi" or "shared"))
        {
            await reply("无效模式。可选: single / multi / shared").ConfigureAwait(false);
            return;
        }

        // 如果跟配置默认一致，存 null 即可
        settings.ChatMode = string.Equals(normalized, _config.Default.DefaultChatMode, StringComparison.OrdinalIgnoreCase)
            ? null
            : normalized;
        await _userSettings.SaveAsync().ConfigureAwait(false);
        var desc = normalized switch
        {
            "single" => "单次对话，不保留上下文",
            "multi" => "多轮对话，按用户独立记录",
            "shared" => "群聊共享，同群所有人共享上下文",
            _ => normalized
        };
        await reply($"已切换对话模式: {normalized} ({desc})").ConfigureAwait(false);
    }

    private async Task HandleFriendAiAsync(FriendIncomingMessage message)
    {
        if (!CheckPermission(message.SenderId, out var deny))
        {
            if (deny is not null) await Context.Message.ReplyAsync(message, deny);
            return;
        }

        var key = ResolveConversationKey(ChatScene.Friend, message.PeerId, message.SenderId);
        await ProcessAiAsync(message, key, prefixToStrip: "#ai", isGroup: false).ConfigureAwait(false);
    }

    private async Task HandleGroupAiAsync(GroupIncomingMessage message)
    {
        if (!CheckPermission(message.SenderId, out var deny))
        {
            if (deny is not null) await ReplyToGroup(message, deny);
            return;
        }

        var key = ResolveConversationKey(ChatScene.Group, message.PeerId, message.SenderId);
        await ProcessAiAsync(message, key, prefixToStrip: "#ai", isGroup: true).ConfigureAwait(false);
    }

    private async Task HandleGroupMentionAsync(GroupIncomingMessage message)
    {
        if (!CheckPermission(message.SenderId, out var deny))
        {
            if (deny is not null) await ReplyToGroup(message, deny);
            return;
        }

        var key = ResolveConversationKey(ChatScene.Group, message.PeerId, message.SenderId);
        // mention 时不需要去掉前缀，但要确保收集结果不为空
        await ProcessAiAsync(message, key, prefixToStrip: null, isGroup: true).ConfigureAwait(false);
    }

    private async Task ProcessAiAsync(IncomingMessage message, ConversationKey key, string? prefixToStrip, bool isGroup)
    {
        var collector = new ResourceCollector(Context, _fetcher, _cache, _config.Resources);
        List<ChatPart> parts;
        try
        {
            parts = isGroup
                ? await collector.CollectAsync((GroupIncomingMessage)message).ConfigureAwait(false)
                : await collector.CollectAsync((FriendIncomingMessage)message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            BotLog.Error($"[AiChat] 收集资源失败: {ex}");
            await ReplyAsync(message, isGroup, "[AiChat] 处理消息资源失败。").ConfigureAwait(false);
            return;
        }

        if (!string.IsNullOrEmpty(prefixToStrip))
        {
            StripPrefixFromFirstTextPart(parts, prefixToStrip);
        }

        // 诊断：把这次提取到的资源种类和数量打日志，便于排查"图片被吞"的问题。
        var imgCount = parts.Count(p => p is ImagePart);
        var fileTextCount = parts.Count(p => p is FileTextPart);
        var fileMetaCount = parts.Count(p => p is FileMetaPart);
        var textCount = parts.Count(p => p is TextPart);
        BotLog.Info($"[AiChat] 收集结果: text={textCount} image={imgCount} fileText={fileTextCount} fileMeta={fileMetaCount}");

        if (parts.Count == 0)
        {
            if (_config.Behaviour.HintOnEmptyInput)
            {
                await ReplyAsync(message, isGroup, "[AiChat] 没有可处理的内容。发送 #aihelp 查看用法。").ConfigureAwait(false);
            }
            return;
        }

        var senderId = isGroup
            ? ((GroupIncomingMessage)message).SenderId
            : ((FriendIncomingMessage)message).SenderId;
        var settings = _userSettings.GetOrCreate(senderId);
        var chatMode = settings.ChatMode ?? _config.Default.DefaultChatMode;

        // 选择模型：shared 模式用共享会话的公共设置，否则用个人偏好
        var isSingleMode = chatMode == "single";
        var isSharedMode = chatMode == "shared" && isGroup;

        ConversationState? convo = null;
        try
        {
            convo = isSingleMode ? null : await _conversations.AcquireAsync(key).ConfigureAwait(false);

            var modelName = isSharedMode
                ? convo?.SharedModel ?? _config.Default.Model
                : settings.PreferredModel ?? _config.Default.Model;
            var model = ResolveModel(modelName);
            if (model is null && !string.Equals(modelName, _config.Default.Model, StringComparison.OrdinalIgnoreCase))
            {
                var fallbackModel = ResolveModel(_config.Default.Model);
                if (fallbackModel is not null)
                {
                    model = fallbackModel;
                    await ReplyAsync(message, isGroup,
                        $"[AiChat] 模型 {modelName} 不可用，已自动回退到 {model.Name}。可用 #model 重新选择。").ConfigureAwait(false);
                    if (isSharedMode && convo is not null)
                    {
                        convo.SharedModel = null;
                        _conversations.SaveMetadata(convo);
                    }
                    else
                    {
                        settings.PreferredModel = null;
                        _ = _userSettings.SaveAsync();
                    }
                }
            }
            if (model is null)
            {
                await ReplyAsync(message, isGroup, $"[AiChat] 未找到模型 {modelName}，请联系管理员检查配置。").ConfigureAwait(false);
                return;
            }

            // 视觉能力检查
            var hasImage = imgCount > 0;
            if (hasImage && !model.SupportsVision)
            {
                BotLog.Warning($"[AiChat] 模型 {model.Name} 标记为不支持视觉(supports_vision=false)，{imgCount} 张图片将被剥离。如需让模型看图，请在 [[models]] 段对应模型设置 supports_vision = true。");
                if (_config.Behaviour.WarnOnNoVision)
                {
                    parts = parts.Where(p => p is not ImagePart).Append(
                        new TextPart("[注意: 当前模型不支持视觉，已忽略消息中的图片。]")).ToList();
                }
            }
            else if (hasImage)
            {
                BotLog.Info($"[AiChat] 准备把 {imgCount} 张图片以 base64 形式发送给 {model.Name}。");
            }

            await _globalConcurrency.WaitAsync().ConfigureAwait(false);
            try
            {
                // shared 模式用共享会话的公共 prompt，否则用个人设置
                var systemPrompt = isSharedMode && convo?.SharedPrompt is not null
                    ? convo.SharedPrompt
                    : !string.IsNullOrWhiteSpace(settings.SystemPrompt)
                        ? settings.SystemPrompt
                        : _config.Default.DefaultPrompt;
                systemPrompt = RenderPromptPlaceholders(systemPrompt, message);
                if (isSharedMode && !string.IsNullOrWhiteSpace(_config.Default.SharedPromptSuffix))
                {
                    systemPrompt += "\n" + RenderPromptPlaceholders(_config.Default.SharedPromptSuffix, message);
                }

                var userParts = isSharedMode && message is GroupIncomingMessage groupMessage
                    ? AddSharedSpeakerPrefix(parts, groupMessage)
                    : parts;
                var userTurn = new ChatTurn("user", userParts, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

                string assistantText;
                try
                {
                    var hasImageInCurrent = parts.Any(p => p is ImagePart);

                    if (hasImageInCurrent)
                    {
                        // 有图片：用流式 body 写入，避免大 string 进 LOH
                        var descriptors = BuildMessageDescriptors(systemPrompt, convo?.Turns ?? [], userTurn);
                        assistantText = await _openAi.ChatStreamingBodyAsync(_config.Default, model, descriptors).ConfigureAwait(false);
                    }
                    else
                    {
                        // 纯文本：用普通序列化（更简单、有诊断日志）
                        var openAiMessages = BuildOpenAiMessages(systemPrompt, convo?.Turns ?? [], userTurn);
                        var request = new OpenAiChatRequest
                        {
                            Model = model.Name,
                            Messages = openAiMessages,
                            Stream = false
                        };
                        assistantText = await _openAi.ChatAsync(_config.Default, model, request).ConfigureAwait(false);
                    }
                }
                catch (TaskCanceledException)
                {
                    await ReplyAsync(message, isGroup, "[AiChat] 请求超时。").ConfigureAwait(false);
                    return;
                }
                catch (Exception ex)
                {
                    BotLog.Warning($"[AiChat] 上游调用失败: {ex.Message}");
                    await ReplyAsync(message, isGroup, $"[AiChat] 调用失败: {ex.Message}").ConfigureAwait(false);
                    return;
                }

                // single 模式不保存历史
                if (convo is not null)
                {
                    var assistantTurn = new ChatTurn(
                        "assistant",
                        [new TextPart(assistantText)],
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    await _conversations.AppendTurnsAsync(convo, userTurn, assistantTurn).ConfigureAwait(false);

                    // Context compact：当轮次接近上限时自动压缩前半部分为摘要
                    await TryCompactConversationAsync(convo, model).ConfigureAwait(false);
                }

                await ReplyAsync(message, isGroup, assistantText).ConfigureAwait(false);
            }
            finally
            {
                _globalConcurrency.Release();
            }
        }
        finally
        {
            if (convo is not null) _conversations.Release(key);
        }
    }

    // -------- #prompt --------

    private async Task HandleFriendPromptAsync(FriendIncomingMessage message)
    {
        var arg = StripPrefix(message.GetPlainText(), "#prompt");
        await ApplyPromptAsync(message.SenderId, arg, async reply =>
            await Context.Message.ReplyAsync(message, reply)).ConfigureAwait(false);
    }

    private async Task HandleGroupPromptAsync(GroupIncomingMessage message)
    {
        var settings = _userSettings.GetOrCreate(message.SenderId);
        var mode = settings.ChatMode ?? _config.Default.DefaultChatMode;
        var arg = StripPrefix(message.GetPlainText(), "#prompt");

        // shared 模式：修改/重置操作写入共享会话，仅 admin 可操作
        if (mode == "shared")
        {
            if (!string.IsNullOrEmpty(arg) && !Context.IsAdmin(message.SenderId))
            {
                await ReplyToGroup(message, "共享模式下仅管理员可修改 system prompt。").ConfigureAwait(false);
                return;
            }

            var key = ResolveConversationKey(ChatScene.Group, message.PeerId, message.SenderId);
            var convo = await _conversations.AcquireAsync(key).ConfigureAwait(false);
            try
            {
                if (string.IsNullOrEmpty(arg))
                {
                    var current = convo.SharedPrompt ?? _config.Default.DefaultPrompt;
                    await ReplyToGroup(message, $"当前共享 prompt:\n{current}\n用法: #prompt <文本> 设置；#prompt - 重置").ConfigureAwait(false);
                    return;
                }

                if (arg == "-" || string.Equals(arg, "reset", StringComparison.OrdinalIgnoreCase))
                {
                    convo.SharedPrompt = null;
                    _conversations.SaveMetadata(convo);
                    await ReplyToGroup(message, "已重置共享 system prompt。").ConfigureAwait(false);
                    return;
                }

                convo.SharedPrompt = arg;
                _conversations.SaveMetadata(convo);
                await ReplyToGroup(message, "共享 system prompt 已更新。").ConfigureAwait(false);
            }
            finally
            {
                _conversations.Release(key);
            }
            return;
        }

        await ApplyPromptAsync(message.SenderId, arg, async reply =>
            await ReplyToGroup(message, reply)).ConfigureAwait(false);
    }

    private async Task ApplyPromptAsync(long userId, string arg, Func<string, Task> reply)
    {
        var settings = _userSettings.GetOrCreate(userId);

        if (string.IsNullOrEmpty(arg))
        {
            var current = settings.SystemPrompt;
            var defaultPrompt = _config.Default.DefaultPrompt;
            var sb = new StringBuilder();
            sb.AppendLine("当前 system prompt:");
            sb.AppendLine(string.IsNullOrEmpty(current) ? $"  (未设置, 使用默认: {defaultPrompt})" : current);
            sb.AppendLine("用法: #prompt <文本>  设置；#prompt -  重置");
            await reply(sb.ToString().TrimEnd()).ConfigureAwait(false);
            return;
        }

        if (arg == "-" || string.Equals(arg, "reset", StringComparison.OrdinalIgnoreCase))
        {
            settings.SystemPrompt = null;
            await _userSettings.SaveAsync().ConfigureAwait(false);
            await reply("已重置 system prompt。").ConfigureAwait(false);
            return;
        }

        settings.SystemPrompt = arg;
        await _userSettings.SaveAsync().ConfigureAwait(false);
        await reply("system prompt 已更新。").ConfigureAwait(false);
    }

    // -------- #clear --------

    private async Task HandleFriendClearAsync(FriendIncomingMessage message)
    {
        var key = ResolveConversationKey(ChatScene.Friend, message.PeerId, message.SenderId);
        _conversations.Clear(key);
        await Context.Message.ReplyAsync(message, "已清空当前会话。").ConfigureAwait(false);
    }

    private async Task HandleGroupClearAsync(GroupIncomingMessage message)
    {
        var settings = _userSettings.GetOrCreate(message.SenderId);
        var mode = settings.ChatMode ?? _config.Default.DefaultChatMode;

        // shared 模式下只有 owner/admin 能清空共享会话
        if (mode == "shared" && !Context.IsAdmin(message.SenderId))
        {
            await ReplyToGroup(message, "共享模式下仅管理员可清空会话。").ConfigureAwait(false);
            return;
        }

        var key = ResolveConversationKey(ChatScene.Group, message.PeerId, message.SenderId);
        _conversations.Clear(key);

        var hint = mode == "shared" ? "已清空本群共享会话。" : "已清空你在本群的会话。";
        await ReplyToGroup(message, hint).ConfigureAwait(false);
    }

    // -------- #refresh --------

    private async Task HandleFriendRefreshAsync(FriendIncomingMessage message)
    {
        if (!CheckPermission(message.SenderId, out var deny))
        {
            if (deny is not null) await Context.Message.ReplyAsync(message, deny);
            return;
        }

        await RefreshModelsAsync(reply => Context.Message.ReplyAsync(message, reply)).ConfigureAwait(false);
    }

    private async Task HandleGroupRefreshAsync(GroupIncomingMessage message)
    {
        if (!CheckPermission(message.SenderId, out var deny))
        {
            if (deny is not null) await ReplyToGroup(message, deny);
            return;
        }

        await RefreshModelsAsync(reply => ReplyToGroup(message, reply)).ConfigureAwait(false);
    }

    private async Task RefreshModelsAsync(Func<string, Task> reply)
    {
        var providers = _config.Providers.Count(p => p.FetchModels);
        if (providers == 0)
        {
            await reply("没有配置 fetch_models = true 的 provider，无法刷新模型列表。").ConfigureAwait(false);
            return;
        }

        var before = _config.Models.Count;
        var added = await FetchAndMergeProviderModelsAsync().ConfigureAwait(false);
        await reply($"模型列表已刷新。provider={providers}，当前模型数={_config.Models.Count}，新增={added}，刷新前={before}。").ConfigureAwait(false);
    }

    // -------- #model --------

    private async Task HandleFriendModelAsync(FriendIncomingMessage message)
    {
        var arg = StripPrefix(message.GetPlainText(), "#model");
        await ApplyModelAsync(message.SenderId, arg, isAdmin: Context.IsAdmin(message.SenderId),
            reply => Context.Message.ReplyAsync(message, reply)).ConfigureAwait(false);
    }

    private async Task HandleGroupModelAsync(GroupIncomingMessage message)
    {
        var settings = _userSettings.GetOrCreate(message.SenderId);
        var mode = settings.ChatMode ?? _config.Default.DefaultChatMode;
        var arg = StripPrefix(message.GetPlainText(), "#model");

        // shared 模式：修改操作写入共享会话，仅 admin 可操作
        if (mode == "shared")
        {
            if (!string.IsNullOrEmpty(arg) && !Context.IsAdmin(message.SenderId))
            {
                await ReplyToGroup(message, "共享模式下仅管理员可切换模型。").ConfigureAwait(false);
                return;
            }

            var key = ResolveConversationKey(ChatScene.Group, message.PeerId, message.SenderId);
            var convo = await _conversations.AcquireAsync(key).ConfigureAwait(false);
            try
            {
                if (string.IsNullOrEmpty(arg))
                {
                    // 查看列表，30s 后撤回
                    await ApplyModelAsync(message.SenderId, arg, isAdmin: Context.IsAdmin(message.SenderId),
                        async reply =>
                        {
                            var currentShared = FormatSelectedModel(convo.SharedModel, includeDefaultMark: true);
                            var fullReply = $"{reply}\n当前共享模型: {currentShared}";
                            var resp = await Context.Message.ReplyAsync(message, fullReply).ConfigureAwait(false);
                            ScheduleRecall(message.Group.GroupId, resp.MessageSeq, 30);
                        }).ConfigureAwait(false);
                    return;
                }

                if (arg == "-" || string.Equals(arg, "reset", StringComparison.OrdinalIgnoreCase))
                {
                    convo.SharedModel = null;
                    _conversations.SaveMetadata(convo);
                    await ReplyToGroup(message, "已恢复共享模型为默认。").ConfigureAwait(false);
                    return;
                }

                var target = ResolveModelForSelection(arg);
                if (target is null)
                {
                    await ReplyToGroup(message, BuildModelNotFoundReply(arg)).ConfigureAwait(false);
                    return;
                }

                if (_config.Permissions.AdminOnlyModels.Contains(target.Name, StringComparer.OrdinalIgnoreCase) &&
                    !Context.IsAdmin(message.SenderId))
                {
                    await ReplyToGroup(message, $"模型 {target.Name} 仅管理员可切换。").ConfigureAwait(false);
                    return;
                }

                convo.SharedModel = GetModelDisplayId(target);
                _conversations.SaveMetadata(convo);
                await ReplyToGroup(message, $"共享模型已切换到: {GetModelDisplayId(target)}").ConfigureAwait(false);
            }
            finally
            {
                _conversations.Release(key);
            }
            return;
        }

        // 非 shared 模式
        if (string.IsNullOrEmpty(arg))
        {
            await ApplyModelAsync(message.SenderId, arg, isAdmin: Context.IsAdmin(message.SenderId),
                async reply =>
                {
                    var resp = await Context.Message.ReplyAsync(message, reply).ConfigureAwait(false);
                    ScheduleRecall(message.Group.GroupId, resp.MessageSeq, 30);
                }).ConfigureAwait(false);
            return;
        }

        await ApplyModelAsync(message.SenderId, arg, isAdmin: Context.IsAdmin(message.SenderId),
            reply => ReplyToGroup(message, reply)).ConfigureAwait(false);
    }

    private async Task ApplyModelAsync(long userId, string arg, bool isAdmin, Func<string, Task> reply)
    {
        var settings = _userSettings.GetOrCreate(userId);

        if (string.IsNullOrEmpty(arg))
        {
            var sb = new StringBuilder();
            sb.AppendLine("可用模型:");
            if (_config.Models.Count == 0)
            {
                sb.AppendLine($"  - {_config.Default.Model} (默认)");
            }
            else
            {
                foreach (var m in _config.Models)
                {
                    var displayId = GetModelDisplayId(m);
                    var mark = IsSelectedModel(m, settings.PreferredModel) ? " *" : "";
                    var visionMark = m.SupportsVision ? " [vision]" : "";
                    var adminMark = _config.Permissions.AdminOnlyModels.Contains(m.Name, StringComparer.OrdinalIgnoreCase)
                        ? " [admin]" : "";
                    var displayLabel = !string.IsNullOrEmpty(m.DisplayName) ? $" ({m.DisplayName})" : "";
                    sb.AppendLine($"  - {displayId}{displayLabel}{visionMark}{adminMark}{mark}");
                }
            }
            sb.AppendLine($"当前偏好: {FormatSelectedModel(settings.PreferredModel, includeDefaultMark: true)}");
            sb.AppendLine("用法: #model <name>  切换；#model -  恢复默认");
            await reply(sb.ToString().TrimEnd()).ConfigureAwait(false);
            return;
        }

        if (arg == "-" || string.Equals(arg, "reset", StringComparison.OrdinalIgnoreCase))
        {
            settings.PreferredModel = null;
            await _userSettings.SaveAsync().ConfigureAwait(false);
            await reply("已恢复为默认模型。").ConfigureAwait(false);
            return;
        }

        var target = ResolveModelForSelection(arg);
        if (target is null)
        {
            await reply(BuildModelNotFoundReply(arg)).ConfigureAwait(false);
            return;
        }

        if (_config.Permissions.AdminOnlyModels.Contains(target.Name, StringComparer.OrdinalIgnoreCase) && !isAdmin)
        {
            await reply($"模型 {target.Name} 仅管理员可切换。").ConfigureAwait(false);
            return;
        }

        settings.PreferredModel = GetModelDisplayId(target);
        await _userSettings.SaveAsync().ConfigureAwait(false);
        await reply($"已切换到模型: {GetModelDisplayId(target)}").ConfigureAwait(false);
    }

    // -------- 工具方法 --------

    /// <summary>
    /// Context compact：当会话轮次达到 maxTurns 的 80% 时，把前半部分对话压缩为一条摘要 turn，
    /// 保留最近的对话不动。这样既不丢失重要上下文，又控制了 token 消耗。
    /// </summary>
    private async Task TryCompactConversationAsync(ConversationState convo, ModelEntry model)
    {
        var maxItems = _config.History.MaxTurns * 2; // user+assistant 配对
        var threshold = (int)(maxItems * 0.8);

        if (convo.Turns.Count < threshold)
            return;

        // 把前半部分压缩，保留后半部分原样
        var halfCount = convo.Turns.Count / 2;
        // 确保 halfCount 是偶数（user+assistant 配对）
        if (halfCount % 2 != 0) halfCount--;
        if (halfCount < 2) return;

        var turnsToCompact = convo.Turns.GetRange(0, halfCount);

        // 构造摘要请求
        var summaryMessages = new List<OpenAiMessage>
        {
            new()
            {
                Role = "system",
                Content = "请用简洁的中文总结以下对话的关键信息和结论，保留重要的事实、决定和上下文。不要使用Markdown格式。"
            }
        };

        foreach (var turn in turnsToCompact)
        {
            summaryMessages.Add(TurnToMessage(turn, includeImages: false));
        }

        summaryMessages.Add(new OpenAiMessage
        {
            Role = "user",
            Content = "请总结以上对话。"
        });

        try
        {
            var summaryRequest = new OpenAiChatRequest
            {
                Model = model.Name,
                Messages = summaryMessages,
                Stream = false
            };

            var summary = await _openAi.ChatAsync(_config.Default, model, summaryRequest).ConfigureAwait(false);

            // 用摘要 turn 替换前半部分
            convo.Turns.RemoveRange(0, halfCount);
            convo.Turns.Insert(0, new ChatTurn(
                "assistant",
                [new TextPart($"[对话摘要] {summary}")],
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

            // 持久化压缩后的状态
            await _conversations.RewriteAsync(convo).ConfigureAwait(false);

            BotLog.Info($"[AiChat] Context compact 完成: {halfCount} turns -> 1 摘要, 剩余 {convo.Turns.Count} turns");
        }
        catch (Exception ex)
        {
            // compact 失败不影响正常对话，降级为旧截断行为
            BotLog.Warning($"[AiChat] Context compact 失败，回退到截断: {ex.Message}");
        }
    }

    private bool CheckPermission(long userId, out string? denyReason)
    {
        denyReason = null;
        if (_config.Permissions.DenyUsers.Contains(userId))
        {
            denyReason = "[AiChat] 你已被禁用此功能。";
            return false;
        }
        if (_config.Permissions.AllowUsers.Length > 0 && !_config.Permissions.AllowUsers.Contains(userId))
        {
            denyReason = "[AiChat] 此功能仅对白名单用户开放。";
            return false;
        }
        return true;
    }

    private static List<ChatPart> AddSharedSpeakerPrefix(List<ChatPart> parts, GroupIncomingMessage message)
    {
        var displayName = !string.IsNullOrWhiteSpace(message.GroupMember.Card)
            ? message.GroupMember.Card
            : !string.IsNullOrWhiteSpace(message.GroupMember.Nickname)
                ? message.GroupMember.Nickname
                : message.SenderId.ToString();
        var prefix = $"[{displayName}/{message.SenderId}] ";

        var result = new List<ChatPart>(parts.Count + 1);
        var prefixed = false;
        foreach (var part in parts)
        {
            if (!prefixed && part is TextPart text)
            {
                result.Add(new TextPart(prefix + text.Text));
                prefixed = true;
            }
            else
            {
                result.Add(part);
            }
        }

        if (!prefixed)
        {
            result.Insert(0, new TextPart(prefix + "发送了一条非文本消息。"));
        }

        return result;
    }

    private static string RenderPromptPlaceholders(string prompt, IncomingMessage message)
    {
        var now = DateTimeOffset.Now;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["time"] = now.ToString("yyyy-MM-dd HH:mm:ss zzz"),
            ["date"] = now.ToString("yyyy-MM-dd"),
            ["userid"] = string.Empty,
            ["groupname"] = string.Empty,
            ["groupid"] = string.Empty,
            ["nickname"] = string.Empty,
            ["card"] = string.Empty
        };

        switch (message)
        {
            case GroupIncomingMessage group:
                values["userid"] = group.SenderId.ToString();
                values["groupname"] = group.Group.GroupName;
                values["groupid"] = group.Group.GroupId.ToString();
                values["nickname"] = group.GroupMember.Nickname;
                values["card"] = group.GroupMember.Card;
                break;
            case FriendIncomingMessage friend:
                values["userid"] = friend.SenderId.ToString();
                values["nickname"] = friend.Friend.Nickname;
                break;
        }

        foreach (var (key, value) in values)
        {
            prompt = prompt.Replace("{" + key + "}", value, StringComparison.OrdinalIgnoreCase);
        }

        return prompt;
    }

    /// <summary>
    /// 根据用户的 ChatMode 设置决定 ConversationKey：
    /// - single: 返回一个唯一 key（不会命中缓存，等效于无历史）
    /// - multi（默认）: 按用户独立
    /// - shared: 群聊时所有人共享同一个 key（userId 固定为 0）
    /// </summary>
    private ConversationKey ResolveConversationKey(ChatScene scene, long peerId, long userId)
    {
        var settings = _userSettings.GetOrCreate(userId);
        var mode = settings.ChatMode ?? _config.Default.DefaultChatMode;

        return mode switch
        {
            "single" => new ConversationKey(scene, peerId, -DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            "shared" when scene == ChatScene.Group => new ConversationKey(scene, peerId, 0),
            _ => new ConversationKey(scene, peerId, userId)
        };
    }

    private static string StripPrefix(string text, string prefix)
    {
        text = text.TrimStart();
        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            text = text[prefix.Length..];
        }
        return text.Trim();
    }

    /// <summary>
    /// 从抽取出的 parts 列表中找到第一个 TextPart，去掉指令前缀（例如 "#ai"），
    /// 如果剩余文本为空则把这一段整个移除，避免把 "#ai" 字面量传给模型。
    /// </summary>
    private static void StripPrefixFromFirstTextPart(List<ChatPart> parts, string prefix)
    {
        for (var i = 0; i < parts.Count; i++)
        {
            if (parts[i] is not TextPart tp)
            {
                continue;
            }

            var stripped = StripPrefix(tp.Text, prefix);
            if (string.IsNullOrEmpty(stripped))
            {
                parts.RemoveAt(i);
            }
            else
            {
                parts[i] = new TextPart(stripped);
            }
            return;
        }
    }

    private ModelEntry? ResolveModel(string name)
    {
        name = name.Trim();
        if (_config.Models.Count == 0)
        {
            // 没有模型注册时退化为只用 default 段
            if (string.Equals(name, _config.Default.Model, StringComparison.OrdinalIgnoreCase))
            {
                return new ModelEntry { Name = _config.Default.Model };
            }
            return null;
        }

        // 优先支持 "provider-name" 格式匹配（列表显示格式）。必须放在裸 name 前，
        // 避免 provider fetch 拉到同名裸模型时抢先命中错误 provider。
        var match = _config.Models.FirstOrDefault(m =>
            !string.IsNullOrEmpty(m.Provider) &&
            string.Equals(GetModelDisplayId(m), name, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match;

        // 精确匹配 model name
        match = _config.Models.FirstOrDefault(m =>
            string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        return match;
    }

    private ModelEntry? ResolveModelForSelection(string name)
    {
        name = name.Trim();
        if (_config.Models.Count == 0)
        {
            return string.Equals(name, _config.Default.Model, StringComparison.OrdinalIgnoreCase)
                ? new ModelEntry { Name = _config.Default.Model }
                : null;
        }

        return _config.Models.FirstOrDefault(m =>
            string.Equals(GetModelDisplayId(m), name, StringComparison.OrdinalIgnoreCase));
    }

    private string BuildModelNotFoundReply(string keyword)
    {
        var matches = FindModelCandidates(keyword, maxCount: 12);
        if (matches.Count == 0)
        {
            return $"未找到模型: {keyword}。发送 #model 查看可用列表。";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"未找到模型: {keyword}");
        sb.AppendLine("你可能想选:");
        foreach (var m in matches)
        {
            var displayLabel = !string.IsNullOrEmpty(m.DisplayName) ? $" ({m.DisplayName})" : "";
            sb.AppendLine($"  - {GetModelDisplayId(m)}{displayLabel}");
        }
        sb.AppendLine("用法: #model <上面的完整名称>");
        return sb.ToString().TrimEnd();
    }

    private List<ModelEntry> FindModelCandidates(string keyword, int maxCount)
    {
        keyword = keyword.Trim();
        if (string.IsNullOrEmpty(keyword)) return [];

        return _config.Models
            .Where(m =>
                m.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                GetModelDisplayId(m).Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(m.DisplayName) && m.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(m => GetModelDisplayId(m).StartsWith(keyword, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(m => m.Name.StartsWith(keyword, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(GetModelDisplayId, StringComparer.OrdinalIgnoreCase)
            .Take(maxCount)
            .ToList();
    }

    private static string GetModelDisplayId(ModelEntry model) =>
        string.IsNullOrWhiteSpace(model.Provider) ? model.Name : $"{model.Provider}-{model.Name}";

    private bool IsSelectedModel(ModelEntry model, string? selected)
    {
        if (string.IsNullOrWhiteSpace(selected)) return false;

        return string.Equals(model.Name, selected, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(GetModelDisplayId(model), selected, StringComparison.OrdinalIgnoreCase);
    }

    private string FormatSelectedModel(string? selected, bool includeDefaultMark)
    {
        var isDefault = string.IsNullOrWhiteSpace(selected);
        var value = isDefault ? _config.Default.Model : selected!;
        var model = ResolveModel(value);
        var display = model is null ? value : GetModelDisplayId(model);
        return isDefault && includeDefaultMark ? display + " (默认)" : display;
    }

    private List<OpenAiMessage> BuildOpenAiMessages(string systemPrompt, List<ChatTurn> history, ChatTurn currentUserTurn)
    {
        var msgs = new List<OpenAiMessage>(history.Count + 2)
        {
            new() { Role = "system", Content = systemPrompt }
        };

        foreach (var t in history)
        {
            msgs.Add(TurnToMessage(t, includeImages: false));
        }
        msgs.Add(TurnToMessage(currentUserTurn, includeImages: true));
        return msgs;
    }

    /// <summary>
    /// 构建流式请求用的消息描述列表。图片只记录 .b64 文件路径，不读取内容到内存。
    /// </summary>
    private List<ChatMessageDescriptor> BuildMessageDescriptors(string systemPrompt, List<ChatTurn> history, ChatTurn currentUserTurn)
    {
        var descriptors = new List<ChatMessageDescriptor>(history.Count + 2)
        {
            new()
            {
                Role = "system",
                Segments = [new ContentSegment { Kind = SegmentKind.Text, Text = systemPrompt }]
            }
        };

        foreach (var t in history)
        {
            descriptors.Add(TurnToDescriptor(t, includeImages: false));
        }
        descriptors.Add(TurnToDescriptor(currentUserTurn, includeImages: true));
        return descriptors;
    }

    private ChatMessageDescriptor TurnToDescriptor(ChatTurn turn, bool includeImages)
    {
        if (turn.Role == "assistant")
        {
            var combined = string.Concat(turn.Parts.OfType<TextPart>().Select(p => p.Text));
            return new ChatMessageDescriptor
            {
                Role = "assistant",
                Segments = [new ContentSegment { Kind = SegmentKind.Text, Text = combined }]
            };
        }

        var segments = new List<ContentSegment>();
        foreach (var p in turn.Parts)
        {
            switch (p)
            {
                case TextPart tp when !string.IsNullOrEmpty(tp.Text):
                    segments.Add(new ContentSegment { Kind = SegmentKind.Text, Text = tp.Text });
                    break;

                case ImagePart ip:
                    if (!includeImages)
                    {
                        segments.Add(new ContentSegment { Kind = SegmentKind.Text, Text = "[图片已省略，历史上下文不再附带图片内容。]" });
                        break;
                    }

                    // 获取 .b64 文件路径（不读取内容）
                    var b64Path = GetB64FilePath(ip.CachePath);
                    if (b64Path is not null)
                    {
                        segments.Add(new ContentSegment { Kind = SegmentKind.Image, B64FilePath = b64Path });
                    }
                    else if (ip.InlineBytes is { Length: > 0 })
                    {
                        // fallback: 内存中有 inline bytes，生成 data URL
                        var mime = MimeGuesser.SniffImageMime(ip.InlineBytes)
                                   ?? (ip.Mime.StartsWith("image/", StringComparison.Ordinal) ? ip.Mime : "image/png");
                        var dataUrl = $"data:{mime};base64,{Convert.ToBase64String(ip.InlineBytes)}";
                        segments.Add(new ContentSegment { Kind = SegmentKind.Image, InlineDataUrl = dataUrl });
                    }
                    else
                    {
                        segments.Add(new ContentSegment { Kind = SegmentKind.Text, Text = "[图片读取失败]" });
                    }
                    break;

                case FileTextPart ft:
                    var truncMark = ft.Truncated ? " (已截断)" : "";
                    segments.Add(new ContentSegment
                    {
                        Kind = SegmentKind.Text,
                        Text = $"[文件: {ft.Name} ({ft.Size} 字节){truncMark}]\n```\n{ft.Excerpt}\n```"
                    });
                    break;

                case FileMetaPart fm:
                    var hashStr = string.IsNullOrEmpty(fm.Sha256) ? "" : $", sha256={fm.Sha256}";
                    segments.Add(new ContentSegment
                    {
                        Kind = SegmentKind.Text,
                        Text = $"[文件: {fm.Name} ({fm.Size} 字节{hashStr}) (二进制内容未加载)]"
                    });
                    break;
            }
        }

        if (segments.Count == 0)
            segments.Add(new ContentSegment { Kind = SegmentKind.Text, Text = "" });

        return new ChatMessageDescriptor { Role = turn.Role, Segments = segments };
    }

    /// <summary>
    /// 从 cachePath 推导 .b64 文件路径。
    /// </summary>
    private static string? GetB64FilePath(string? cachePath)
    {
        if (string.IsNullOrEmpty(cachePath)) return null;
        var dir = Path.GetDirectoryName(cachePath);
        var name = Path.GetFileNameWithoutExtension(cachePath);
        var b64Path = Path.Combine(dir ?? "", name + ".b64");
        return File.Exists(b64Path) ? b64Path : null;
    }

    private OpenAiMessage TurnToMessage(ChatTurn turn, bool includeImages)
    {
        // assistant 消息：直接 string 形式（绝大多数模型只生成纯文本）。
        if (turn.Role == "assistant")
        {
            var combined = string.Concat(turn.Parts.OfType<TextPart>().Select(p => p.Text));
            return new OpenAiMessage
            {
                Role = "assistant",
                Content = combined
            };
        }

        // user 消息：按多模态数组形式组装。每一段都是 { "type": "...", ... } 的扁平字典。
        var contentParts = new List<Dictionary<string, object>>();
        var hasImage = false;

        foreach (var p in turn.Parts)
        {
            switch (p)
            {
                case TextPart tp:
                    if (!string.IsNullOrEmpty(tp.Text))
                    {
                        contentParts.Add(new Dictionary<string, object>
                        {
                            ["type"] = "text",
                            ["text"] = tp.Text
                        });
                    }
                    break;

                case ImagePart ip:
                    if (!includeImages)
                    {
                        contentParts.Add(new Dictionary<string, object>
                        {
                            ["type"] = "text",
                            ["text"] = "[图片已省略，历史上下文不再附带图片内容。]"
                        });
                        break;
                    }

                    var imageDataUrl = TryReadImageAsDataUrl(ip);
                    if (imageDataUrl is not null)
                    {
                        contentParts.Add(new Dictionary<string, object>
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new Dictionary<string, object>
                            {
                                ["url"] = imageDataUrl
                            }
                        });
                        hasImage = true;
                    }
                    else
                    {
                        contentParts.Add(new Dictionary<string, object>
                        {
                            ["type"] = "text",
                            ["text"] = "[图片读取失败]"
                        });
                    }
                    break;

                case FileTextPart ft:
                    var truncMark = ft.Truncated ? " (已截断)" : "";
                    contentParts.Add(new Dictionary<string, object>
                    {
                        ["type"] = "text",
                        ["text"] = $"[文件: {ft.Name} ({ft.Size} 字节){truncMark}]\n```\n{ft.Excerpt}\n```"
                    });
                    break;

                case FileMetaPart fm:
                    var hashStr = string.IsNullOrEmpty(fm.Sha256) ? "" : $", sha256={fm.Sha256}";
                    contentParts.Add(new Dictionary<string, object>
                    {
                        ["type"] = "text",
                        ["text"] = $"[文件: {fm.Name} ({fm.Size} 字节{hashStr}) (二进制内容未加载)]"
                    });
                    break;
            }
        }

        // 关键: 一旦有图片，必须用数组形式，绝不能塌缩成字符串，
        // 否则模型完全看不到图片，只会收到一段纯文本。
        if (hasImage)
        {
            if (contentParts.Count == 0)
            {
                // 极端情况，加一段空文本占位（应当不会触发）
                contentParts.Add(new Dictionary<string, object>
                {
                    ["type"] = "text",
                    ["text"] = ""
                });
            }
            return new OpenAiMessage { Role = turn.Role, Content = contentParts };
        }

        // 没有图片时，单段纯文本可以塌缩为 string 形式（最大兼容）
        if (contentParts.Count == 1 &&
            contentParts[0].TryGetValue("type", out var t) &&
            t is string and "text" &&
            contentParts[0].TryGetValue("text", out var textObj) &&
            textObj is string textStr)
        {
            return new OpenAiMessage { Role = turn.Role, Content = textStr };
        }

        if (contentParts.Count == 0)
        {
            return new OpenAiMessage { Role = turn.Role, Content = "" };
        }

        return new OpenAiMessage { Role = turn.Role, Content = contentParts };
    }

    /// <summary>
    /// 把图片转成完整的 data URL。优先级：
    /// 1) <see cref="ImagePart.InlineBytes"/>（缓存写盘失败时的兜底）
    /// 2) <see cref="ImagePart.CachePath"/> 落盘文件
    /// 任一来源都失败时返回 null，调用方降级为提示文本。
    /// 这样能保证发给模型的永远是合法的 base64 图片，不会出现空 data URL 或错 mime。
    /// </summary>
    /// <summary>
    /// 把图片转成完整的 data URL。优先从 .b64 缓存文件直接读取（零转换），
    /// 回退到 InlineBytes 时用 ArrayPool 分块编码。
    /// </summary>
    private string? TryReadImageAsDataUrl(ImagePart ip)
    {
        try
        {
            // 优先从 .b64 缓存读取（零转换，零大对象）
            var cached = _cache.ReadDataUrl(ip.CachePath);
            if (cached is not null) return cached;

            // 回退：InlineBytes（仅在缓存写入失败时存在）
            if (ip.InlineBytes is { Length: > 0 } inline)
            {
                var mime = MimeGuesser.SniffImageMime(inline)
                           ?? (ip.Mime.StartsWith("image/", StringComparison.Ordinal) ? ip.Mime : "image/png");

                // 分块编码避免大字符串进 LOH
                var base64Length = ((inline.Length + 2) / 3) * 4;
                var prefix = $"data:{mime};base64,";
                var sb = new StringBuilder(prefix.Length + base64Length);
                sb.Append(prefix);

                const int chunkSize = 48 * 1024;
                var offset = 0;
                while (offset < inline.Length)
                {
                    var count = Math.Min(chunkSize, inline.Length - offset);
                    sb.Append(Convert.ToBase64String(inline, offset, count));
                    offset += count;
                }
                return sb.ToString();
            }

            BotLog.Warning($"[AiChat] 图片缺少可用字节源 (cachePath={ip.CachePath ?? "<null>"})");
            return null;
        }
        catch (Exception ex)
        {
            BotLog.Warning($"[AiChat] 读取图片失败 {ip.CachePath ?? "<inline>"}: {ex.Message}");
            return null;
        }
    }

    private async Task ReplyAsync(IncomingMessage message, bool isGroup, string text)
    {
        if (isGroup)
        {
            await ReplyToGroup((GroupIncomingMessage)message, text).ConfigureAwait(false);
        }
        else
        {
            await Context.Message.ReplyAsync((FriendIncomingMessage)message, text).ConfigureAwait(false);
        }
    }

    private Task ReplyToGroup(GroupIncomingMessage message, string text)
    {
        return _config.Behaviour.ReplyWithQuote ? Context.Message.QuoteReplyAsync(message, new TextOutgoingSegment(text)) : Context.Message.ReplyAsync(message, text);
    }

    /// <summary>
    /// 延迟指定秒数后撤回群消息。fire-and-forget，失败静默。
    /// </summary>
    private void ScheduleRecall(long groupId, long messageSeq, int delaySeconds)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds)).ConfigureAwait(false);
                await Context.Message.RecallGroupMessageAsync(groupId, messageSeq).ConfigureAwait(false);
            }
            catch
            {
                // 撤回失败（权限不足等）静默忽略
            }
        });
    }
}
