using System.ComponentModel;
using System.Globalization;

namespace TLIGDashboard.Services;

/// <summary>
/// Singleton localization manager. Changing <see cref="CurrentLanguage"/> raises
/// PropertyChanged("") so every {x:Bind Lang.XYZ, Mode=OneWay} binding refreshes.
/// Supported: id · en
/// </summary>
public sealed class LocalizationManager : INotifyPropertyChanged
{
    public static readonly string[] SupportedLanguages = ["id", "en"];
    public static readonly string[] LanguageLabels     = ["Indonesia", "English"];

    // ── Singleton ─────────────────────────────────────────────────────────
    public static LocalizationManager Instance { get; } = new();
    private LocalizationManager() { _currentLang = LoadSavedOrDefault(); }

    // ── INotifyPropertyChanged ────────────────────────────────────────────
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    // ── Language state ────────────────────────────────────────────────────
    private string _currentLang;

    public string CurrentLanguage
    {
        get => _currentLang;
        set
        {
            var language = NormalizeLanguage(value);
            if (_currentLang == language) return;
            _currentLang = language;
            Save(language);
            Notify();
        }
    }

    // ── Persistence ───────────────────────────────────────────────────────
    // Language is stored inside AppSettingsService's settings.json so it
    // benefits from the same atomic save/load path as every other setting.

    private static string LoadSavedOrDefault()
    {
        try
        {
            var saved = AppSettingsService.LoadLanguage();
            if (Array.IndexOf(SupportedLanguages, saved) >= 0) return saved;
        }
        catch { /* ignore */ }

        return "en";
    }

    private static void Save(string lang)
    {
        try
        {
            AppSettingsService.SaveLanguage(lang);
        }
        catch { /* ignore */ }
    }

    private static string NormalizeLanguage(string? lang)
    {
        var code = (lang ?? "").Trim();
        return Array.IndexOf(SupportedLanguages, code) >= 0 ? code : "en";
    }

    // ── String lookup ─────────────────────────────────────────────────────
    private string T(string key)
    {
        if (_strings.TryGetValue(_currentLang, out var d) && d.TryGetValue(key, out var v)) return v;
        if (_extraStrings.TryGetValue(_currentLang, out var x) && x.TryGetValue(key, out var xv)) return xv;
        if (_strings.TryGetValue("en",         out var e) && e.TryGetValue(key, out var f)) return f;
        if (_extraStrings.TryGetValue("en",    out var xe) && xe.TryGetValue(key, out var xf)) return xf;
        return key;
    }

    public string Get(string key) => T(key);

    public string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(key), args);

    // ── Navigation ────────────────────────────────────────────────────────
    public string Nav_Dashboard    => T(nameof(Nav_Dashboard));
    public string Nav_CellView     => T(nameof(Nav_CellView));
    public string Nav_ControlPanel => T(nameof(Nav_ControlPanel));
    public string Nav_Logging      => T(nameof(Nav_Logging));
    public string Nav_Playback     => T(nameof(Nav_Playback));
    public string Nav_Parameter    => T(nameof(Nav_Parameter));
    public string Nav_LiveView     => T(nameof(Nav_LiveView));
    public string Nav_AI           => T(nameof(Nav_AI));

    // ── Theme toggle ──────────────────────────────────────────────────────
    public string Ui_Dark           => T(nameof(Ui_Dark));
    public string Ui_Light          => T(nameof(Ui_Light));
    public string Ui_SwitchToLight  => T(nameof(Ui_SwitchToLight));
    public string Ui_SwitchToDark   => T(nameof(Ui_SwitchToDark));
    public string Ui_ChangeLanguage => T(nameof(Ui_ChangeLanguage));

    // ── Caption-bar OPC UA picker ─────────────────────────────────────────
    public string Ui_OpcUaConnection  => T(nameof(Ui_OpcUaConnection));
    public string Ui_OpcUaEndpoint    => T(nameof(Ui_OpcUaEndpoint));
    public string Ui_OpcUaSecurity    => T(nameof(Ui_OpcUaSecurity));
    public string Ui_OpcUaAuth        => T(nameof(Ui_OpcUaAuth));
    public string Ui_OpcUaAnonymous   => T(nameof(Ui_OpcUaAnonymous));
    public string Ui_OpcUaUsernameAuth=> T(nameof(Ui_OpcUaUsernameAuth));
    public string Ui_OpcUaUsername    => T(nameof(Ui_OpcUaUsername));
    public string Ui_OpcUaPassword    => T(nameof(Ui_OpcUaPassword));
    public string Ui_OpcUaSecNone     => T(nameof(Ui_OpcUaSecNone));
    public string Ui_OpcUaSecSign     => T(nameof(Ui_OpcUaSecSign));
    public string Ui_OpcUaSecSignEnc  => T(nameof(Ui_OpcUaSecSignEnc));

    // ── Alert history ─────────────────────────────────────────────────────
    public string Ui_AlertHistory   => T(nameof(Ui_AlertHistory));
    public string Ui_NoAlerts       => T(nameof(Ui_NoAlerts));
    public string Ui_ClearAlerts    => T(nameof(Ui_ClearAlerts));

    // ── Logo / customize menu ─────────────────────────────────────────────
    public string Ui_Menu_Customize  => T(nameof(Ui_Menu_Customize));
    public string Ui_Menu_View       => T(nameof(Ui_Menu_View));
    public string Ui_Menu_Unit       => T(nameof(Ui_Menu_Unit));
    public string Ui_Menu_Temperature => T(nameof(Ui_Menu_Temperature));
    public string Ui_Menu_Voltage    => T(nameof(Ui_Menu_Voltage));
    public string Ui_Menu_Capacity   => T(nameof(Ui_Menu_Capacity));
    public string Ui_Menu_About      => T(nameof(Ui_Menu_About));
    public string Ui_About_Product   => T(nameof(Ui_About_Product));
    public string Ui_About_Version   => T(nameof(Ui_About_Version));
    public string Ui_About_License   => T(nameof(Ui_About_License));
    public string Ui_About_Copyright => T(nameof(Ui_About_Copyright));
    public string Ui_Menu_Tour       => T(nameof(Ui_Menu_Tour));
    public string Ui_Menu_RefreshApp => T(nameof(Ui_Menu_RefreshApp));
    public string Ui_Menu_Zoom       => T(nameof(Ui_Menu_Zoom));
    public string Ui_Menu_ActualSize => T(nameof(Ui_Menu_ActualSize));
    public string Ui_Menu_ZoomIn     => T(nameof(Ui_Menu_ZoomIn));
    public string Ui_Menu_ZoomOut    => T(nameof(Ui_Menu_ZoomOut));
    public string Ui_Menu_Developer     => T(nameof(Ui_Menu_Developer));
    public string Ui_Menu_OpenLogFolder => T(nameof(Ui_Menu_OpenLogFolder));
    public string Ui_Menu_OpenSettings  => T(nameof(Ui_Menu_OpenSettings));
    public string Ui_Menu_ReportBug     => T(nameof(Ui_Menu_ReportBug));
    public string Ui_Menu_CheckUpdate   => T(nameof(Ui_Menu_CheckUpdate));

    // ── Update dialog ─────────────────────────────────────────────────────
    public string Upd_CheckingTitle  => T(nameof(Upd_CheckingTitle));
    public string Upd_UpToDateTitle  => T(nameof(Upd_UpToDateTitle));
    public string Upd_UpToDateMsg    => T(nameof(Upd_UpToDateMsg));
    public string Upd_AvailableTitle => T(nameof(Upd_AvailableTitle));
    public string Upd_CurrentVersion => T(nameof(Upd_CurrentVersion));
    public string Upd_LatestVersion  => T(nameof(Upd_LatestVersion));
    public string Upd_ReleaseNotes   => T(nameof(Upd_ReleaseNotes));
    public string Upd_Download       => T(nameof(Upd_Download));
    public string Upd_OpenPage       => T(nameof(Upd_OpenPage));
    public string Upd_Later          => T(nameof(Upd_Later));
    public string Upd_Close          => T(nameof(Upd_Close));
    public string Upd_ErrorTitle     => T(nameof(Upd_ErrorTitle));
    public string Upd_Downloading    => T(nameof(Upd_Downloading));
    public string Upd_InstallNow     => T(nameof(Upd_InstallNow));
    public string Upd_Extracting     => T(nameof(Upd_Extracting));

    // ── Common ────────────────────────────────────────────────────────────
    public string Com_Min    => T(nameof(Com_Min));
    public string Com_Max    => T(nameof(Com_Max));
    public string Com_Avg    => T(nameof(Com_Avg));
    public string Com_Delta  => T(nameof(Com_Delta));
    public string Com_Status => T(nameof(Com_Status));

    // ── Dashboard ─────────────────────────────────────────────────────────
    public string Dash_SecPackOverview  => T(nameof(Dash_SecPackOverview));
    public string Dash_PackVoltage      => T(nameof(Dash_PackVoltage));
    public string Dash_PackNominal      => T(nameof(Dash_PackNominal));
    public string Dash_StateOfCharge    => T(nameof(Dash_StateOfCharge));
    public string Dash_Remaining        => T(nameof(Dash_Remaining));
    public string Dash_RemainingSub     => T(nameof(Dash_RemainingSub));
    public string Dash_SubToEmpty       => T(nameof(Dash_SubToEmpty));
    public string Dash_SubToFull        => T(nameof(Dash_SubToFull));
    public string Dash_SubIdle          => T(nameof(Dash_SubIdle));
    public string Dash_Current          => T(nameof(Dash_Current));
    public string Dash_CurrentSub       => T(nameof(Dash_CurrentSub));
    public string Dash_PackConfig       => T(nameof(Dash_PackConfig));
    public string Dash_SecSocHistory    => T(nameof(Dash_SecSocHistory));
    public string Dash_TimeAgo          => T(nameof(Dash_TimeAgo));
    public string Dash_Now              => T(nameof(Dash_Now));
    public string Dash_NowArrow         => T(nameof(Dash_NowArrow));
    public string Dash_SecViHistory     => T(nameof(Dash_SecViHistory));
    public string Dash_SaveChart        => T(nameof(Dash_SaveChart));
    public string Dash_SecTempHistory   => T(nameof(Dash_SecTempHistory));
    public string Dash_TempC            => T(nameof(Dash_TempC));
    public string Dash_TempF            => T(nameof(Dash_TempF));
    public string Dash_VoltageV         => T(nameof(Dash_VoltageV));
    public string Dash_CurrentA         => T(nameof(Dash_CurrentA));
    public string Dash_SecCellSummary   => T(nameof(Dash_SecCellSummary));
    public string Dash_TempSensors      => T(nameof(Dash_TempSensors));
    public string Dash_ActiveCells      => T(nameof(Dash_ActiveCells));
    public string Dash_CellDelta        => T(nameof(Dash_CellDelta));
    public string Dash_Method           => T(nameof(Dash_Method));
    public string Dash_ActiveMethod     => T(nameof(Dash_ActiveMethod));
    public string Dash_SecAlertsWarnings => T(nameof(Dash_SecAlertsWarnings));

    // ── Cell statistics ───────────────────────────────────────────────────
    public string Cell_ResetStats        => T(nameof(Cell_ResetStats));

    // ── Cell View ─────────────────────────────────────────────────────────
    public string Cell_SecVoltageSummary => T(nameof(Cell_SecVoltageSummary));
    public string Cell_SecCellGrid       => T(nameof(Cell_SecCellGrid));
    public string Cell_DeltaLabel        => T(nameof(Cell_DeltaLabel));
    public string Cell_Normal            => T(nameof(Cell_Normal));
    public string Cell_Low               => T(nameof(Cell_Low));
    public string Cell_Undervoltage      => T(nameof(Cell_Undervoltage));
    public string Cell_Overvoltage       => T(nameof(Cell_Overvoltage));
    public string Cell_Balancing         => T(nameof(Cell_Balancing));
    public string Cell_SecNtcReadings    => T(nameof(Cell_SecNtcReadings));
    public string Cell_Thresholds        => T(nameof(Cell_Thresholds));
    public string Cell_ThreshWarn        => T(nameof(Cell_ThreshWarn));
    public string Cell_ThreshCutoff      => T(nameof(Cell_ThreshCutoff));
    public string Cell_Legend            => T(nameof(Cell_Legend));
    public string Cell_NormalDesc        => T(nameof(Cell_NormalDesc));
    public string Cell_WarnDesc          => T(nameof(Cell_WarnDesc));
    public string Cell_CutoffDesc        => T(nameof(Cell_CutoffDesc));

    // ── Control Panel ─────────────────────────────────────────────────────
    public string Ctrl_PhNoPorts          => T(nameof(Ctrl_PhNoPorts));
    public string Ctrl_Connect            => T(nameof(Ctrl_Connect));
    public string Ctrl_Disconnect         => T(nameof(Ctrl_Disconnect));
    public string Ctrl_ConnStatus         => T(nameof(Ctrl_ConnStatus));
    public string Ctrl_NotConnected       => T(nameof(Ctrl_NotConnected));
    public string Ctrl_OverTempWarn       => T(nameof(Ctrl_OverTempWarn));
    public string Ctrl_OverTempCutoff     => T(nameof(Ctrl_OverTempCutoff));
    public string Ctrl_MaxDod             => T(nameof(Ctrl_MaxDod));
    public string Ctrl_StartDelta         => T(nameof(Ctrl_StartDelta));
    public string Ctrl_StopDelta          => T(nameof(Ctrl_StopDelta));
    public string Ctrl_ResetDefaults      => T(nameof(Ctrl_ResetDefaults));
    public string Ctrl_ApplySettings      => T(nameof(Ctrl_ApplySettings));

    // ── Control Panel — advanced serial parameters ────────────────────────
    public string Ctrl_AutoConnect        => T(nameof(Ctrl_AutoConnect));
    public string Ctrl_AutoConnectHint    => T(nameof(Ctrl_AutoConnectHint));
    public string Ctrl_ReconnectInterval  => T(nameof(Ctrl_ReconnectInterval));
    public string Ctrl_ProbeTimeout       => T(nameof(Ctrl_ProbeTimeout));
    public string Ctrl_FramesReceived     => T(nameof(Ctrl_FramesReceived));
    public string Ctrl_ParseErrors        => T(nameof(Ctrl_ParseErrors));

    // ── Control Panel — Bluetooth ─────────────────────────────────────────
    public string Ctrl_BtDevice           => T(nameof(Ctrl_BtDevice));

    // ── Feedback messages ─────────────────────────────────────────────────
    public string Fb_SerialError       => T(nameof(Fb_SerialError));
    public string Fb_SelectChannel           => T(nameof(Fb_SelectChannel));
    public string Fb_SelectChannelMsg        => T(nameof(Fb_SelectChannelMsg));
    public string Fb_SettingsApplied      => T(nameof(Fb_SettingsApplied));
    public string Fb_SettingsAppliedMsg   => T(nameof(Fb_SettingsAppliedMsg));
    public string Fb_DefaultsRestored     => T(nameof(Fb_DefaultsRestored));
    public string Fb_DefaultsRestoredMsg  => T(nameof(Fb_DefaultsRestoredMsg));

    // ── Logging ───────────────────────────────────────────────────────────
    public string Log_SecStatus          => T(nameof(Log_SecStatus));
    public string Log_State              => T(nameof(Log_State));
    public string Log_Samples            => T(nameof(Log_Samples));
    public string Log_Duration           => T(nameof(Log_Duration));
    public string Log_SecFileSettings    => T(nameof(Log_SecFileSettings));
    public string Log_Folder             => T(nameof(Log_Folder));
    public string Log_Browse             => T(nameof(Log_Browse));
    public string Log_Format             => T(nameof(Log_Format));
    public string Log_Filename           => T(nameof(Log_Filename));
    public string Log_PhAutoFilename     => T(nameof(Log_PhAutoFilename));
    public string Log_SecControls        => T(nameof(Log_SecControls));
    public string Log_ConnectHint        => T(nameof(Log_ConnectHint));
    public string Log_RecordingHint      => T(nameof(Log_RecordingHint));
    public string Log_ReadyHint          => T(nameof(Log_ReadyHint));
    public string Log_StartLogging       => T(nameof(Log_StartLogging));
    public string Log_StopLogging        => T(nameof(Log_StopLogging));
    public string Log_OpenFolder         => T(nameof(Log_OpenFolder));
    public string Log_SecDataFormat      => T(nameof(Log_SecDataFormat));
    public string Log_DataDesc1          => T(nameof(Log_DataDesc1));
    public string Log_DataDesc2          => T(nameof(Log_DataDesc2));
    public string Log_DataDesc3          => T(nameof(Log_DataDesc3));
    public string Log_SecLiveData        => T(nameof(Log_SecLiveData));
    public string Log_HdrTimestamp       => T(nameof(Log_HdrTimestamp));
    public string Log_HdrSoc             => T(nameof(Log_HdrSoc));
    public string Log_HdrPackV           => T(nameof(Log_HdrPackV));
    public string Log_HdrCurrentA        => T(nameof(Log_HdrCurrentA));
    public string Log_HdrStatus          => T(nameof(Log_HdrStatus));
    public string Log_HdrMinCell         => T(nameof(Log_HdrMinCell));
    public string Log_HdrMaxCell         => T(nameof(Log_HdrMaxCell));
    public string Log_HdrDeltaMv         => T(nameof(Log_HdrDeltaMv));
    public string Log_HdrBalCells        => T(nameof(Log_HdrBalCells));
    public string Log_NoData             => T(nameof(Log_NoData));
    public string Log_Idle               => T(nameof(Log_Idle));
    public string Log_Logging            => T(nameof(Log_Logging));

    // ── Playback ──────────────────────────────────────────────────────────
    public string Pb_SecLoadFile    => T(nameof(Pb_SecLoadFile));
    public string Pb_NoFileLoaded   => T(nameof(Pb_NoFileLoaded));
    public string Pb_Browse         => T(nameof(Pb_Browse));
    public string Pb_Unload         => T(nameof(Pb_Unload));
    public string Pb_LoadStatus     => T(nameof(Pb_LoadStatus));
    public string Pb_SecFileInfo    => T(nameof(Pb_SecFileInfo));
    public string Pb_Frames         => T(nameof(Pb_Frames));
    public string Pb_EstDuration    => T(nameof(Pb_EstDuration));
    public string Pb_PlaybackSpeed  => T(nameof(Pb_PlaybackSpeed));
    public string Pb_SecHowToUse    => T(nameof(Pb_SecHowToUse));
    public string Pb_HowToUse1      => T(nameof(Pb_HowToUse1));
    public string Pb_HowToUse2      => T(nameof(Pb_HowToUse2));

    // ── Login dialog ──────────────────────────────────────────────────────
    public string Login_Title        => T(nameof(Login_Title));
    public string Login_Username     => T(nameof(Login_Username));
    public string Login_Password     => T(nameof(Login_Password));
    public string Login_UsernameHint => T(nameof(Login_UsernameHint));
    public string Login_PasswordHint => T(nameof(Login_PasswordHint));
    public string Login_Submit       => T(nameof(Login_Submit));
    public string Login_ErrorEmpty   => T(nameof(Login_ErrorEmpty));
    public string Login_ErrorInvalid => T(nameof(Login_ErrorInvalid));

    // ── Account flyout ────────────────────────────────────────────────────
    public string Account_LoggedInAs => T(nameof(Account_LoggedInAs));
    public string Account_Logout     => T(nameof(Account_Logout));

    // ── System model panel ────────────────────────────────────────────────
    public string Panel_ModelSistem      => T(nameof(Panel_ModelSistem));
    public string Panel_DiagramBlok      => T(nameof(Panel_DiagramBlok));
    public string Panel_ParameterPid     => T(nameof(Panel_ParameterPid));
    public string Panel_ResponSistem     => T(nameof(Panel_ResponSistem));
    public string Panel_RiseTime         => T(nameof(Panel_RiseTime));
    public string Panel_Overshoot        => T(nameof(Panel_Overshoot));
    public string Panel_Settling         => T(nameof(Panel_Settling));
    public string Panel_SteadyErr        => T(nameof(Panel_SteadyErr));
    public string Panel_TransferFunction => T(nameof(Panel_TransferFunction));

    // ── AI chat ───────────────────────────────────────────────────────────
    public string Ai_Title         => T(nameof(Ai_Title));
    public string Ai_UserLabel     => T(nameof(Ai_UserLabel));
    public string Ai_AiLabel       => T(nameof(Ai_AiLabel));
    public string Ai_InputHint     => T(nameof(Ai_InputHint));
    public string Ai_InputHintFull => T(nameof(Ai_InputHintFull));
    public string Ai_Settings      => T(nameof(Ai_Settings));
    public string Ai_ApiUrl        => T(nameof(Ai_ApiUrl));
    public string Ai_ApiKey        => T(nameof(Ai_ApiKey));
    public string Ai_Model         => T(nameof(Ai_Model));
    public string Ai_SystemPrompt  => T(nameof(Ai_SystemPrompt));
    public string Ai_ClearChat     => T(nameof(Ai_ClearChat));
    public string Ai_SaveSettings  => T(nameof(Ai_SaveSettings));
    public string Ai_ErrorNoKey    => T(nameof(Ai_ErrorNoKey));
    public string Ai_Thinking      => T(nameof(Ai_Thinking));
    public string Ai_StopGen       => T(nameof(Ai_StopGen));
    public string Ai_ModelLabel    => T(nameof(Ai_ModelLabel));

    // Supplemental UI text used by code-behind and dynamic controls.
    public string Log_FormatCsv       => T(nameof(Log_FormatCsv));
    public string Log_FormatTsv       => T(nameof(Log_FormatTsv));
    public string Log_FormatExcel     => T(nameof(Log_FormatExcel));
    public string Log_FormatJson      => T(nameof(Log_FormatJson));
    public string Cell_TimeRangeTrim  => T(nameof(Cell_TimeRangeTrim));
    public string Cell_TrimNoData     => T(nameof(Cell_TrimNoData));
    public string Cell_TrimReset      => T(nameof(Cell_TrimReset));
    public string Cell_TrimHint       => T(nameof(Cell_TrimHint));

    // ══════════════════════════════════════════════════════════════════════
    // Translation table
    // ══════════════════════════════════════════════════════════════════════
    private static readonly Dictionary<string, Dictionary<string, string>> _strings = new()
    {
        // ── ENGLISH ──────────────────────────────────────────────────────
        ["en"] = new()
        {
            [nameof(Nav_Dashboard)]    = "Dashboard",
            [nameof(Nav_CellView)]     = "Cell View",
            [nameof(Nav_ControlPanel)] = "Control Panel",
            [nameof(Nav_Logging)]      = "Logging",
            [nameof(Nav_Playback)]     = "Playback",
            [nameof(Nav_Parameter)]    = "Parameter",
            [nameof(Nav_LiveView)]     = "Live View",
            [nameof(Nav_AI)]           = "AI",

            [nameof(Ui_Dark)]          = "DARK",
            [nameof(Ui_Light)]         = "LIGHT",
            [nameof(Ui_SwitchToLight)] = "Switch to Light mode",
            [nameof(Ui_SwitchToDark)]  = "Switch to Dark mode",
            [nameof(Ui_ChangeLanguage)] = "Change language",
            [nameof(Ui_OpcUaConnection)]  = "OPC UA Connection",
            [nameof(Ui_OpcUaEndpoint)]    = "Endpoint URL",
            [nameof(Ui_OpcUaSecurity)]    = "Security Mode",
            [nameof(Ui_OpcUaAuth)]        = "Authentication",
            [nameof(Ui_OpcUaAnonymous)]   = "Anonymous",
            [nameof(Ui_OpcUaUsernameAuth)]= "Username / Password",
            [nameof(Ui_OpcUaUsername)]    = "Username",
            [nameof(Ui_OpcUaPassword)]    = "Password",
            [nameof(Ui_OpcUaSecNone)]     = "None (No Security)",
            [nameof(Ui_OpcUaSecSign)]     = "Sign",
            [nameof(Ui_OpcUaSecSignEnc)]  = "Sign & Encrypt",
            [nameof(Ui_AlertHistory)]   = "Alert History",
            [nameof(Ui_NoAlerts)]       = "No alerts yet",
            [nameof(Ui_ClearAlerts)]    = "Clear",
            [nameof(Ui_Menu_Customize)]  = "Customize",
            [nameof(Ui_Menu_View)]       = "View",
            [nameof(Ui_Menu_Unit)]       = "Unit",
            [nameof(Ui_Menu_Temperature)] = "Temperature",
            [nameof(Ui_Menu_Voltage)]    = "Voltage",
            [nameof(Ui_Menu_Capacity)]   = "Capacity",
            [nameof(Ui_Menu_About)]      = "About",
            [nameof(Ui_About_Product)]   = "Product",
            [nameof(Ui_About_Version)]   = "Version",
            [nameof(Ui_About_License)]   = "License",
            [nameof(Ui_About_Copyright)] = "Copyright",
            [nameof(Ui_Menu_Tour)]       = "Tour",
            [nameof(Ui_Menu_RefreshApp)] = "Refresh",
            [nameof(Ui_Menu_Zoom)]       = "Zoom",
            [nameof(Ui_Menu_ActualSize)] = "Actual Size",
            [nameof(Ui_Menu_ZoomIn)]     = "Zoom In",
            [nameof(Ui_Menu_ZoomOut)]    = "Zoom Out",
            [nameof(Ui_Menu_Developer)]     = "Developer",
            [nameof(Ui_Menu_OpenLogFolder)] = "Open Log Folder",
            [nameof(Ui_Menu_OpenSettings)]  = "Open settings.json",
            [nameof(Ui_Menu_ReportBug)]     = "Report Bug…",
            [nameof(Ui_Menu_CheckUpdate)]   = "Check for Updates…",

            [nameof(Upd_CheckingTitle)]  = "Checking for Updates",
            [nameof(Upd_UpToDateTitle)]  = "Up to Date",
            [nameof(Upd_UpToDateMsg)]    = "You are running the latest version.",
            [nameof(Upd_AvailableTitle)] = "Update Available",
            [nameof(Upd_CurrentVersion)] = "Current version",
            [nameof(Upd_LatestVersion)]  = "Latest version",
            [nameof(Upd_ReleaseNotes)]   = "Release Notes",
            [nameof(Upd_Download)]       = "Download & Apply",
            [nameof(Upd_OpenPage)]       = "Open Release Page",
            [nameof(Upd_Later)]          = "Later",
            [nameof(Upd_Close)]          = "Close",
            [nameof(Upd_ErrorTitle)]     = "Update Check Failed",
            [nameof(Upd_Downloading)]    = "Downloading Update…",
            [nameof(Upd_InstallNow)]     = "Restart to Apply",
            [nameof(Upd_Extracting)]     = "Preparing Update…",

            [nameof(Com_Min)]    = "MIN",
            [nameof(Com_Max)]    = "MAX",
            [nameof(Com_Avg)]    = "AVG",
            [nameof(Com_Delta)]  = "DELTA",
            [nameof(Com_Status)] = "STATUS",

            [nameof(Dash_SecPackOverview)]  = "PACK OVERVIEW",
            [nameof(Dash_PackVoltage)]      = "PACK VOLTAGE",
            [nameof(Dash_PackNominal)]      = "72V nominal",
            [nameof(Dash_StateOfCharge)]    = "STATE OF CHARGE",
            [nameof(Dash_Remaining)]        = "REMAINING",
            [nameof(Dash_RemainingSub)]     = "based on SOC × capacity",
            [nameof(Dash_SubToEmpty)]       = "{0} to empty",
            [nameof(Dash_SubToFull)]        = "{0} to full",
            [nameof(Dash_SubIdle)]          = "idle",
            [nameof(Dash_Current)]          = "CURRENT",
            [nameof(Dash_CurrentSub)]       = "+ = charging  − = discharging",
            [nameof(Dash_PackConfig)]       = "20S4P NMC",
            [nameof(Dash_SecSocHistory)]    = "SOC HISTORY",
            [nameof(Dash_TimeAgo)]          = "← 2 min ago",
            [nameof(Dash_Now)]              = "now",
            [nameof(Dash_NowArrow)]         = "now →",
            [nameof(Dash_SecViHistory)]     = "VOLTAGE / CURRENT HISTORY",
            [nameof(Dash_VoltageV)]         = "Voltage (V)",
            [nameof(Dash_CurrentA)]         = "Current (A)",
            [nameof(Dash_SecCellSummary)]   = "CELL VOLTAGE SUMMARY",
            [nameof(Dash_TempSensors)]      = "TEMPERATURE SENSORS",
            [nameof(Dash_ActiveCells)]      = "Active Cells",
            [nameof(Dash_CellDelta)]        = "Cell Delta",
            [nameof(Dash_Method)]           = "Method",
            [nameof(Dash_ActiveMethod)]     = "Active (LTC8584)",
            [nameof(Dash_SecAlertsWarnings)] = "ALERT & WARNING",
            [nameof(Dash_SaveChart)]        = "Save chart as PNG",
            [nameof(Dash_SecTempHistory)]   = "TEMPERATURE HISTORY",
            [nameof(Dash_TempC)]            = "°C",
            [nameof(Dash_TempF)]            = "°F",

            [nameof(Cell_SecVoltageSummary)] = "VOLTAGE SUMMARY",
            [nameof(Cell_SecCellGrid)]       = "CELL GRID — 20S4P",
            [nameof(Cell_DeltaLabel)]        = "Δ DELTA",
            [nameof(Cell_Normal)]            = "Normal",
            [nameof(Cell_Low)]               = "Low",
            [nameof(Cell_Undervoltage)]      = "Undervoltage",
            [nameof(Cell_Overvoltage)]       = "Overvoltage",
            [nameof(Cell_Balancing)]         = "Balancing",
            [nameof(Cell_SecNtcReadings)]    = "NTC THERMISTOR READINGS",
            [nameof(Cell_Thresholds)]        = "THRESHOLDS",
            [nameof(Cell_ThreshWarn)]        = "Warning :  60 °C",
            [nameof(Cell_ThreshCutoff)]      = "Cutoff  :  70 °C",
            [nameof(Cell_Legend)]            = "LEGEND",
            [nameof(Cell_NormalDesc)]        = "Normal  (below 60°C)",
            [nameof(Cell_WarnDesc)]          = "Warning (60 – 70°C)",
            [nameof(Cell_CutoffDesc)]        = "Cutoff  (above 70°C)",
            [nameof(Cell_ResetStats)]        = "Reset Statistics",

            [nameof(Ctrl_PhNoPorts)]         = "No COM ports detected",
            ["Ctrl_Refresh"]           = "Refresh",
            [nameof(Ctrl_Connect)]           = "Connect",
            [nameof(Ctrl_Disconnect)]        = "Disconnect",
            [nameof(Ctrl_ConnStatus)]        = "Status",
            [nameof(Ctrl_NotConnected)]      = "Not connected",
            [nameof(Ctrl_OverTempWarn)]      = "Over-Temp Warning",
            [nameof(Ctrl_OverTempCutoff)]    = "Over-Temp Cutoff",
            [nameof(Ctrl_MaxDod)]            = "Max DoD",
            ["Ctrl_SecBalancing"]      = "DIGITAL FLAGS",
            [nameof(Ctrl_StartDelta)]        = "Start Delta",
            [nameof(Ctrl_StopDelta)]         = "Stop Delta",
            [nameof(Ctrl_ResetDefaults)]     = "Reset to Defaults",
            [nameof(Ctrl_ApplySettings)]     = "Apply Settings",

            [nameof(Ctrl_AutoConnect)]       = "Auto-Connect",
            [nameof(Ctrl_AutoConnectHint)]   = "Automatically scan and connect to the first available server.",
            [nameof(Ctrl_ReconnectInterval)] = "Reconnect Interval",
            [nameof(Ctrl_ProbeTimeout)]      = "Probe Timeout",
            [nameof(Ctrl_FramesReceived)]    = "Frames received",
            [nameof(Ctrl_ParseErrors)]       = "Parse errors",

            [nameof(Ctrl_BtDevice)]          = "Device",

            [nameof(Fb_SerialError)]            = "Serial error",
            [nameof(Fb_SelectChannel)]       = "Select a port",
            [nameof(Fb_SelectChannelMsg)]    = "Pick a COM port from the dropdown first.",
            [nameof(Fb_SettingsApplied)]     = "Settings applied",
            [nameof(Fb_SettingsAppliedMsg)]  = "New thresholds are active.",
            [nameof(Fb_DefaultsRestored)]    = "Defaults restored",
            [nameof(Fb_DefaultsRestoredMsg)] = "Values reset — click Apply to activate.",

            [nameof(Log_SecStatus)]         = "LOGGING STATUS",
            [nameof(Log_State)]             = "STATE",
            [nameof(Log_Samples)]           = "SAMPLES",
            [nameof(Log_Duration)]          = "DURATION",
            [nameof(Log_SecFileSettings)]   = "FILE SETTINGS",
            [nameof(Log_Folder)]            = "Folder",
            [nameof(Log_Browse)]            = "Browse…",
            [nameof(Log_Format)]            = "Format",
            [nameof(Log_Filename)]          = "Filename",
            [nameof(Log_PhAutoFilename)]    = "auto-generated if left blank",
            [nameof(Log_SecControls)]       = "CONTROLS",
            [nameof(Log_ConnectHint)]       = "Connect to the server first, then start logging.",
            [nameof(Log_RecordingHint)]     = "Recording in progress. Stop to close / write the file.",
            [nameof(Log_ReadyHint)]         = "Ready. Press Start Logging to begin recording.",
            [nameof(Log_StartLogging)]      = "Start Logging",
            [nameof(Log_StopLogging)]       = "Stop Logging",
            [nameof(Log_OpenFolder)]        = "Open Folder",
            [nameof(Log_SecDataFormat)]     = "DATA FORMAT",
            [nameof(Log_DataDesc1)]         = "Each row = one data frame received from the server (∼1 Hz).",
            [nameof(Log_DataDesc2)]         = "Fields: Timestamp · PackVoltage_V · SOC_pct · Current_A · Status · Cell1_V … Cell20_V · Bal1 … Bal20 · Temp1_C … Temp10_C",
            [nameof(Log_DataDesc3)]         = "CSV / TSV: streamed to disk every frame.  Excel / JSON: buffered in memory and written when you press Stop.",
            [nameof(Log_SecLiveData)]       = "LIVE DATA (LAST 20)",
            [nameof(Log_HdrTimestamp)]      = "TIMESTAMP",
            [nameof(Log_HdrSoc)]            = "SOC %",
            [nameof(Log_HdrPackV)]          = "PACK V",
            [nameof(Log_HdrCurrentA)]       = "CURRENT A",
            [nameof(Log_HdrStatus)]         = "STATUS",
            [nameof(Log_HdrMinCell)]        = "MIN CELL V",
            [nameof(Log_HdrMaxCell)]        = "MAX CELL V",
            [nameof(Log_HdrDeltaMv)]        = "DELTA mV",
            [nameof(Log_HdrBalCells)]       = "BAL CELLS",
            [nameof(Log_NoData)]            = "No data yet — connect to the server to see the data stream.",
            [nameof(Log_Idle)]              = "Idle",
            [nameof(Log_Logging)]           = "Logging",

            [nameof(Pb_SecLoadFile)]   = "LOAD FILE",
            [nameof(Pb_NoFileLoaded)]  = "No file loaded",
            [nameof(Pb_Browse)]        = "Browse…",
            [nameof(Pb_Unload)]        = "Unload",
            [nameof(Pb_LoadStatus)]    = "Browse and open a TLIG Dashboard CSV log file (.csv).",
            [nameof(Pb_SecFileInfo)]   = "FILE INFO",
            [nameof(Pb_Frames)]        = "FRAMES",
            [nameof(Pb_EstDuration)]   = "ESTIMATED DURATION",
            [nameof(Pb_PlaybackSpeed)] = "PLAYBACK SPEED",
            [nameof(Pb_SecHowToUse)]   = "HOW TO USE",
            [nameof(Pb_HowToUse1)]     = "Browse and load a CSV file above, then use the playback bar that appears at the bottom of the window.",
            [nameof(Pb_HowToUse2)]     = "While playing, all pages (Dashboard, Cell View, etc.) update in real-time with the recorded data. Logging is paused automatically during playback. Click ✕ in the playback bar or press Unload to return to live mode.",

            [nameof(Login_Title)]         = "Login",
            [nameof(Login_Username)]      = "Username",
            [nameof(Login_Password)]      = "Password",
            [nameof(Login_UsernameHint)]  = "Enter username",
            [nameof(Login_PasswordHint)]  = "Enter password",
            [nameof(Login_Submit)]        = "Login",
            [nameof(Login_ErrorEmpty)]    = "Username and password cannot be empty.",
            [nameof(Login_ErrorInvalid)]  = "Invalid username or password.",

            [nameof(Account_LoggedInAs)]  = "Logged in as",
            [nameof(Account_Logout)]      = "Logout",

            [nameof(Panel_ModelSistem)]      = "SYSTEM MODEL",
            [nameof(Panel_DiagramBlok)]      = "BLOCK DIAGRAM",
            [nameof(Panel_ParameterPid)]     = "PID PARAMETERS",
            [nameof(Panel_ResponSistem)]     = "SYSTEM RESPONSE",
            [nameof(Panel_RiseTime)]         = "RISE TIME",
            [nameof(Panel_Overshoot)]        = "OVERSHOOT",
            [nameof(Panel_Settling)]         = "SETTLING",
            [nameof(Panel_SteadyErr)]        = "STEADY ERR",
            [nameof(Panel_TransferFunction)] = "TRANSFER FUNCTION",

            [nameof(Ai_Title)]         = "AI ASSISTANT",
            [nameof(Ai_UserLabel)]     = "USER",
            [nameof(Ai_AiLabel)]       = "AI ASSISTANT",
            [nameof(Ai_InputHint)]     = "Type a message...",
            [nameof(Ai_InputHintFull)] = "Type a message to AI Assistant...",
            [nameof(Ai_Settings)]      = "AI Settings",
            [nameof(Ai_ApiUrl)]        = "Server URL",
            [nameof(Ai_ApiKey)]        = "API Key",
            [nameof(Ai_Model)]         = "Model",
            [nameof(Ai_SystemPrompt)]  = "System Prompt",
            [nameof(Ai_ClearChat)]     = "Clear Chat",
            [nameof(Ai_SaveSettings)]  = "Save",
            [nameof(Ai_ErrorNoKey)]    = "API key is required. Open AI Settings to configure.",
            [nameof(Ai_Thinking)]      = "Thinking...",
            [nameof(Ai_StopGen)]       = "Stop",
            [nameof(Ai_ModelLabel)]    = "Model: {0}",
        },

        // ── INDONESIA ─────────────────────────────────────────────────────
        ["id"] = new()
        {
            [nameof(Nav_Dashboard)]    = "Dashboard",
            [nameof(Nav_CellView)]     = "Tampilan Sel",
            [nameof(Nav_ControlPanel)] = "Panel Kontrol",
            [nameof(Nav_Logging)]      = "Logging",
            [nameof(Nav_Playback)]     = "Putar Ulang",
            [nameof(Nav_Parameter)]    = "Parameter",
            [nameof(Nav_LiveView)]     = "Tampilan Live",
            [nameof(Nav_AI)]           = "AI",

            [nameof(Ui_Dark)]          = "GELAP",
            [nameof(Ui_Light)]         = "TERANG",
            [nameof(Ui_SwitchToLight)] = "Beralih ke Mode Terang",
            [nameof(Ui_SwitchToDark)]  = "Beralih ke Mode Gelap",
            [nameof(Ui_ChangeLanguage)] = "Ubah bahasa",
            [nameof(Ui_OpcUaConnection)]  = "Koneksi OPC UA",
            [nameof(Ui_OpcUaEndpoint)]    = "URL Endpoint",
            [nameof(Ui_OpcUaSecurity)]    = "Mode Keamanan",
            [nameof(Ui_OpcUaAuth)]        = "Autentikasi",
            [nameof(Ui_OpcUaAnonymous)]   = "Anonim",
            [nameof(Ui_OpcUaUsernameAuth)]= "Nama Pengguna / Kata Sandi",
            [nameof(Ui_OpcUaUsername)]    = "Nama Pengguna",
            [nameof(Ui_OpcUaPassword)]    = "Kata Sandi",
            [nameof(Ui_OpcUaSecNone)]     = "Tidak Ada (Tanpa Keamanan)",
            [nameof(Ui_OpcUaSecSign)]     = "Tanda Tangan",
            [nameof(Ui_OpcUaSecSignEnc)]  = "Tanda Tangan & Enkripsi",
            [nameof(Ui_AlertHistory)]   = "Riwayat Alert",
            [nameof(Ui_NoAlerts)]       = "Belum ada alert",
            [nameof(Ui_ClearAlerts)]    = "Hapus",
            [nameof(Ui_Menu_Customize)]  = "Kustomisasi",
            [nameof(Ui_Menu_View)]       = "Tampilan",
            [nameof(Ui_Menu_Unit)]       = "Unit",
            [nameof(Ui_Menu_Temperature)] = "Temperatur",
            [nameof(Ui_Menu_Voltage)]    = "Tegangan",
            [nameof(Ui_Menu_Capacity)]   = "Kapasitas",
            [nameof(Ui_Menu_About)]      = "Tentang",
            [nameof(Ui_About_Product)]   = "Produk",
            [nameof(Ui_About_Version)]   = "Versi",
            [nameof(Ui_About_License)]   = "Lisensi",
            [nameof(Ui_About_Copyright)] = "Hak cipta",
            [nameof(Ui_Menu_Tour)]       = "Tour",
            [nameof(Ui_Menu_RefreshApp)] = "Refresh",
            [nameof(Ui_Menu_Zoom)]       = "Zoom",
            [nameof(Ui_Menu_ActualSize)] = "Ukuran Asli",
            [nameof(Ui_Menu_ZoomIn)]     = "Perbesar",
            [nameof(Ui_Menu_ZoomOut)]    = "Perkecil",
            [nameof(Ui_Menu_Developer)]     = "Developer",
            [nameof(Ui_Menu_OpenLogFolder)] = "Buka Folder Log",
            [nameof(Ui_Menu_OpenSettings)]  = "Buka settings.json",
            [nameof(Ui_Menu_ReportBug)]     = "Laporkan Bug…",
            [nameof(Ui_Menu_CheckUpdate)]   = "Periksa Pembaruan…",

            [nameof(Upd_CheckingTitle)]  = "Memeriksa Pembaruan",
            [nameof(Upd_UpToDateTitle)]  = "Versi Terbaru",
            [nameof(Upd_UpToDateMsg)]    = "Anda sudah menggunakan versi terbaru.",
            [nameof(Upd_AvailableTitle)] = "Pembaruan Tersedia",
            [nameof(Upd_CurrentVersion)] = "Versi saat ini",
            [nameof(Upd_LatestVersion)]  = "Versi terbaru",
            [nameof(Upd_ReleaseNotes)]   = "Catatan Rilis",
            [nameof(Upd_Download)]       = "Unduh & Terapkan",
            [nameof(Upd_OpenPage)]       = "Buka Halaman Rilis",
            [nameof(Upd_Later)]          = "Nanti",
            [nameof(Upd_Close)]          = "Tutup",
            [nameof(Upd_ErrorTitle)]     = "Gagal Memeriksa Pembaruan",
            [nameof(Upd_Downloading)]    = "Mengunduh Pembaruan…",
            [nameof(Upd_InstallNow)]     = "Restart untuk Terapkan",
            [nameof(Upd_Extracting)]     = "Mempersiapkan Pembaruan…",

            [nameof(Com_Min)]    = "MIN",
            [nameof(Com_Max)]    = "MAKS",
            [nameof(Com_Avg)]    = "RATA",
            [nameof(Com_Delta)]  = "DELTA",
            [nameof(Com_Status)] = "STATUS",

            [nameof(Dash_SecPackOverview)]  = "IKHTISAR PACK",
            [nameof(Dash_PackVoltage)]      = "TEGANGAN PACK",
            [nameof(Dash_PackNominal)]      = "72V nominal",
            [nameof(Dash_StateOfCharge)]    = "STATUS PENGISIAN",
            [nameof(Dash_Remaining)]        = "SISA",
            [nameof(Dash_RemainingSub)]     = "berdasarkan SOC × kapasitas",
            [nameof(Dash_SubToEmpty)]       = "{0} menuju habis",
            [nameof(Dash_SubToFull)]        = "{0} menuju penuh",
            [nameof(Dash_SubIdle)]          = "siaga",
            [nameof(Dash_Current)]          = "ARUS",
            [nameof(Dash_CurrentSub)]       = "+ = mengisi  − = mengosongkan",
            [nameof(Dash_PackConfig)]       = "20S4P NMC",
            [nameof(Dash_SecSocHistory)]    = "RIWAYAT SOC",
            [nameof(Dash_TimeAgo)]          = "← 2 mnt lalu",
            [nameof(Dash_Now)]              = "sekarang",
            [nameof(Dash_NowArrow)]         = "sekarang →",
            [nameof(Dash_SecViHistory)]     = "RIWAYAT TEGANGAN / ARUS",
            [nameof(Dash_VoltageV)]         = "Tegangan (V)",
            [nameof(Dash_CurrentA)]         = "Arus (A)",
            [nameof(Dash_SecCellSummary)]   = "RINGKASAN TEGANGAN SEL",
            [nameof(Dash_TempSensors)]      = "SENSOR SUHU",
            [nameof(Dash_ActiveCells)]      = "Sel Aktif",
            [nameof(Dash_CellDelta)]        = "Delta Sel",
            [nameof(Dash_Method)]           = "Metode",
            [nameof(Dash_ActiveMethod)]     = "Aktif (LTC8584)",
            [nameof(Dash_SecAlertsWarnings)] = "ALERT & PERINGATAN",
            [nameof(Dash_SaveChart)]        = "Simpan grafik sebagai PNG",
            [nameof(Dash_SecTempHistory)]   = "RIWAYAT SUHU",
            [nameof(Dash_TempC)]            = "°C",
            [nameof(Dash_TempF)]            = "°F",

            [nameof(Cell_SecVoltageSummary)] = "RINGKASAN TEGANGAN",
            [nameof(Cell_SecCellGrid)]       = "GRID SEL — 20S4P",
            [nameof(Cell_DeltaLabel)]        = "Δ DELTA",
            [nameof(Cell_Normal)]            = "Normal",
            [nameof(Cell_Low)]               = "Rendah",
            [nameof(Cell_Undervoltage)]      = "Tegangan Rendah",
            [nameof(Cell_Overvoltage)]       = "Tegangan Tinggi",
            [nameof(Cell_Balancing)]         = "Penyeimbangan",
            [nameof(Cell_SecNtcReadings)]    = "PEMBACAAN TERMISTOR NTC",
            [nameof(Cell_Thresholds)]        = "AMBANG BATAS",
            [nameof(Cell_ThreshWarn)]        = "Peringatan :  60 °C",
            [nameof(Cell_ThreshCutoff)]      = "Pemutus  :  70 °C",
            [nameof(Cell_Legend)]            = "LEGENDA",
            [nameof(Cell_NormalDesc)]        = "Normal  (di bawah 60°C)",
            [nameof(Cell_WarnDesc)]          = "Peringatan (60 – 70°C)",
            [nameof(Cell_CutoffDesc)]        = "Pemutus  (di atas 70°C)",
            [nameof(Cell_ResetStats)]        = "Reset Statistik",

            [nameof(Ctrl_PhNoPorts)]         = "Tidak ada port COM terdeteksi",
            ["Ctrl_Refresh"]           = "Perbarui",
            [nameof(Ctrl_Connect)]           = "Hubungkan",
            [nameof(Ctrl_Disconnect)]        = "Putuskan",
            [nameof(Ctrl_ConnStatus)]        = "Status",
            [nameof(Ctrl_NotConnected)]      = "Tidak terhubung",
            [nameof(Ctrl_OverTempWarn)]      = "Peringatan Suhu Tinggi",
            [nameof(Ctrl_OverTempCutoff)]    = "Pemutus Suhu Tinggi",
            [nameof(Ctrl_MaxDod)]            = "DoD Maks",
            ["Ctrl_SecBalancing"]      = "FLAG DIGITAL",
            [nameof(Ctrl_StartDelta)]        = "Delta Mulai",
            [nameof(Ctrl_StopDelta)]         = "Delta Berhenti",
            [nameof(Ctrl_ResetDefaults)]     = "Reset ke Default",
            [nameof(Ctrl_ApplySettings)]     = "Terapkan Pengaturan",

            [nameof(Ctrl_AutoConnect)]       = "Auto-Hubungkan",
            [nameof(Ctrl_AutoConnectHint)]   = "Memindai dan menghubungkan ke server yang pertama tersedia secara otomatis.",
            [nameof(Ctrl_ReconnectInterval)] = "Interval Pindai",
            [nameof(Ctrl_ProbeTimeout)]      = "Timeout Verifikasi",
            [nameof(Ctrl_FramesReceived)]    = "Frame diterima",
            [nameof(Ctrl_ParseErrors)]       = "Kesalahan parsing",

            [nameof(Ctrl_BtDevice)]          = "Perangkat",

            [nameof(Fb_SerialError)]            = "Kesalahan Serial",
            [nameof(Fb_SelectChannel)]       = "Pilih port",
            [nameof(Fb_SelectChannelMsg)]    = "Pilih port COM dari dropdown terlebih dahulu.",
            [nameof(Fb_SettingsApplied)]     = "Pengaturan diterapkan",
            [nameof(Fb_SettingsAppliedMsg)]  = "Ambang batas baru aktif.",
            [nameof(Fb_DefaultsRestored)]    = "Default dipulihkan",
            [nameof(Fb_DefaultsRestoredMsg)] = "Nilai direset — klik Terapkan untuk mengaktifkan.",

            [nameof(Log_SecStatus)]         = "STATUS LOGGING",
            [nameof(Log_State)]             = "STATUS",
            [nameof(Log_Samples)]           = "SAMPEL",
            [nameof(Log_Duration)]          = "DURASI",
            [nameof(Log_SecFileSettings)]   = "PENGATURAN FILE",
            [nameof(Log_Folder)]            = "Folder",
            [nameof(Log_Browse)]            = "Telusuri…",
            [nameof(Log_Format)]            = "Format",
            [nameof(Log_Filename)]          = "Nama File",
            [nameof(Log_PhAutoFilename)]    = "dibuat otomatis jika kosong",
            [nameof(Log_SecControls)]       = "KONTROL",
            [nameof(Log_ConnectHint)]       = "Hubungkan ke server terlebih dahulu, lalu mulai logging.",
            [nameof(Log_RecordingHint)]     = "Perekaman sedang berlangsung. Hentikan untuk menutup / menulis file.",
            [nameof(Log_ReadyHint)]         = "Siap. Tekan Mulai Logging untuk mulai merekam.",
            [nameof(Log_StartLogging)]      = "Mulai Logging",
            [nameof(Log_StopLogging)]       = "Hentikan Logging",
            [nameof(Log_OpenFolder)]        = "Buka Folder",
            [nameof(Log_SecDataFormat)]     = "FORMAT DATA",
            [nameof(Log_DataDesc1)]         = "Setiap baris = satu frame data yang diterima dari server (∼1 Hz).",
            [nameof(Log_DataDesc2)]         = "Kolom: Timestamp · PackVoltage_V · SOC_pct · Current_A · Status · Cell1_V … Cell20_V · Bal1 … Bal20 · Temp1_C … Temp10_C",
            [nameof(Log_DataDesc3)]         = "CSV / TSV: langsung ditulis setiap frame.  Excel / JSON: disimpan di memori dan ditulis saat Anda menekan Stop.",
            [nameof(Log_SecLiveData)]       = "DATA LANGSUNG (20 TERBARU)",
            [nameof(Log_HdrTimestamp)]      = "TIMESTAMP",
            [nameof(Log_HdrSoc)]            = "SOC %",
            [nameof(Log_HdrPackV)]          = "PACK V",
            [nameof(Log_HdrCurrentA)]       = "ARUS A",
            [nameof(Log_HdrStatus)]         = "STATUS",
            [nameof(Log_HdrMinCell)]        = "MIN SEL V",
            [nameof(Log_HdrMaxCell)]        = "MAKS SEL V",
            [nameof(Log_HdrDeltaMv)]        = "DELTA mV",
            [nameof(Log_HdrBalCells)]       = "BAL CELLS",
            [nameof(Log_NoData)]            = "Belum ada data — hubungkan ke server untuk melihat data stream.",
            [nameof(Log_Idle)]              = "Diam",
            [nameof(Log_Logging)]           = "Merekam",

            [nameof(Pb_SecLoadFile)]   = "MUAT FILE",
            [nameof(Pb_NoFileLoaded)]  = "Tidak ada file yang dimuat",
            [nameof(Pb_Browse)]        = "Telusuri…",
            [nameof(Pb_Unload)]        = "Hapus",
            [nameof(Pb_LoadStatus)]    = "Telusuri dan buka file log CSV TLIG Dashboard (.csv).",
            [nameof(Pb_SecFileInfo)]   = "INFO FILE",
            [nameof(Pb_Frames)]        = "BINGKAI",
            [nameof(Pb_EstDuration)]   = "PERKIRAAN DURASI",
            [nameof(Pb_PlaybackSpeed)] = "KECEPATAN PUTAR",
            [nameof(Pb_SecHowToUse)]   = "CARA MENGGUNAKAN",
            [nameof(Pb_HowToUse1)]     = "Telusuri dan muat file CSV di atas, lalu gunakan bilah putar ulang yang muncul di bagian bawah jendela.",
            [nameof(Pb_HowToUse2)]     = "Saat diputar, semua halaman (Dashboard, Tampilan Sel, dll.) diperbarui secara real-time. Logging dijeda otomatis saat playback. Klik ✕ di bilah putar ulang atau tekan Hapus untuk kembali ke mode live.",

            [nameof(Login_Title)]         = "Masuk",
            [nameof(Login_Username)]      = "Nama Pengguna",
            [nameof(Login_Password)]      = "Kata Sandi",
            [nameof(Login_UsernameHint)]  = "Masukkan nama pengguna",
            [nameof(Login_PasswordHint)]  = "Masukkan kata sandi",
            [nameof(Login_Submit)]        = "Masuk",
            [nameof(Login_ErrorEmpty)]    = "Username dan password tidak boleh kosong.",
            [nameof(Login_ErrorInvalid)]  = "Username atau password salah.",

            [nameof(Account_LoggedInAs)]  = "Masuk sebagai",
            [nameof(Account_Logout)]      = "Keluar",

            [nameof(Panel_ModelSistem)]      = "MODEL SISTEM",
            [nameof(Panel_DiagramBlok)]      = "DIAGRAM BLOK",
            [nameof(Panel_ParameterPid)]     = "PARAMETER PID",
            [nameof(Panel_ResponSistem)]     = "RESPON SISTEM",
            [nameof(Panel_RiseTime)]         = "RISE TIME",
            [nameof(Panel_Overshoot)]        = "OVERSHOOT",
            [nameof(Panel_Settling)]         = "SETTLING",
            [nameof(Panel_SteadyErr)]        = "STEADY ERR",
            [nameof(Panel_TransferFunction)] = "FUNGSI TRANSFER",

            [nameof(Ai_Title)]         = "ASISTEN AI",
            [nameof(Ai_UserLabel)]     = "PENGGUNA",
            [nameof(Ai_AiLabel)]       = "ASISTEN AI",
            [nameof(Ai_InputHint)]     = "Ketik pesan...",
            [nameof(Ai_InputHintFull)] = "Ketik pesan ke AI Assistant...",
            [nameof(Ai_Settings)]      = "Pengaturan AI",
            [nameof(Ai_ApiUrl)]        = "URL Server",
            [nameof(Ai_ApiKey)]        = "Kunci API",
            [nameof(Ai_Model)]         = "Model",
            [nameof(Ai_SystemPrompt)]  = "System Prompt",
            [nameof(Ai_ClearChat)]     = "Hapus Obrolan",
            [nameof(Ai_SaveSettings)]  = "Simpan",
            [nameof(Ai_ErrorNoKey)]    = "API key diperlukan. Buka Pengaturan AI untuk mengkonfigurasi.",
            [nameof(Ai_Thinking)]      = "Berpikir...",
            [nameof(Ai_StopGen)]       = "Berhenti",
            [nameof(Ai_ModelLabel)]    = "Model: {0}",
        },
    };

    private static readonly Dictionary<string, Dictionary<string, string>> _extraStrings = new()
    {
        ["en"] = new()
        {
            ["Ui_StartupError"] = "Startup Error",
            ["Ui_Ok"] = "OK",
            ["Ui_Close"] = "Close",
            ["Ui_Cancel"] = "Cancel",
            ["Ui_Save"] = "Save",
            ["Ui_SaveEllipsis"] = "Save...",
            ["Ui_SaveFailed"] = "Save failed",
            ["Ui_Error"] = "Error",
            ["Ui_ErrorWithMessage"] = "Error: {0}",
            ["Ui_SourceNotConnected"] = "SOURCE: NOT CONNECTED",
            ["Ui_SourceConnected"] = "SOURCE: {0} @ {1}",
            ["Ui_InitialConnectionHint"] = "Not connected - open Control Panel to connect to the server",

            ["OpcUa_StatusConnected"]   = "Connected — {0}",
            ["OpcUa_StatusDisconnected"] = "Disconnected",
            ["OpcUa_StatusConnecting"]   = "Connecting…",
            ["OpcUa_StatusReconnecting"] = "Reconnecting…",
            ["Serial_ReadError"] = "Serial read error: {0}",
            ["Serial_ParseError"] = "ESP JSON parse error (total: {0}) - data: \"{1}\"",
            ["AutoConnect_Suspended"] = "Auto-connect paused - click Connect to reconnect.",
            ["AutoConnect_Verified"] = "{0} verified - connecting...",
            ["AutoConnect_ConnectFailed"] = "{0} - failed to connect after verification.",

            ["Bt_StatusDisconnected"] = "Bluetooth disconnected",
            ["Bt_ScanError"] = "Bluetooth scan failed: {0}",
            ["Bt_OpenFailed"] = "Failed to connect to {0}: {1}",
            ["Bt_NoNusService"] = "{0} does not expose the Nordic UART Service.",
            ["Bt_NoTxCharacteristic"] = "{0} - notify characteristic not found.",
            ["Bt_SubscribeFailed"] = "{0} - could not subscribe to notifications.",
            ["Bt_ReadError"] = "Bluetooth read error: {0}",
            ["Bt_ParseError"] = "BLE JSON parse error (total: {0}) - data: \"{1}\"",
            ["Bt_FbSelect"] = "Select a device",
            ["Bt_FbSelectMsg"] = "Pick a Bluetooth device from the dropdown first.",

            ["Dash_NotBalancing"] = "All stable",
            ["Chart_TimeAxis"] = "time ({0})",
            ["Chart_SampleRateUnknown"] = "- sample/s",
            ["Chart_SamplesPerSecond"] = "{0} samples/s",
            ["Chart_SecondsPerSample"] = "{0} s/sample",
            ["Chart_Seconds"] = "seconds",
            ["Chart_Minutes"] = "minutes",
            ["Chart_Hours"] = "hours",
            ["PackStatus_Idle"] = "Idle",
            ["PackStatus_Charging"] = "Charging",
            ["PackStatus_Discharging"] = "Discharging",
            ["PackStatus_Full"] = "Full",
            ["PackStatus_Error"] = "Error",

            ["Log_FormatCsv"] = "CSV - comma-separated (.csv)",
            ["Log_FormatTsv"] = "TSV - tab-separated (.tsv)",
            ["Log_FormatExcel"] = "Excel - workbook (.xlsx)",
            ["Log_FormatJson"] = "JSON - array of objects (.json)",
            ["Log_ColPackVoltage"] = "Main Value (V)",
            ["Log_ColCurrent"] = "Current (A)",
            ["Log_ColCell"] = "Cell {0} (V)",
            ["Log_ColBalancing"] = "Flag {0}",
            ["Log_ColTemp"] = "Temp {0} (C)",

            ["Pb_LoadedFramesFromFile"] = "Loaded {0:N0} frames from \"{1}\".",
            ["Pb_Loading"] = "Loading...",
            ["Pb_Error"] = "Error: {0}",
            ["Pb_FileNoDataRows"] = "File has no data rows.",
            ["Pb_ExcelNoDataRows"] = "Excel file has no data rows.",
            ["Pb_JsonNoRows"] = "JSON file contains no rows.",
            ["Pb_NoValidDataRows"] = "No valid data rows found.",

            ["Cell_NtcThermistor"] = "Sensor",
            ["Cell_TimeRangeTrim"] = "TIME RANGE (TRIM)",
            ["Cell_TrimNoData"] = "No data captured yet",
            ["Cell_TrimReset"] = "Reset",
            ["Cell_TrimHint"] = "Drag the handles to select a time range. The temperature chart updates live to the selected window. Reset to return to the rolling view.",
            ["Cell_TrimFullRange"] = "Full range: {0} -> {1}  ·  {2}  (drag a handle to trim)",
            ["Cell_TrimTrimmedRange"] = "Trim: {0} -> {1}  ·  {2}",
            ["Cell_TempHistoryTitle"] = "NTC {0} Temperature History",
            ["Cell_VoltageHistoryTitle"] = "Cell C{0:D2} Voltage History",
            ["Cell_CurrentValue"] = "Current: {0}",
            ["Cell_TempAxis"] = "Temp",
            ["Cell_VoltageAxis"] = "Voltage",
            ["Cell_SampleNumber"] = "Sample #",
            ["Cell_ElapsedSeconds"] = "Elapsed time (s)",
            ["Cell_ElapsedClock"] = "Elapsed time ({0})",
            ["Cell_OneSample"] = "1 sample collected. Waiting for more samples to draw a line.",
            ["Cell_NoTempHistory"] = "No temperature history yet. Waiting for live/playback samples.",
            ["Cell_NoVoltageHistory"] = "No voltage history yet. Waiting for live/playback samples.",
            ["Cell_RangeSummary"] = "{0} samples  ·  {1}-{2} {3}  ·  Range {4} {3}",
            ["Cell_DeltaSummary"] = "{0} samples  ·  {1}-{2} {3}  ·  Delta {4} {3}",

            ["Export_Title"] = "Export chart",
            ["Export_PreviewLive"] = "Preview (live)",
            ["Export_TimeRange"] = "Time range",
            ["Export_TimeRangeHint"] = "Drag the handles to select a segment. Full range: 0 - {0} s ({1:F1} min)",
            ["Export_Dimensions"] = "Dimensions",
            ["Export_AspectRatio"] = "Aspect ratio",
            ["Export_WidthPx"] = "Width (px)",
            ["Export_HeightPx"] = "Height (px)",
            ["Export_FileFormat"] = "File format",
            ["Export_Aspect43"] = "4:3 - paper / Origin default",
            ["Export_Aspect32"] = "3:2 - photo / wide paper",
            ["Export_Aspect169"] = "16:9 - slide / video",
            ["Export_AspectGolden"] = "Golden 1.618:1",
            ["Export_Aspect11"] = "1:1 - square (correlation)",
            ["Export_AspectCustom"] = "Custom - set height manually",
            ["Export_FormatPng"] = "PNG - raster, lossless",
            ["Export_FormatJpg"] = "JPG - raster, smaller",
            ["Export_FormatSvg"] = "SVG - vector, editable",
            ["Export_FileTypePng"] = "PNG image",
            ["Export_FileTypeJpeg"] = "JPEG image",
            ["Export_FileTypeSvg"] = "SVG vector",

            ["Alert_SerialErrorTitle"] = "Serial Error",
            ["Alert_ConnectionTitle"] = "Connection",
            ["Alert_OvervoltageTitle"] = "High Value Alert",
            ["Alert_HighVoltageTitle"] = "High Value Warning",
            ["Alert_UndervoltageTitle"] = "Low Value Alert",
            ["Alert_LowVoltageTitle"] = "Low Value Warning",
            ["Alert_OvercurrentTitle"] = "Overcurrent Alert",
            ["Alert_TempCriticalTitle"] = "High Temperature Critical",
            ["Alert_TempWarningTitle"] = "Temperature Warning",
            ["Alert_ImbalanceTitle"] = "Channel Imbalance",
            ["Alert_CellOvervoltageBody"] = "Cell {0} at {1:F3}V - exceeds {2:F2}V cutoff",
            ["Alert_CellHighVoltageBody"] = "Cell {0} at {1:F3}V - exceeds {2:F2}V warning",
            ["Alert_CellUndervoltageBody"] = "Cell {0} at {1:F3}V - below {2:F2}V cutoff",
            ["Alert_CellLowVoltageBody"] = "Cell {0} at {1:F3}V - below {2:F2}V warning",
            ["Alert_ChargeCurrentBody"] = "Charge current {0:F1}A - exceeds {1:F0}A limit",
            ["Alert_DischargeCurrentBody"] = "Discharge current {0:F1}A - exceeds {1:F0}A limit",
            ["Alert_TempCriticalBody"] = "Sensor {0} at {1:F0}C - exceeds {2:F0}C cutoff",
            ["Alert_TempWarningBody"] = "Sensor {0} at {1:F0}C - exceeds {2:F0}C warning",
            ["Alert_ImbalanceBody"] = "Delta {0:F1}mV - exceeds {1:F0}mV threshold",
        },
        ["id"] = new()
        {
            ["Ui_StartupError"] = "Error Startup",
            ["Ui_Ok"] = "OK",
            ["Ui_Close"] = "Tutup",
            ["Ui_Cancel"] = "Batal",
            ["Ui_Save"] = "Simpan",
            ["Ui_SaveEllipsis"] = "Simpan...",
            ["Ui_SaveFailed"] = "Gagal menyimpan",
            ["Ui_Error"] = "Error",
            ["Ui_ErrorWithMessage"] = "Error: {0}",
            ["Ui_SourceNotConnected"] = "SUMBER: TIDAK TERHUBUNG",
            ["Ui_SourceConnected"] = "SUMBER: {0} @ {1}",
            ["Ui_InitialConnectionHint"] = "Tidak terhubung - buka Panel Kontrol untuk terhubung ke server",

            ["OpcUa_StatusConnected"]   = "Terhubung — {0}",
            ["OpcUa_StatusDisconnected"] = "Terputus",
            ["OpcUa_StatusConnecting"]   = "Menghubungkan…",
            ["OpcUa_StatusReconnecting"] = "Menghubungkan kembali…",
            ["Serial_ReadError"] = "Error baca serial: {0}",
            ["Serial_ParseError"] = "Error parsing JSON ESP (total: {0}) - data: \"{1}\"",
            ["AutoConnect_Suspended"] = "Auto-connect dijeda - klik Hubungkan untuk menghubungkan kembali.",
            ["AutoConnect_Verified"] = "{0} terverifikasi - menghubungkan...",
            ["AutoConnect_ConnectFailed"] = "{0} - gagal terhubung setelah verifikasi.",

            ["Bt_StatusDisconnected"] = "Bluetooth terputus",
            ["Bt_ScanError"] = "Pemindaian Bluetooth gagal: {0}",
            ["Bt_OpenFailed"] = "Gagal terhubung ke {0}: {1}",
            ["Bt_NoNusService"] = "{0} tidak menyediakan Nordic UART Service.",
            ["Bt_NoTxCharacteristic"] = "{0} - karakteristik notify tidak ditemukan.",
            ["Bt_SubscribeFailed"] = "{0} - tidak dapat berlangganan notifikasi.",
            ["Bt_ReadError"] = "Error baca Bluetooth: {0}",
            ["Bt_ParseError"] = "Error parsing JSON BLE (total: {0}) - data: \"{1}\"",
            ["Bt_FbSelect"] = "Pilih perangkat",
            ["Bt_FbSelectMsg"] = "Pilih perangkat Bluetooth dari dropdown terlebih dahulu.",

            ["Dash_NotBalancing"] = "Semua stabil",
            ["Chart_TimeAxis"] = "waktu ({0})",
            ["Chart_SampleRateUnknown"] = "- sampel/dtk",
            ["Chart_SamplesPerSecond"] = "{0} sampel/dtk",
            ["Chart_SecondsPerSample"] = "{0} dtk/sampel",
            ["Chart_Seconds"] = "detik",
            ["Chart_Minutes"] = "menit",
            ["Chart_Hours"] = "jam",
            ["PackStatus_Idle"] = "Siaga",
            ["PackStatus_Charging"] = "Mengisi",
            ["PackStatus_Discharging"] = "Mengosongkan",
            ["PackStatus_Full"] = "Penuh",
            ["PackStatus_Error"] = "Error",

            ["Log_FormatCsv"] = "CSV - dipisahkan koma (.csv)",
            ["Log_FormatTsv"] = "TSV - dipisahkan tab (.tsv)",
            ["Log_FormatExcel"] = "Excel - workbook (.xlsx)",
            ["Log_FormatJson"] = "JSON - array objek (.json)",
            ["Log_ColPackVoltage"] = "Nilai Utama (V)",
            ["Log_ColCurrent"] = "Arus (A)",
            ["Log_ColCell"] = "Sel {0} (V)",
            ["Log_ColBalancing"] = "Flag {0}",
            ["Log_ColTemp"] = "Suhu {0} (C)",

            ["Pb_LoadedFramesFromFile"] = "Memuat {0:N0} frame dari \"{1}\".",
            ["Pb_Loading"] = "Memuat...",
            ["Pb_Error"] = "Error: {0}",
            ["Pb_FileNoDataRows"] = "File tidak memiliki baris data.",
            ["Pb_ExcelNoDataRows"] = "File Excel tidak memiliki baris data.",
            ["Pb_JsonNoRows"] = "File JSON tidak berisi baris data.",
            ["Pb_NoValidDataRows"] = "Tidak ada baris data valid yang ditemukan.",

            ["Cell_NtcThermistor"] = "Sensor",
            ["Cell_TimeRangeTrim"] = "RENTANG WAKTU (TRIM)",
            ["Cell_TrimNoData"] = "Belum ada data yang direkam",
            ["Cell_TrimReset"] = "Reset",
            ["Cell_TrimHint"] = "Geser handle untuk memilih rentang waktu. Grafik suhu diperbarui langsung mengikuti rentang terpilih. Reset untuk kembali ke tampilan berjalan.",
            ["Cell_TrimFullRange"] = "Rentang penuh: {0} -> {1}  ·  {2}  (geser handle untuk trim)",
            ["Cell_TrimTrimmedRange"] = "Trim: {0} -> {1}  ·  {2}",
            ["Cell_TempHistoryTitle"] = "Riwayat Suhu NTC {0}",
            ["Cell_VoltageHistoryTitle"] = "Riwayat Tegangan Sel C{0:D2}",
            ["Cell_CurrentValue"] = "Saat ini: {0}",
            ["Cell_TempAxis"] = "Suhu",
            ["Cell_VoltageAxis"] = "Tegangan",
            ["Cell_SampleNumber"] = "Sampel #",
            ["Cell_ElapsedSeconds"] = "Waktu berlalu (dtk)",
            ["Cell_ElapsedClock"] = "Waktu berlalu ({0})",
            ["Cell_OneSample"] = "1 sampel terkumpul. Menunggu sampel tambahan untuk menggambar garis.",
            ["Cell_NoTempHistory"] = "Belum ada riwayat suhu. Menunggu sampel live/playback.",
            ["Cell_NoVoltageHistory"] = "Belum ada riwayat tegangan. Menunggu sampel live/playback.",
            ["Cell_RangeSummary"] = "{0} sampel  ·  {1}-{2} {3}  ·  Rentang {4} {3}",
            ["Cell_DeltaSummary"] = "{0} sampel  ·  {1}-{2} {3}  ·  Delta {4} {3}",

            ["Export_Title"] = "Ekspor grafik",
            ["Export_PreviewLive"] = "Pratinjau (live)",
            ["Export_TimeRange"] = "Rentang waktu",
            ["Export_TimeRangeHint"] = "Geser handle untuk memilih segmen. Rentang penuh: 0 - {0} dtk ({1:F1} mnt)",
            ["Export_Dimensions"] = "Dimensi",
            ["Export_AspectRatio"] = "Rasio aspek",
            ["Export_WidthPx"] = "Lebar (px)",
            ["Export_HeightPx"] = "Tinggi (px)",
            ["Export_FileFormat"] = "Format file",
            ["Export_Aspect43"] = "4:3 - kertas / default Origin",
            ["Export_Aspect32"] = "3:2 - foto / kertas lebar",
            ["Export_Aspect169"] = "16:9 - slide / video",
            ["Export_AspectGolden"] = "Golden 1.618:1",
            ["Export_Aspect11"] = "1:1 - persegi (korelasi)",
            ["Export_AspectCustom"] = "Kustom - atur tinggi manual",
            ["Export_FormatPng"] = "PNG - raster, lossless",
            ["Export_FormatJpg"] = "JPG - raster, lebih kecil",
            ["Export_FormatSvg"] = "SVG - vektor, bisa diedit",
            ["Export_FileTypePng"] = "Gambar PNG",
            ["Export_FileTypeJpeg"] = "Gambar JPEG",
            ["Export_FileTypeSvg"] = "Vektor SVG",

            ["Alert_SerialErrorTitle"] = "Error Serial",
            ["Alert_ConnectionTitle"] = "Koneksi",
            ["Alert_OvervoltageTitle"] = "Alert Nilai Tinggi",
            ["Alert_HighVoltageTitle"] = "Peringatan Nilai Tinggi",
            ["Alert_UndervoltageTitle"] = "Alert Nilai Rendah",
            ["Alert_LowVoltageTitle"] = "Peringatan Nilai Rendah",
            ["Alert_OvercurrentTitle"] = "Alert Arus Berlebih",
            ["Alert_TempCriticalTitle"] = "Alert Suhu Kritis",
            ["Alert_TempWarningTitle"] = "Peringatan Suhu",
            ["Alert_ImbalanceTitle"] = "Ketidakseimbangan Kanal",
            ["Alert_CellOvervoltageBody"] = "Sel {0} pada {1:F3}V - melebihi cutoff {2:F2}V",
            ["Alert_CellHighVoltageBody"] = "Sel {0} pada {1:F3}V - melebihi peringatan {2:F2}V",
            ["Alert_CellUndervoltageBody"] = "Sel {0} pada {1:F3}V - di bawah cutoff {2:F2}V",
            ["Alert_CellLowVoltageBody"] = "Sel {0} pada {1:F3}V - di bawah peringatan {2:F2}V",
            ["Alert_ChargeCurrentBody"] = "Arus pengisian {0:F1}A - melebihi batas {1:F0}A",
            ["Alert_DischargeCurrentBody"] = "Arus pengosongan {0:F1}A - melebihi batas {1:F0}A",
            ["Alert_TempCriticalBody"] = "Sensor {0} pada {1:F0}C - melebihi cutoff {2:F0}C",
            ["Alert_TempWarningBody"] = "Sensor {0} pada {1:F0}C - melebihi peringatan {2:F0}C",
            ["Alert_ImbalanceBody"] = "Delta {0:F1}mV - melebihi ambang {1:F0}mV",
        },
    };
}
