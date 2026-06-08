using System.IO;
using System.Linq;
using System.Text.Json;

namespace TLIGDashboard.Services;

/// <summary>
/// Per-install state for one AI provider (see <see cref="AiProviders"/> for the
/// compile-time metadata). The <see cref="ApiKey"/> only ever lives on the machine
/// that owns the provider account — the server. Clients receive the enabled flag
/// and model list but never the key.
/// </summary>
public sealed class AiProviderSettings
{
    public string       Id      { get; set; } = "";   // matches AiProviderInfo.Id
    public string       ApiKey  { get; set; } = "";   // provider secret (server-side only)
    public bool         Enabled { get; set; }         // offered to users when true
    public List<string> Models  { get; set; } = new(); // model ids surfaced in the pickers
}

public class AppSettings
{
    // OPC protocol selection: "UA" | "DA"
    public string OpcProtocol             { get; set; } = "UA";

    // OPC UA server connection settings
    public string OpcUaEndpointUrl        { get; set; } = "opc.tcp://localhost:4840";
    public string OpcUaSecurityMode       { get; set; } = "None";   // None | Sign | SignAndEncrypt
    public bool   OpcUaUseAnonymous       { get; set; } = true;
    public string OpcUaUsername           { get; set; } = "";
    public int    OpcUaPublishingIntervalMs { get; set; } = 1000;
    public OpcUaNodeConfig OpcUaNodeConfig { get; set; } = new();

    // OPC DA server connection settings
    public string OpcDaProgId             { get; set; } = "";

    // ── Sharing: server side ──────────────────────────────────────────────
    // The server broadcasts its camera + HMI screen and proxies AI chat.
    public int    SharePort   { get; set; } = 8088;   // TCP port the share server listens on
    public string ShareToken  { get; set; } = "";     // access token clients must present (auto-generated if empty)
    public bool   ShareCamera { get; set; } = true;    // broadcast the live camera
    public bool   ShareHmi    { get; set; } = true;    // broadcast the HMI screen share
    public bool   ShareAutoStart { get; set; } = false; // start the server automatically on launch

    // ── Sharing: Cloudflare named tunnel (custom domain) ──────────────────
    // When enabled, the tunnel runs on the user's own domain (a fixed public URL)
    // instead of a random *.trycloudflare.com address. Requires a one-time
    // `cloudflared tunnel login` (browser sign-in) that authorizes a Cloudflare zone.
    public bool   TunnelUseCustomDomain { get; set; } = false;
    public string TunnelCustomDomain    { get; set; } = "";              // e.g. "tlig.example.com"
    public string TunnelName            { get; set; } = "tlig-dashboard"; // cloudflared tunnel name

    // ── Sharing: client side ──────────────────────────────────────────────
    // The client logs in with credentials; the server issues a session token that
    // is used for the WebSocket stream + AI proxy (no manual access token anymore).
    public string ServerHost     { get; set; } = "";   // e.g. "192.168.1.10:8088"
    public string ServerUsername { get; set; } = "";   // last username, used to prefill the login popup
    public string ServerToken    { get; set; } = "";   // session token issued by the server on login (transient)

    // ── AI service settings ───────────────────────────────────────────────
    // Legacy single-provider fields. Kept so older settings.json files migrate
    // cleanly into AiProviderConfigs below; no longer the source of truth.
    public string AiApiUrl       { get; set; } = "https://api.deepseek.com";
    public string AiApiKey       { get; set; } = "";
    public string AiModel        { get; set; } = "deepseek-chat";
    public string AiSystemPrompt { get; set; } = "You are a helpful assistant integrated in TLIG Dashboard, an industrial HMI dashboard and AI connector.";

    // Multi-provider config (DeepSeek / OpenAI / Anthropic). On the server each
    // entry holds the real key; on the client the keys stay blank and only the
    // enabled flag + model list are populated from the server's /ai/config.
    public List<AiProviderSettings> AiProviderConfigs { get; set; } = new();

    // The provider + model actually used for chat. On the client this is the
    // user's pick from the enabled set; on the server it is the model the
    // server's own AI page chats with and the proxy's fallback.
    public string AiActiveProvider { get; set; } = AiProviders.DeepSeek;
    public string AiActiveModel    { get; set; } = "";

    // Display units selected from the title-bar customize menu.
    public string TemperatureUnit         { get; set; } = "C";
    public string VoltageUnit             { get; set; } = "V";
    public string CapacityUnit            { get; set; } = "mAh";

    // Navigation visibility — driven by the customize menu in the pane header.
    public bool   ShowNav_Dashboard      { get; set; } = true;
    public bool   ShowNav_CellView       { get; set; } = true;
    public bool   ShowNav_ControlPanel   { get; set; } = true;
    public bool   ShowNav_Logging        { get; set; } = true;
    public bool   ShowNav_Playback       { get; set; } = true;
    public bool   ShowNav_Parameter      { get; set; } = true;
    public bool   ShowNav_LiveView       { get; set; } = true;
    public bool   ShowNav_LearningAnalytic { get; set; } = true;
    public bool   ShowNav_AI             { get; set; } = true;

    // UI zoom level (1.0 = 100%). Adjusted via the Zoom menu / Ctrl +/-/0.
    public double ZoomLevel              { get; set; } = 1.0;

    // UI language — persisted here so it uses the same proven save/load path
    // as every other setting. One of: "id" | "ms" | "en" | "nl" | "zh".
    public string Language               { get; set; } = "en";

    // ── Developer options ─────────────────────────────────────────────────
    // When true, the update checker uses /releases (all releases including
    // prereleases) instead of /releases/latest (stable only).
    public bool   EarlyAccess            { get; set; } = false;
}

public static class AppSettingsService
{
    private static readonly string[] _supportedLanguages = ["id", "en"];
    private static readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TLIGDashboard", "settings.json");
    private static readonly string _legacyLanguagePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TLIGDashboard", "language.txt");

    public static string FilePath   => _path;
    public static string FolderPath => Path.GetDirectoryName(_path)!;
    public static string LanguageFilePath => _legacyLanguagePath;

    public static AppSettings Load()
    {
        AppSettings settings = new();
        bool loadedSettingsFile = false;

        try
        {
            if (File.Exists(_path))
            {
                settings = JsonSerializer.Deserialize(File.ReadAllText(_path), AppJsonContext.Default.AppSettings) ?? new();
                loadedSettingsFile = true;

                // Migration: fix old incorrect URL that had /v1 appended.
                // DeepSeek endpoint is https://api.deepseek.com/chat/completions
                // (no /v1 in the base URL).
                if (settings.AiApiUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                {
                    settings.AiApiUrl = settings.AiApiUrl[..^3]; // strip trailing /v1
                    Save(settings);
                }
            }
        }
        catch { }

        var persistedLanguage = LoadLanguageFromFiles(settings.Language);
        if (settings.Language != persistedLanguage)
        {
            settings.Language = persistedLanguage;
            if (loadedSettingsFile || File.Exists(_legacyLanguagePath))
                Save(settings);
        }

        // Seed / migrate the multi-provider AI config. Persist only when something
        // actually changed and we already have a settings file to update.
        if (EnsureAiProviders(settings) && loadedSettingsFile)
            Save(settings);

        return settings;
    }

    /// <summary>
    /// Guarantees <see cref="AppSettings.AiProviderConfigs"/> has an entry for every
    /// provider in the registry and migrates the legacy single-provider fields into
    /// the matching provider on first run. Returns true when it mutated settings.
    /// </summary>
    private static bool EnsureAiProviders(AppSettings s)
    {
        bool changed = false;

        if (s.AiProviderConfigs.Count == 0)
        {
            // First run (or pre-multi-provider settings.json): seed from the registry.
            foreach (var p in AiProviders.All)
                s.AiProviderConfigs.Add(new AiProviderSettings { Id = p.Id, Models = p.Models.ToList() });

            // Carry the old single-provider config into whichever provider its URL matched.
            var legacyInfo = AiProviders.All.FirstOrDefault(p =>
                s.AiApiUrl.TrimEnd('/').StartsWith(p.BaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
            var legacyId = legacyInfo?.Id ?? AiProviders.DeepSeek;
            var legacy   = s.AiProviderConfigs.First(c => c.Id == legacyId);

            if (!string.IsNullOrWhiteSpace(s.AiApiKey))
            {
                legacy.ApiKey  = s.AiApiKey;
                legacy.Enabled = true;
            }
            if (!string.IsNullOrWhiteSpace(s.AiModel) && !legacy.Models.Contains(s.AiModel))
                legacy.Models.Insert(0, s.AiModel);

            s.AiActiveProvider = legacyId;
            s.AiActiveModel    = !string.IsNullOrWhiteSpace(s.AiModel)
                ? s.AiModel
                : (legacy.Models.FirstOrDefault() ?? "");
            changed = true;
        }
        else
        {
            // Add providers introduced in a later app version.
            foreach (var p in AiProviders.All)
            {
                if (!s.AiProviderConfigs.Any(c => c.Id == p.Id))
                {
                    s.AiProviderConfigs.Add(new AiProviderSettings { Id = p.Id, Models = p.Models.ToList() });
                    changed = true;
                }
            }
        }

        if (!AiProviders.IsValid(s.AiActiveProvider))
        {
            s.AiActiveProvider = AiProviders.DeepSeek;
            changed = true;
        }

        return changed;
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            settings.Language = NormalizeLanguage(settings.Language);
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, AppJsonContext.Default.AppSettings));
            File.WriteAllText(_legacyLanguagePath, settings.Language);
        }
        catch { }
    }

    public static string LoadLanguage() => Load().Language;

    public static void SaveLanguage(string language)
    {
        var settings = Load();
        settings.Language = NormalizeLanguage(language);
        Save(settings);
    }

    private static string LoadLanguageFromFiles(string settingsLanguage)
    {
        var normalizedSettingsLanguage = NormalizeLanguage(settingsLanguage);
        if (normalizedSettingsLanguage != "en")
            return normalizedSettingsLanguage;

        try
        {
            if (File.Exists(_legacyLanguagePath))
            {
                var legacyLanguage = NormalizeLanguage(File.ReadAllText(_legacyLanguagePath));
                if (legacyLanguage != "en")
                    return legacyLanguage;
            }
        }
        catch { }

        return normalizedSettingsLanguage;
    }

    private static string NormalizeLanguage(string? language)
    {
        var code = (language ?? "").Trim();
        return Array.IndexOf(_supportedLanguages, code) >= 0 ? code : "en";
    }
}
