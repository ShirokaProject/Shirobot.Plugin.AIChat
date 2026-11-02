using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.AiChatPlugin.Tools;

internal sealed record MemeSelectionInfo(
    string Id,
    string Caption,
    IReadOnlyList<string> Tags,
    object ContractSelection);

internal sealed record MemeSendTargetInfo(
    string Platform,
    string? InstanceId,
    string ChannelType,
    string ChannelId,
    string? MessageId,
    string? SenderId,
    bool IsGroup,
    bool Mention,
    bool Quote);

internal sealed record MemeSendResultInfo(bool Success, string? Error = null);

internal interface IMemeToolService
{
    Task<MemeSelectionInfo?> SelectAsync(
        string query,
        string? emotion,
        string? channelId,
        string? userId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<MemeSendResultInfo> SendAsync(
        MemeSelectionInfo selection,
        MemeSendTargetInfo target,
        string? caption,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional bridge to MemePlugin. AIChat deliberately does not reference or ship
/// ShiroBot.Meme.Contracts; when MemePlugin is loaded, the host has already loaded its
/// shared contract assembly into the Default ALC, so this bridge can resolve the service
/// and invoke it by reflection.
/// </summary>
internal sealed class MemeServiceBridge : IMemeToolService
{
    private const string ContractAssemblyName = "ShiroBot.Meme.Contracts";
    private const string ServiceTypeName = "ShiroBot.Meme.Contracts.IMemeService";
    private const string QueryTypeName = "ShiroBot.Meme.Contracts.MemeQuery";
    private const string TargetTypeName = "ShiroBot.Meme.Contracts.MemeSendTarget";

    private readonly object _service;
    private readonly Type _queryType;
    private readonly Type _targetType;
    private readonly MethodInfo _selectMethod;
    private readonly MethodInfo _sendMethod;

    private MemeServiceBridge(
        object service,
        Type queryType,
        Type targetType,
        MethodInfo selectMethod,
        MethodInfo sendMethod)
    {
        _service = service;
        _queryType = queryType;
        _targetType = targetType;
        _selectMethod = selectMethod;
        _sendMethod = sendMethod;
    }

    public static IMemeToolService? TryCreate(IPluginServices services)
    {
        try
        {
            var assembly = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(candidate =>
                string.Equals(candidate.GetName().Name, ContractAssemblyName, StringComparison.OrdinalIgnoreCase));
            if (assembly is null) return null;

            var serviceType = assembly.GetType(ServiceTypeName, throwOnError: false);
            var queryType = assembly.GetType(QueryTypeName, throwOnError: false);
            var targetType = assembly.GetType(TargetTypeName, throwOnError: false);
            if (serviceType is null || queryType is null || targetType is null) return null;

            var service = services.GetService(serviceType);
            if (service is null) return null;

            var selectMethod = serviceType.GetMethod("SelectAsync", BindingFlags.Public | BindingFlags.Instance);
            var sendMethod = serviceType.GetMethod("SendAsync", BindingFlags.Public | BindingFlags.Instance);
            if (selectMethod is null || sendMethod is null) return null;

            return new MemeServiceBridge(service, queryType, targetType, selectMethod, sendMethod);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            BotLog.Warning($"[AiChat] Meme 契约反射初始化失败: {ex.Message}");
            return null;
        }
    }

    public async Task<MemeSelectionInfo?> SelectAsync(
        string query,
        string? emotion,
        string? channelId,
        string? userId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var contractQuery = Activator.CreateInstance(_queryType, query, emotion, channelId, userId, limit)
            ?? throw new InvalidOperationException("无法创建 MemeQuery。");
        var task = (Task)_selectMethod.Invoke(_service, [contractQuery, cancellationToken])!;
        await task.ConfigureAwait(false);

        var selection = GetTaskResult(task);
        if (selection is null) return null;

        var id = GetStringProperty(selection, "Id");
        var caption = GetStringProperty(selection, "Caption");
        var tags = GetTags(selection);
        return new MemeSelectionInfo(id, caption, tags, selection);
    }

    public async Task<MemeSendResultInfo> SendAsync(
        MemeSelectionInfo selection,
        MemeSendTargetInfo target,
        string? caption,
        CancellationToken cancellationToken = default)
    {
        var contractTarget = Activator.CreateInstance(
            _targetType,
            target.Platform,
            target.InstanceId,
            target.ChannelType,
            target.ChannelId,
            target.MessageId,
            target.SenderId,
            target.IsGroup,
            target.Mention,
            target.Quote) ?? throw new InvalidOperationException("无法创建 MemeSendTarget。");

        var task = (Task)_sendMethod.Invoke(
            _service,
            [selection.ContractSelection, contractTarget, caption, cancellationToken])!;
        await task.ConfigureAwait(false);

        var result = GetTaskResult(task);
        return result is null
            ? new MemeSendResultInfo(false, "Meme 服务未返回结果。")
            : new MemeSendResultInfo(GetBoolProperty(result, "Success"), GetStringOrNullProperty(result, "Error"));
    }

    private static object? GetTaskResult(Task task)
    {
        var resultProperty = task.GetType().GetProperty("Result", BindingFlags.Public | BindingFlags.Instance);
        return resultProperty?.GetValue(task);
    }

    private static string GetStringProperty(object instance, string name) =>
        instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance) as string ?? "";

    private static string? GetStringOrNullProperty(object instance, string name) =>
        instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance) as string;

    private static bool GetBoolProperty(object instance, string name) =>
        instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance) is true;

    private static IReadOnlyList<string> GetTags(object selection)
    {
        var tags = selection.GetType().GetProperty("Tags", BindingFlags.Public | BindingFlags.Instance)?.GetValue(selection);
        return tags is IEnumerable enumerable
            ? enumerable.Cast<object?>().Select(tag => tag?.ToString() ?? "").ToArray()
            : [];
    }
}
