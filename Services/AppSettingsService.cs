using System.IO;
using System.Text.Json;

namespace TLIGDashboard.Services;

public class AppSettings
{
    // BmsConfig thresholds
    public double NominalCapacityAh      { get; set; } = 20.0;
    public double MaxDod                 { get; set; } = 80;
    public double MaxChargeCurrent       { get; set; } = 20;
    public double MaxDischargeCurrent    { get; set; } = 40;
    public double OvervoltageThreshold   { get; set; } = 4.20;
    public double HighVoltageWarning     { get; set; } = 4.10;
    public double UndervoltageThreshold  { get; set; } = 2.80;
    public double LowVoltageWarning      { get; set; } = 3.00;
    public double OverTempWarning        { get; set; } = 60;
    public double OverTempCutoff         { get; set; } = 70;
    public double BalancingStartDeltaMv  { get; set; } = 20;
    public double BalancingStopDeltaMv   { get; set; } = 5;

    // UART baud rate to the ESP32 master.
    public int    SerialBaud             { get; set; } = 115200;
    public int    ReconnectIntervalSec   { get; set; } = 2;
    public int    ProbeTimeoutMs         { get; set; } = 3000;
    public bool   AutoConnectEnabled     { get; set; } = true;

    // Last paired BLE device id — restored into the Bluetooth dropdown so the
    // user can reconnect to "their" pack with one click after relaunch.
    public string LastBluetoothDeviceId   { get; set; } = "";
    public string LastBluetoothDeviceName { get; set; } = "";

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
    public bool   ShowNav_AI             { get; set; } = true;

    // UI zoom level (1.0 = 100%). Adjusted via the Zoom menu / Ctrl +/-/0.
    public double ZoomLevel              { get; set; } = 1.0;

    // UI language — persisted here so it uses the same proven save/load path
    // as every other setting. One of: "id" | "ms" | "en" | "nl" | "zh".
    public string Language               { get; set; } = "en";
}

public static class AppSettingsService
{
    private static readonly string[] _supportedLanguages = ["id", "ms", "en", "nl", "zh"];
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
