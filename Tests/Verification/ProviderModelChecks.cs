using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ShiroBot.AiChatPlugin;
using ShiroBot.AiChatPlugin.Config;
using ShiroBot.AiChatPlugin.OpenAi;

internal static class ProviderModelChecks
{
    static readonly BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
    static void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
    static void Set(AiChatPlugin plugin, string field, object value) => typeof(AiChatPlugin).GetField(field, Flags)!.SetValue(plugin, value);
    static ModelEntry? Resolve(AiChatPlugin plugin, string id) => (ModelEntry?)typeof(AiChatPlugin).GetMethod("ResolveModel", Flags)!.Invoke(plugin, [id]);
    static Task<int> Fetch(AiChatPlugin plugin) => (Task<int>)typeof(AiChatPlugin).GetMethod("FetchAndMergeProviderModelsAsync", Flags)!.Invoke(plugin, null)!;
    static ProviderEntry Provider(string name) => new() { Name = name, BaseUrl = "https://" + name + ".example/v1", ApiKey = "fixture", FetchModels = true };
    public static async Task RunAsync()
    {
        var plugin = new AiChatPlugin();
        var config = new PluginConfig { Providers = [Provider("first"), Provider("sg")] };
        config.Default.Model = "sg-gemini-3.8-flash-low";
        Set(plugin, "_config", config);
        var cold = Resolve(plugin, config.Default.Model);
        Check(typeof(AiChatPlugin).GetMethod("ResolveModelForSelection", Flags)!.Invoke(plugin, [config.Default.Model]) is ModelEntry { Provider: "sg", Name: "gemini-3.8-flash-low" },
            "Explicit provider models must remain selectable even when discovery omitted them.");
        Check(cold is { Provider: "sg", Name: "gemini-3.8-flash-low" }, "Cold start without /models must keep the provider and strip its prefix.");
        var handler = new Handler();
        using var client = new OpenAiClient(10, ""); client.RegisterProviders(config.Providers);
        var clients = (Dictionary<string, HttpClient>)typeof(OpenAiClient).GetField("_clients", Flags)!.GetValue(client)!;
        foreach (var provider in config.Providers) clients[provider.BaseUrl] = new HttpClient(handler, disposeHandler: false);
        Set(plugin, "_openAi", client);
        await Fetch(plugin);
        Check(config.Models.Count == 4 && config.Models.Count(m => m.Name == "gemini-3.8-flash-low") == 2,
            "Same-named models must be discovered independently for both providers.");
        var explicitModel = Resolve(plugin, config.Default.Model)!;
        Check(explicitModel.Provider == "sg", "An earlier provider with the same model must not steal the sg route.");
        await client.ChatAsync(config.Default, explicitModel, new OpenAiChatRequest { Model = explicitModel.Name, Messages = [new OpenAiMessage { Content = "test" }] });
        Check(handler.LastChatHost == "sg.example" && handler.LastChatModel == "gemini-3.8-flash-low", "Actual completion must use the sg endpoint and raw model ID.");
        handler.ChatFails = true;
        try { await client.ChatAsync(config.Default, explicitModel, new OpenAiChatRequest { Model = explicitModel.Name }); }
        catch (Exception) { }
        Check(Resolve(plugin, config.Default.Model)?.Provider == "sg", "An upstream completion failure must not invalidate model discovery.");
        handler.DiscoveryFails = true;
        var reloaded = new PluginConfig { Providers = config.Providers }; reloaded.Default.Model = config.Default.Model;
        Set(plugin, "_config", reloaded); await Fetch(plugin);
        Check(reloaded.Models.Count == 4 && Resolve(plugin, reloaded.Default.Model)?.Provider == "sg", "Reload during /models failure must restore the last successful catalog.");
        var manual = new ModelEntry { Name = "gemini-3.8-flash-low", Provider = "sg", SupportsVision = false, DisplayName = "manual" };
        var custom = new PluginConfig { Providers = [Provider("sg")], Models = [manual] };
        Set(plugin, "_config", custom); await Fetch(plugin);
        Check(custom.Models.Single(m => m.Name == manual.Name) == manual && !Resolve(plugin, "sg-gemini-3.8-flash-low")!.SupportsVision,
            "Restoring discovery must preserve manual overrides for that provider.");
        var filteredProvider = Provider("sg"); filteredProvider.ModelFilter = ["other"];
        var filtered = new PluginConfig { Providers = [filteredProvider] }; Set(plugin, "_config", filtered); await Fetch(plugin);
        Check(filtered.Models.All(m => m.Name.StartsWith("other")) && Resolve(plugin, "sg-gemini-3.8-flash-low") is null,
            "A changed filter must also apply to restored and explicit provider models.");
        var changed = Provider("sg"); changed.ApiKey = "changed-fixture";
        var newCredentials = new PluginConfig { Providers = [changed] }; Set(plugin, "_config", newCredentials); await Fetch(plugin);
        Check(newCredentials.Models.Count == 0, "Catalogs must not leak across changed provider credentials.");
        var longest = new PluginConfig { Providers = [Provider("sg"), Provider("sg-long")] }; Set(plugin, "_config", longest);
        Check(Resolve(plugin, "sg-long-model-x") is { Provider: "sg-long", Name: "model-x" }, "The longest provider prefix must win.");
        Check(Resolve(plugin, "unknown-model-x") is null, "Unknown providers must not be invented.");
        Console.WriteLine("PASS: cold starts, duplicate provider models, real request routing, completion/discovery failures, config reload, manual overrides, filters and credential isolation");
    }
    private sealed class Handler : HttpMessageHandler
    {
        public bool DiscoveryFails, ChatFails;
        public string? LastChatHost, LastChatModel;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get)
                return DiscoveryFails ? new(HttpStatusCode.ServiceUnavailable) : new(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"id\":\"gemini-3.8-flash-low\"},{\"id\":\"other-model\"}]}", Encoding.UTF8, "application/json") };
            LastChatHost = request.RequestUri!.Host;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); LastChatModel = body.RootElement.GetProperty("model").GetString();
            if (ChatFails) return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{\"error\":{\"message\":\"fixture failure\"}}", Encoding.UTF8, "application/json") };
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}", Encoding.UTF8, "application/json") };
        }
    }
}
