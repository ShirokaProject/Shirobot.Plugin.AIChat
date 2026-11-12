using ShiroBot.AiChatPlugin.Config;

namespace ShiroBot.AiChatPlugin.OpenAi;

/// <summary>Keep successful discovery across config reloads; credentials/endpoints and filters stay scoped to their provider.</summary>
internal sealed class ProviderModelCatalog
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (string Endpoint, string Key, string[] Models)> _lastGood = new(StringComparer.OrdinalIgnoreCase);

    internal int Restore(PluginConfig config)
    {
        lock (_gate)
        {
            var added = 0;
            foreach (var provider in config.Providers.Where(p => p.FetchModels && !string.IsNullOrWhiteSpace(p.ApiKey)))
                if (_lastGood.TryGetValue(provider.Name, out var saved) && saved.Endpoint == provider.BaseUrl.TrimEnd('/') && saved.Key == provider.ApiKey)
                    added += Merge(config, provider, saved.Models);
            return added;
        }
    }

    internal int Remember(PluginConfig config, ProviderEntry provider, IReadOnlyList<string> models)
    {
        if (models.Count == 0) return 0; // An unavailable catalog must not erase the last working list.
        lock (_gate)
        {
            var ids = models.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            _lastGood[provider.Name] = (provider.BaseUrl.TrimEnd('/'), provider.ApiKey, ids);
            return Merge(config, provider, ids);
        }
    }

    private static int Merge(PluginConfig config, ProviderEntry provider, IEnumerable<string> models)
    {
        var existing = config.Models.Where(m => string.Equals(m.Provider, provider.Name, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = models.Where(id => provider.ModelFilter is not { Length: > 0 } || provider.ModelFilter.Any(f => id.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
            .Where(existing.Add).Select(id => new ModelEntry { Name = id, Provider = provider.Name, SupportsVision = true }).ToArray();
        // Readers can keep enumerating their old snapshot while discovery updates the catalog.
        if (added.Length > 0) config.Models = [.. config.Models, .. added];
        return added.Length;
    }
}
