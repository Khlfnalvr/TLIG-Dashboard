using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace TLIGDashboard.Services;

/// <summary>One provider's state as seen by the settings UI (read + edit intent).</summary>
public sealed class AiProviderView
{
    public string       Id        { get; set; } = "";
    public string       Name      { get; set; } = "";
    public string       Protocol  { get; set; } = AiProtocols.OpenAi;
    public bool         Enabled   { get; set; }
    public bool         HasKey    { get; set; }                 // server holds a key (value never sent to clients)
    public List<string> Models    { get; set; } = new();        // models offered to users
    public List<string> AllModels { get; set; } = new();        // full registry catalogue (for the staff toggles)

    // ── Edit intent (filled by the staff config UI before SaveAsync) ──────────
    public string? NewApiKey { get; set; }    // non-blank → set/replace the key; null/blank → keep
    public bool    ClearKey  { get; set; }    // true → wipe the stored key
}

/// <summary>Snapshot of the whole AI configuration for the UI.</summary>
public sealed class AiConfigResult
{
    public List<AiProviderView> Providers      { get; } = new();
    public bool                 CanEdit        { get; set; }
    public string               ActiveProvider { get; set; } = AiProviders.DeepSeek;
    public string               ActiveModel    { get; set; } = "";
    public string               SystemPrompt   { get; set; } = "";
    public bool                 Ok             { get; set; }

    public AiProviderView? Provider(string id) => Providers.FirstOrDefault(p => p.Id == id);

    /// <summary>Providers a non-staff user may pick from (enabled and key present).</summary>
    public IEnumerable<AiProviderView> Usable => Providers.Where(p => p.Enabled && p.HasKey);
}

/// <summary>
/// Client-side helper that talks to a <see cref="ShareServer"/>'s <c>/ai/config</c>
/// endpoint over HTTP (session token = Bearer). Mirrors <see cref="TaskClient"/>.
/// </summary>
public static class AiConfigClient
{
    public static async Task<AiConfigResult> GetAsync(string host, string token)
    {
        var result = new AiConfigResult();
        if (string.IsNullOrWhiteSpace(AuthClient.NormalizeHost(host)) || string.IsNullOrWhiteSpace(token))
            return result;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var req  = new HttpRequestMessage(HttpMethod.Get, $"{AuthClient.BaseUrl(host)}{ShareProtocol.AiConfigPath}");
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

            using var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return result;

            var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync());
            ParseInto(node, result);
            result.Ok = true;
        }
        catch { /* unreachable / malformed → empty result, Ok stays false */ }
        return result;
    }

    public static async Task<bool> SaveAsync(string host, string token, AiConfigResult cfg)
    {
        if (string.IsNullOrWhiteSpace(AuthClient.NormalizeHost(host)) || string.IsNullOrWhiteSpace(token))
            return false;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var req  = new HttpRequestMessage(HttpMethod.Post, $"{AuthClient.BaseUrl(host)}{ShareProtocol.AiConfigPath}")
            {
                Content = new StringContent(BuildSaveBody(cfg).ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            using var resp = await http.SendAsync(req);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    // ── Shared (re)used by the Server flavor too ──────────────────────────────

    internal static void ParseInto(JsonNode? node, AiConfigResult result)
    {
        result.CanEdit        = (bool?)node?["canEdit"]        ?? false;
        result.ActiveProvider = (string?)node?["activeProvider"] ?? AiProviders.DeepSeek;
        result.ActiveModel    = (string?)node?["activeModel"]    ?? "";
        result.SystemPrompt   = (string?)node?["systemPrompt"]   ?? "";

        if (node?["providers"] is JsonArray arr)
        {
            foreach (var p in arr)
            {
                if (p is null) continue;
                var view = new AiProviderView
                {
                    Id       = (string?)p["id"]       ?? "",
                    Name     = (string?)p["name"]     ?? "",
                    Protocol = (string?)p["protocol"] ?? AiProtocols.OpenAi,
                    Enabled  = (bool?)p["enabled"]    ?? false,
                    HasKey   = (bool?)p["hasKey"]     ?? false,
                };
                if (p["models"]    is JsonArray ms) foreach (var m in ms) view.Models.Add((string?)m ?? "");
                if (p["allModels"] is JsonArray am) foreach (var m in am) view.AllModels.Add((string?)m ?? "");
                result.Providers.Add(view);
            }
        }
    }

    internal static JsonObject BuildSaveBody(AiConfigResult cfg)
    {
        var provs = new JsonArray();
        foreach (var p in cfg.Providers)
        {
            var models = new JsonArray();
            foreach (var m in p.Models) models.Add(m);

            var obj = new JsonObject
            {
                ["id"]      = p.Id,
                ["enabled"] = p.Enabled,
                ["models"]  = models,
            };
            if (p.ClearKey) obj["clearKey"] = true;
            else if (!string.IsNullOrWhiteSpace(p.NewApiKey)) obj["apiKey"] = p.NewApiKey;
            provs.Add(obj);
        }

        return new JsonObject
        {
            ["providers"]      = provs,
            ["systemPrompt"]   = cfg.SystemPrompt,
            ["activeProvider"] = cfg.ActiveProvider,
            ["activeModel"]    = cfg.ActiveModel,
        };
    }
}

/// <summary>
/// Single integration point the UI uses for AI provider configuration, hiding the
/// server-vs-client split (mirrors <see cref="LearningTaskService"/>). On the
/// <b>Server</b> flavor it reads/writes the local <see cref="AppSettings"/>; on the
/// <b>Client</b> flavor it goes over HTTP to the signed-in server. Editing is
/// staff-only — enforced here and authoritatively on the server.
/// </summary>
public static class AiConfigService
{
    /// <summary>
    /// Points the shared <see cref="AiService"/> at the active provider/model from
    /// settings. <b>Server</b> talks to the provider directly (its key + protocol);
    /// <b>Client</b> routes through the server's <c>/ai</c> proxy (always OpenAI) and
    /// passes the chosen provider as a hint. Shared by the AI page and the Dashboard chat.
    /// </summary>
    public static void ApplyActive(AiService ai)
    {
        var s = AppSettingsService.Load();
        ai.SystemPrompt = s.AiSystemPrompt;

        if (BuildInfo.IsServer)
        {
            var info = AiProviders.Resolve(s.AiActiveProvider);
            var pcfg = s.AiProviderConfigs.FirstOrDefault(c => c.Id == info.Id);
            ai.Protocol   = info.Protocol;
            ai.ApiUrl     = info.BaseUrl;
            ai.ApiKey     = pcfg?.ApiKey ?? "";
            ai.ProviderId = "";
            ai.Model      = !string.IsNullOrWhiteSpace(s.AiActiveModel)
                ? s.AiActiveModel
                : (pcfg?.Models.FirstOrDefault() ?? info.Models.FirstOrDefault() ?? "");
        }
        else
        {
            var baseUrl = AuthClient.BaseUrl(s.ServerHost);
            ai.Protocol   = AiProtocols.OpenAi;
            ai.ApiUrl     = string.IsNullOrWhiteSpace(AuthClient.NormalizeHost(s.ServerHost))
                ? "" : $"{baseUrl}/ai";
            ai.ApiKey     = s.ServerToken;
            ai.ProviderId = s.AiActiveProvider;
            ai.Model      = !string.IsNullOrWhiteSpace(s.AiActiveModel) ? s.AiActiveModel : "(server)";
        }
    }

    public static async Task<AiConfigResult> LoadAsync()
    {
        if (BuildInfo.IsServer)
            return LoadLocal();

        var s = AppSettingsService.Load();
        return await AiConfigClient.GetAsync(s.ServerHost, s.ServerToken);
    }

    public static async Task<bool> SaveAsync(AiConfigResult cfg)
    {
        if (!SessionService.Instance.IsStaff) return false;

        if (BuildInfo.IsServer)
        {
            SaveLocal(cfg);
            return true;
        }

        var s = AppSettingsService.Load();
        return await AiConfigClient.SaveAsync(s.ServerHost, s.ServerToken, cfg);
    }

    // ── Server flavor: straight from local settings ───────────────────────────

    private static AiConfigResult LoadLocal()
    {
        var s      = AppSettingsService.Load();
        var result = new AiConfigResult
        {
            CanEdit        = SessionService.Instance.IsStaff,
            ActiveProvider = s.AiActiveProvider,
            ActiveModel    = s.AiActiveModel,
            SystemPrompt   = s.AiSystemPrompt,
            Ok             = true,
        };

        foreach (var info in AiProviders.All)
        {
            var cfg  = s.AiProviderConfigs.FirstOrDefault(c => c.Id == info.Id);
            var view = new AiProviderView
            {
                Id       = info.Id,
                Name     = info.Name,
                Protocol = info.Protocol,
                Enabled  = cfg?.Enabled ?? false,
                HasKey   = !string.IsNullOrWhiteSpace(cfg?.ApiKey),
                Models   = cfg?.Models is { } ml ? new List<string>(ml) : new List<string>(),
                AllModels = new List<string>(info.Models),
            };
            result.Providers.Add(view);
        }
        return result;
    }

    private static void SaveLocal(AiConfigResult cfg)
    {
        var s = AppSettingsService.Load();

        foreach (var view in cfg.Providers)
        {
            var info = AiProviders.Find(view.Id);
            if (info is null) continue;

            var entry = s.AiProviderConfigs.FirstOrDefault(c => c.Id == view.Id);
            if (entry is null)
            {
                entry = new AiProviderSettings { Id = view.Id };
                s.AiProviderConfigs.Add(entry);
            }

            entry.Enabled = view.Enabled;
            entry.Models  = view.Models.Where(m => info.Models.Contains(m)).Distinct().ToList();

            if (view.ClearKey) entry.ApiKey = "";
            else if (!string.IsNullOrWhiteSpace(view.NewApiKey)) entry.ApiKey = view.NewApiKey!.Trim();
        }

        if (!string.IsNullOrWhiteSpace(cfg.SystemPrompt)) s.AiSystemPrompt = cfg.SystemPrompt;
        if (AiProviders.IsValid(cfg.ActiveProvider))      s.AiActiveProvider = cfg.ActiveProvider;
        if (!string.IsNullOrWhiteSpace(cfg.ActiveModel))  s.AiActiveModel = cfg.ActiveModel;

        AppSettingsService.Save(s);
    }
}
