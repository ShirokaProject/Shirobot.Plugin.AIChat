
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.Contracts;
using ShiroBot.AiChatPlugin.Conversation;
using ShiroBot.AiChatPlugin.OpenAi;
using ShiroBot.AiChatPlugin.Resources;
using ShiroBot.AiChatPlugin.UserState;
using ShiroBot.AiChatPlugin.Tools;
using ShiroBot.AiChatPlugin.Tools.Mcp;
using ShiroBot.AiChatPlugin.Tools.Skills;
using ShiroBot.AiChatPlugin.Tools.Memory;
using ShiroBot.AiChatPlugin.Proactive;
using ShiroBot.AiChatPlugin.Tools.Timers;
using ShiroBot.AiChatPlugin.Usage;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Config;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

[assembly: ShiroBotPackage("shirobot.plugin.aichat", ShiroBotPackageKind.Plugin, "1.4.11")]
[assembly: RequiresShiroBotPackage("shirobot.model.qq", MinimumVersion = "0.9.8")]

namespace ShiroBot.AiChatPlugin;


[BotPlugin(id: "AiChatPlugin",
    Name = "AI聊天插件",
    Author = "ShirokaProject",
    Category = PluginCategory.Chat,
    Description = "支持上下文/图片/文件/历史/模型切换的 OpenAI 兼容聊天插件。",
    Version = "1.4.11",
    GithubRepo = "ShirokaProject/Shirobot.Plugin.AIChat",
    IsPluginSingleFile = true,
    SharedAssemblies = "ShiroBot.Model.QQ")]
public sealed partial class AiChatPlugin : PluginBase, IConfigurableComponent<PluginConfig>
{
    private PluginConfig _config = new();
    private readonly ProviderModelCatalog _providerModels = new();
    private readonly CancellationTokenSource _backgroundStop = new();
    private readonly Lock _backgroundGate = new();
    private readonly HashSet<Task> _recallTasks = [];
    private readonly Dictionary<string, string> _selfNames = new(StringComparer.OrdinalIgnoreCase);

    private ConversationStore _conversations = null!;
    private UserSettingsStore _userSettings = null!;
    private OpenAiClient _openAi = null!;
    private ResourceFetcher _fetcher = null!;
    private ResourceCache _cache = null!;
    private SemaphoreSlim _globalConcurrency = null!;
    private readonly ProactiveTrigger _proactive = new();
    /// <summary>每个群等待判断的消息；同一个群同时只有一个等待任务。</summary>
    private readonly Dictionary<string, PendingProactive> _pendingProactive = new();
    private sealed class PendingProactive(DateTimeOffset due)
    {
        /// <summary>上次判断之后的新消息，判断时全部交给模型，不只看最后一条。</summary>
        public readonly List<MessageEvent> Batch = [];
        /// <summary>批次里提到机器人的消息。</summary>
        public readonly HashSet<MessageEvent> Mentions = [];
        public DateTimeOffset Due = due;
        /// <summary>正在交给模型判断的那批消息；判断期间其中有消息被撤回时取消这次判断。</summary>
        public MessageEvent[]? Judging;
        public CancellationTokenSource? JudgingAbort;
        /// <summary>判断期间被撤回的消息 ID。</summary>
        public readonly HashSet<string> RecalledWhileJudging = [];
        /// <summary>判断期间同一个人又发了消息：取消这次判断，和新消息一起重新判断。</summary>
        public bool Interrupted;
    }
    private readonly record struct ProactiveOutcome(TimeSpan? RetryAfter, bool ReplyStarted);
    private const int MaxProactiveBatch = 30;
    /// <summary>冷却等待超过这个时间就不再补判，避免回复很久以前的消息。</summary>
    private static readonly TimeSpan MaxProactiveDeferral = TimeSpan.FromMinutes(2);
    private ChatToolRegistry _tools = new();
    private SkillStore _skillStore = null!;
    private McpHub? _mcp;
    /// <summary>当前 MCP 配置的快照，配置没变时热重载不重启 MCP 进程。</summary>
    private string? _mcpConfigKey;
    private readonly PendingAiRequests _pendingRequests = new();
    private TimerScheduler _timerScheduler = null!;
    private MemoryNotebookStore _memory = null!;
    private UsageStats _usage = null!;
    private Task? _memoryReview;

    private string PluginRoot => Path.GetDirectoryName(Context.Config.ConfigPath)
                                 ?? AppContext.BaseDirectory;

    PluginConfig IConfigurableComponent<PluginConfig>.CurrentConfigValue => _config;

    Task<object> IConfigurableComponent.InitializeConfigAsync(
        IConfigContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureDefaultConfigTemplate();
        _config = context.Load<PluginConfig>();
        return Task.FromResult<object>(_config);
    }

    async Task IConfigurableComponent<PluginConfig>.OnConfigChangedAsync(
        PluginConfig previous, PluginConfig current, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _config = current;
        ApplyConfigDerivatives(initial: false);
        await FetchAndMergeProviderModelsAsync().ConfigureAwait(false);
        BotLog.Info("[AiChat] 配置已热重载。");
    }

    protected override async Task LoadAsync()
    {
        ApplyConfigDerivatives(initial: true);
        await _usage.LoadAsync().ConfigureAwait(false);
        Context.Lifetime.SetInterval(TimeSpan.FromMinutes(1), _ => _usage.SaveAsync());
        RegisterUsagePage();
        RegisterSkillsPage();

        // 从配置了 fetch_models = true 的 provider 自动拉取模型列表
        await FetchAndMergeProviderModelsAsync().ConfigureAwait(false);

        // 对其他插件开放模型；是否开放在每次调用时按当前配置判断。
        ChatModels.Complete.Provide(Context, HandleModelCompleteAsync);
        ChatModels.List.Provide(Context, _ => Task.FromResult(ListPluginModels()));

        // 私聊
        DirectCommands.MapExact("#aihelp", HandleFriendHelpAsync);
        DirectCommands.MapPrefix("#aimode", HandleFriendAiModeAsync);
        DirectCommands.MapPrefix("#imagegen", HandleImageGenAsync);
        DirectCommands.MapPrefix("#ai", HandleFriendAiAsync);
        DirectCommands.MapPrefix("#prompt", HandleFriendPromptAsync);
        DirectCommands.MapExact("#clear", HandleFriendClearAsync);
        DirectCommands.MapPrefix("#model", HandleFriendModelAsync);
        DirectCommands.MapExact("#refresh", HandleFriendRefreshAsync);
        DirectCommands.MapPrefix("#timer", HandleTimerListAsync);
        DirectCommands.MapPrefix("#memory", HandleMemoryAsync);
        DirectCommands.MapExact("#mcp", HandleMcpAsync);
        DirectCommands.MapWhen(ShouldAutoReplyDirectMessage, HandleFriendAutoReplyAsync);

        // 群聊
        GroupCommands.MapExact("#aihelp", HandleGroupHelpAsync);
        GroupCommands.MapPrefix("#aimode", HandleGroupAiModeAsync);
        GroupCommands.MapPrefix("#imagegen", HandleImageGenAsync);
        GroupCommands.MapPrefix("#ai", HandleGroupAiAsync);
        GroupCommands.MapPrefix("#prompt", HandleGroupPromptAsync);
        GroupCommands.MapExact("#clear", HandleGroupClearAsync);
        GroupCommands.MapPrefix("#model", HandleGroupModelAsync);
        GroupCommands.MapExact("#refresh", HandleGroupRefreshAsync);
        GroupCommands.MapPrefix("#timer", HandleTimerListAsync);
        GroupCommands.MapPrefix("#memory", HandleMemoryAsync);
        GroupCommands.MapExact("#mcp", HandleMcpAsync);
        GroupCommands.MapWhen(message => !string.IsNullOrWhiteSpace(message.SelfId) && message.HasMention(message.SelfId), HandleGroupMentionAsync);
        GroupCommands.MapWhen(ShouldRecordGroupMessage, HandleGroupChatterAsync);

        Events.Map<MessageDeletedEvent>(HandleMessageDeletedAsync);
        _timerScheduler.Start();
        _memoryReview = Task.Run(() => RunMemoryReviewLoopAsync(_backgroundStop.Token));
        BotLog.Info("[AiChat] 插件已加载。");
    }

    protected override async Task OnUnloadAsync()
    {
        await _backgroundStop.CancelAsync().ConfigureAwait(false);
        // 先取消进行中的 AI 请求：主动回复的补判任务可能正在等待模型。
        await _pendingRequests.StopAsync().ConfigureAwait(false);
        await _imageRequests.StopAsync().ConfigureAwait(false);
        Task[] recalls;
        lock (_backgroundGate) recalls = _recallTasks.ToArray();
        await Task.WhenAll(recalls).ConfigureAwait(false);
        await Task.WhenAll(_backgroundCompactions.Values).ConfigureAwait(false);
        if (_memoryReview is not null) await _memoryReview.ConfigureAwait(false);
        if (_timerScheduler is not null) await _timerScheduler.DisposeAsync().ConfigureAwait(false);
        _openAi?.Dispose();
        if (_usage is not null) await _usage.SaveAsync().ConfigureAwait(false);
        _fetcher?.Dispose();
        _tools.Dispose();
        if (_mcp is not null) await _mcp.DisposeAsync().ConfigureAwait(false);
        _globalConcurrency?.Dispose();
        _backgroundStop.Dispose();
        BotLog.Info("[AiChat] 插件已卸载。");
    }
}
