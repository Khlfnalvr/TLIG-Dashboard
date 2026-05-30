using System.IO;
using System.Text.Json;

namespace TLIGDashboard.Services;

public class AppSettings
{
    // OPC UA server connection settings
    public string OpcUaEndpointUrl        { get; set; } = "opc.tcp://localhost:4840";
    public string OpcUaSecurityMode       { get; set; } = "None";   // None | Sign | SignAndEncrypt
    public bool   OpcUaUseAnonymous       { get; set; } = true;
    public string OpcUaUsername           { get; set; } = "";
    public int    OpcUaPublishingIntervalMs { get; set; } = 1000;
    public OpcUaNodeConfig OpcUaNodeConfig { get; set; } = new();

    // ── Sharing: server side ──────────────────────────────────────────────
    // The server broadcasts its camera + HMI screen and proxies AI chat.
    public int    SharePort   { get; set; } = 8088;   // TCP port the share server listens on
    public string ShareToken  { get; set; } = "";     // access token clients must present (auto-generated if empty)
    public bool   ShareCamera { get; set; } = true;    // broadcast the live camera
    public bool   ShareHmi    { get; set; } = true;    // broadcast the HMI screen share
    public bool   ShareAutoStart { get; set; } = false; // start the server automatically on launch

    // ── Sharing: client side ──────────────────────────────────────────────
    // The client connects to a server to receive its stream + AI proxy.
    public string ServerHost  { get; set; } = "";      // e.g. "192.168.1.10:8088"
    public string ServerToken { get; set; } = "";      // token issued by the server

    // AI service settings
    public string AiApiUrl       { get; set; } = "https://api.deepseek.com";
    public string AiApiKey       { get; set; } = "";
    public string AiModel        { get; set; } = "deepseek-v4-flash";
    public string AiSystemPrompt { get; set; } = "You are a helpful assistant integrated in TLIG Dashboard, an industrial HMI dashboard and AI connector.";

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

        return settings;
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
