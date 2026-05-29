using System.ComponentModel;
using System.Globalization;

namespace TLIGDashboard.Services;

/// <summary>
/// Singleton localization manager. Changing <see cref="CurrentLanguage"/> raises
/// PropertyChanged("") so every {x:Bind Lang.XYZ, Mode=OneWay} binding refreshes.
/// Supported: id · ms · en · nl · zh
/// </summary>
public sealed class LocalizationManager : INotifyPropertyChanged
{
    public static readonly string[] SupportedLanguages = ["id", "ms", "en", "nl", "zh"];
    public static readonly string[] LanguageLabels     = ["Indonesia", "Malay", "English", "Nederlands", "中文"];

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

    // ── Caption-bar serial picker ─────────────────────────────────────────
    public string Ui_SerialConnection  => T(nameof(Ui_SerialConnection));
    public string Ui_SerialQuickAccess => T(nameof(Ui_SerialQuickAccess));
    public string Ui_TabSerial         => T(nameof(Ui_TabSerial));
    public string Ui_TabBluetooth      => T(nameof(Ui_TabBluetooth));
    public string Ui_BluetoothConnection => T(nameof(Ui_BluetoothConnection));

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
    public string Dash_BalancingStatus  => T(nameof(Dash_BalancingStatus));
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
    public string Ctrl_SecConnection      => T(nameof(Ctrl_SecConnection));
    public string Ctrl_SecSerial          => T(nameof(Ctrl_SecSerial));
    public string Ctrl_SerialPort         => T(nameof(Ctrl_SerialPort));
    public string Ctrl_PhScanning         => T(nameof(Ctrl_PhScanning));
    public string Ctrl_PhNoPorts          => T(nameof(Ctrl_PhNoPorts));
    public string Ctrl_Refresh            => T(nameof(Ctrl_Refresh));
    public string Ctrl_Connect            => T(nameof(Ctrl_Connect));
    public string Ctrl_Disconnect         => T(nameof(Ctrl_Disconnect));
    public string Ctrl_SerialBaud         => T(nameof(Ctrl_SerialBaud));
    public string Ctrl_ConnStatus         => T(nameof(Ctrl_ConnStatus));
    public string Ctrl_NotConnected       => T(nameof(Ctrl_NotConnected));
    public string Ctrl_AutoConnectStatus  => T(nameof(Ctrl_AutoConnectStatus));
    public string Ctrl_SecCapacity        => T(nameof(Ctrl_SecCapacity));
    public string Ctrl_NominalCapacity    => T(nameof(Ctrl_NominalCapacity));
    public string Ctrl_CapacityHint       => T(nameof(Ctrl_CapacityHint));
    public string Ctrl_SecProtection      => T(nameof(Ctrl_SecProtection));
    public string Ctrl_OvervoltCutoff     => T(nameof(Ctrl_OvervoltCutoff));
    public string Ctrl_HighVoltWarn       => T(nameof(Ctrl_HighVoltWarn));
    public string Ctrl_UnderVoltCutoff    => T(nameof(Ctrl_UnderVoltCutoff));
    public string Ctrl_LowVoltWarn        => T(nameof(Ctrl_LowVoltWarn));
    public string Ctrl_OverTempWarn       => T(nameof(Ctrl_OverTempWarn));
    public string Ctrl_OverTempCutoff     => T(nameof(Ctrl_OverTempCutoff));
    public string Ctrl_SecCurrentLimits   => T(nameof(Ctrl_SecCurrentLimits));
    public string Ctrl_MaxCharge          => T(nameof(Ctrl_MaxCharge));
    public string Ctrl_MaxDischarge       => T(nameof(Ctrl_MaxDischarge));
    public string Ctrl_MaxDod             => T(nameof(Ctrl_MaxDod));
    public string Ctrl_SecBalancing       => T(nameof(Ctrl_SecBalancing));
    public string Ctrl_StartDelta         => T(nameof(Ctrl_StartDelta));
    public string Ctrl_StopDelta          => T(nameof(Ctrl_StopDelta));
    public string Ctrl_ResetDefaults      => T(nameof(Ctrl_ResetDefaults));
    public string Ctrl_ApplySettings      => T(nameof(Ctrl_ApplySettings));

    // ── Control Panel — advanced serial parameters ────────────────────────
    public string Ctrl_SecSerialAdvanced  => T(nameof(Ctrl_SecSerialAdvanced));
    public string Ctrl_AutoConnect        => T(nameof(Ctrl_AutoConnect));
    public string Ctrl_AutoConnectHint    => T(nameof(Ctrl_AutoConnectHint));
    public string Ctrl_ReconnectInterval  => T(nameof(Ctrl_ReconnectInterval));
    public string Ctrl_ProbeTimeout       => T(nameof(Ctrl_ProbeTimeout));
    public string Ctrl_FramesReceived     => T(nameof(Ctrl_FramesReceived));
    public string Ctrl_ParseErrors        => T(nameof(Ctrl_ParseErrors));

    // ── Control Panel — Bluetooth ─────────────────────────────────────────
    public string Ctrl_SecBluetooth       => T(nameof(Ctrl_SecBluetooth));
    public string Ctrl_BtDevice           => T(nameof(Ctrl_BtDevice));
    public string Ctrl_BtScan             => T(nameof(Ctrl_BtScan));
    public string Ctrl_BtStopScan         => T(nameof(Ctrl_BtStopScan));
    public string Ctrl_BtConnect          => T(nameof(Ctrl_BtConnect));
    public string Ctrl_BtDisconnect       => T(nameof(Ctrl_BtDisconnect));
    public string Ctrl_BtPhSelect         => T(nameof(Ctrl_BtPhSelect));
    public string Ctrl_BtPhNoDevices      => T(nameof(Ctrl_BtPhNoDevices));
    public string Ctrl_BtHint             => T(nameof(Ctrl_BtHint));

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

    // Supplemental UI text used by code-behind and dynamic controls.
    public string Log_FormatCsv       => T(nameof(Log_FormatCsv));
    public string Log_FormatTsv       => T(nameof(Log_FormatTsv));
    public string Log_FormatExcel     => T(nameof(Log_FormatExcel));
    public string Log_FormatJson      => T(nameof(Log_FormatJson));
    public string Cell_NtcThermistor  => T(nameof(Cell_NtcThermistor));
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
            [nameof(Ui_SerialConnection)]  = "Serial Connection",
            [nameof(Ui_SerialQuickAccess)] = "Quick serial access",
            [nameof(Ui_TabSerial)]         = "Serial",
            [nameof(Ui_TabBluetooth)]      = "Bluetooth",
            [nameof(Ui_BluetoothConnection)] = "Bluetooth Connection",
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
            [nameof(Dash_BalancingStatus)]  = "BALANCING STATUS",
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

            [nameof(Ctrl_SecConnection)]        = "CONNECTION",
            [nameof(Ctrl_SecSerial)]            = "SERIAL CONNECTION",
            [nameof(Ctrl_SerialPort)]        = "COM Port",
            [nameof(Ctrl_PhScanning)]        = "Scanning ports…",
            [nameof(Ctrl_PhNoPorts)]         = "No COM ports detected",
            [nameof(Ctrl_Refresh)]           = "Refresh",
            [nameof(Ctrl_Connect)]           = "Connect",
            [nameof(Ctrl_Disconnect)]        = "Disconnect",
            [nameof(Ctrl_SerialBaud)]        = "Baud Rate",
            [nameof(Ctrl_ConnStatus)]        = "Status",
            [nameof(Ctrl_NotConnected)]      = "Not connected",
            [nameof(Ctrl_AutoConnectStatus)] = "Auto-connect active — waiting for ESP32 BMS data…",
            [nameof(Ctrl_SecCapacity)]       = "BATTERY CAPACITY",
            [nameof(Ctrl_NominalCapacity)]   = "Nominal Capacity",
            [nameof(Ctrl_CapacityHint)]      = "Used to calculate remaining capacity (mAh) on dashboard.",
            [nameof(Ctrl_SecProtection)]     = "PROTECTION THRESHOLDS",
            [nameof(Ctrl_OvervoltCutoff)]    = "Overvoltage Cutoff",
            [nameof(Ctrl_HighVoltWarn)]      = "High Voltage Warning",
            [nameof(Ctrl_UnderVoltCutoff)]   = "Undervoltage Cutoff",
            [nameof(Ctrl_LowVoltWarn)]       = "Low Voltage Warning",
            [nameof(Ctrl_OverTempWarn)]      = "Over-Temp Warning",
            [nameof(Ctrl_OverTempCutoff)]    = "Over-Temp Cutoff",
            [nameof(Ctrl_SecCurrentLimits)]  = "CURRENT LIMITS",
            [nameof(Ctrl_MaxCharge)]         = "Max Charge Current",
            [nameof(Ctrl_MaxDischarge)]      = "Max Discharge Current",
            [nameof(Ctrl_MaxDod)]            = "Max DoD",
            [nameof(Ctrl_SecBalancing)]      = "ACTIVE BALANCING (LTC8584)",
            [nameof(Ctrl_StartDelta)]        = "Start Delta",
            [nameof(Ctrl_StopDelta)]         = "Stop Delta",
            [nameof(Ctrl_ResetDefaults)]     = "Reset to Defaults",
            [nameof(Ctrl_ApplySettings)]     = "Apply Settings",

            [nameof(Ctrl_SecSerialAdvanced)]    = "SERIAL PARAMETERS",
            [nameof(Ctrl_AutoConnect)]       = "Auto-Connect",
            [nameof(Ctrl_AutoConnectHint)]   = "Automatically scan COM ports and lock onto the first one that broadcasts BMS data.",
            [nameof(Ctrl_ReconnectInterval)] = "Reconnect Interval",
            [nameof(Ctrl_ProbeTimeout)]      = "Probe Timeout",
            [nameof(Ctrl_FramesReceived)]    = "Frames received",
            [nameof(Ctrl_ParseErrors)]       = "Parse errors",

            [nameof(Ctrl_SecBluetooth)]      = "BLUETOOTH CONNECTION",
            [nameof(Ctrl_BtDevice)]          = "Device",
            [nameof(Ctrl_BtScan)]            = "Scan",
            [nameof(Ctrl_BtStopScan)]        = "Stop",
            [nameof(Ctrl_BtConnect)]         = "Connect",
            [nameof(Ctrl_BtDisconnect)]      = "Disconnect",
            [nameof(Ctrl_BtPhSelect)]        = "Select a device…",
            [nameof(Ctrl_BtPhNoDevices)]     = "No devices yet — press Scan",
            [nameof(Ctrl_BtHint)]            = "ESP32 BLE devices that advertise the Nordic UART Service (NUS) will appear here. Pair the device in Windows Settings first.",

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
            [nameof(Log_ConnectHint)]       = "Connect to the ESP32 first, then start logging.",
            [nameof(Log_RecordingHint)]     = "Recording in progress. Stop to close / write the file.",
            [nameof(Log_ReadyHint)]         = "Ready. Press Start Logging to begin recording.",
            [nameof(Log_StartLogging)]      = "Start Logging",
            [nameof(Log_StopLogging)]       = "Stop Logging",
            [nameof(Log_OpenFolder)]        = "Open Folder",
            [nameof(Log_SecDataFormat)]     = "DATA FORMAT",
            [nameof(Log_DataDesc1)]         = "Each row = one data frame received from the ESP32 (∼1 Hz).",
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
            [nameof(Log_NoData)]            = "No data yet — connect the ESP32 to see the data stream.",
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
            [nameof(Ui_SerialConnection)]  = "Koneksi Serial",
            [nameof(Ui_SerialQuickAccess)] = "Akses cepat serial",
            [nameof(Ui_TabSerial)]         = "Serial",
            [nameof(Ui_TabBluetooth)]      = "Bluetooth",
            [nameof(Ui_BluetoothConnection)] = "Koneksi Bluetooth",
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
            [nameof(Dash_BalancingStatus)]  = "STATUS PENYEIMBANGAN",
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

            [nameof(Ctrl_SecConnection)]        = "KONEKSI",
            [nameof(Ctrl_SecSerial)]            = "KONEKSI SERIAL",
            [nameof(Ctrl_SerialPort)]        = "Port COM",
            [nameof(Ctrl_PhScanning)]        = "Memindai port…",
            [nameof(Ctrl_PhNoPorts)]         = "Tidak ada port COM terdeteksi",
            [nameof(Ctrl_Refresh)]           = "Perbarui",
            [nameof(Ctrl_Connect)]           = "Hubungkan",
            [nameof(Ctrl_Disconnect)]        = "Putuskan",
            [nameof(Ctrl_SerialBaud)]        = "Baud Rate",
            [nameof(Ctrl_ConnStatus)]        = "Status",
            [nameof(Ctrl_NotConnected)]      = "Tidak terhubung",
            [nameof(Ctrl_AutoConnectStatus)] = "Auto-connect aktif — menunggu data BMS dari ESP32…",
            [nameof(Ctrl_SecCapacity)]       = "KAPASITAS BATERAI",
            [nameof(Ctrl_NominalCapacity)]   = "Kapasitas Nominal",
            [nameof(Ctrl_CapacityHint)]      = "Digunakan untuk menghitung kapasitas sisa (mAh) di dashboard.",
            [nameof(Ctrl_SecProtection)]     = "AMBANG PERLINDUNGAN",
            [nameof(Ctrl_OvervoltCutoff)]    = "Pemutus Tegangan Tinggi",
            [nameof(Ctrl_HighVoltWarn)]      = "Peringatan Tegangan Tinggi",
            [nameof(Ctrl_UnderVoltCutoff)]   = "Pemutus Tegangan Rendah",
            [nameof(Ctrl_LowVoltWarn)]       = "Peringatan Tegangan Rendah",
            [nameof(Ctrl_OverTempWarn)]      = "Peringatan Suhu Tinggi",
            [nameof(Ctrl_OverTempCutoff)]    = "Pemutus Suhu Tinggi",
            [nameof(Ctrl_SecCurrentLimits)]  = "BATAS ARUS",
            [nameof(Ctrl_MaxCharge)]         = "Arus Pengisian Maks",
            [nameof(Ctrl_MaxDischarge)]      = "Arus Pengosongan Maks",
            [nameof(Ctrl_MaxDod)]            = "DoD Maks",
            [nameof(Ctrl_SecBalancing)]      = "PENYEIMBANGAN AKTIF (LTC8584)",
            [nameof(Ctrl_StartDelta)]        = "Delta Mulai",
            [nameof(Ctrl_StopDelta)]         = "Delta Berhenti",
            [nameof(Ctrl_ResetDefaults)]     = "Reset ke Default",
            [nameof(Ctrl_ApplySettings)]     = "Terapkan Pengaturan",

            [nameof(Ctrl_SecSerialAdvanced)]    = "PARAMETER SERIAL",
            [nameof(Ctrl_AutoConnect)]       = "Auto-Hubungkan",
            [nameof(Ctrl_AutoConnectHint)]   = "Memindai port COM secara otomatis dan terhubung ke yang mengirim data BMS.",
            [nameof(Ctrl_ReconnectInterval)] = "Interval Pindai",
            [nameof(Ctrl_ProbeTimeout)]      = "Timeout Verifikasi",
            [nameof(Ctrl_FramesReceived)]    = "Frame diterima",
            [nameof(Ctrl_ParseErrors)]       = "Kesalahan parsing",

            [nameof(Ctrl_SecBluetooth)]      = "KONEKSI BLUETOOTH",
            [nameof(Ctrl_BtDevice)]          = "Perangkat",
            [nameof(Ctrl_BtScan)]            = "Pindai",
            [nameof(Ctrl_BtStopScan)]        = "Berhenti",
            [nameof(Ctrl_BtConnect)]         = "Hubungkan",
            [nameof(Ctrl_BtDisconnect)]      = "Putuskan",
            [nameof(Ctrl_BtPhSelect)]        = "Pilih perangkat…",
            [nameof(Ctrl_BtPhNoDevices)]     = "Belum ada perangkat — tekan Pindai",
            [nameof(Ctrl_BtHint)]            = "Perangkat ESP32 BLE yang menyiarkan Nordic UART Service (NUS) akan muncul di sini. Pasangkan perangkat di Pengaturan Windows terlebih dahulu.",

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
            [nameof(Log_ConnectHint)]       = "Hubungkan ke ESP32 terlebih dahulu, lalu mulai logging.",
            [nameof(Log_RecordingHint)]     = "Perekaman sedang berlangsung. Hentikan untuk menutup / menulis file.",
            [nameof(Log_ReadyHint)]         = "Siap. Tekan Mulai Logging untuk mulai merekam.",
            [nameof(Log_StartLogging)]      = "Mulai Logging",
            [nameof(Log_StopLogging)]       = "Hentikan Logging",
            [nameof(Log_OpenFolder)]        = "Buka Folder",
            [nameof(Log_SecDataFormat)]     = "FORMAT DATA",
            [nameof(Log_DataDesc1)]         = "Setiap baris = satu frame data yang diterima dari ESP32 (∼1 Hz).",
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
            [nameof(Log_NoData)]            = "Belum ada data — hubungkan ESP32 untuk melihat data stream.",
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
        },

        // ── MALAY ─────────────────────────────────────────────────────────
        ["ms"] = new()
        {
            [nameof(Nav_Dashboard)]    = "Dashboard",
            [nameof(Nav_CellView)]     = "Paparan Sel",
            [nameof(Nav_ControlPanel)] = "Panel Kawalan",
            [nameof(Nav_Logging)]      = "Pembalakan",
            [nameof(Nav_Playback)]     = "Main Semula",
            [nameof(Nav_Parameter)]    = "Parameter",
            [nameof(Nav_LiveView)]     = "Paparan Langsung",
            [nameof(Nav_AI)]           = "AI",

            [nameof(Ui_Dark)]          = "GELAP",
            [nameof(Ui_Light)]         = "CERAH",
            [nameof(Ui_SwitchToLight)] = "Tukar ke Mod Cerah",
            [nameof(Ui_SwitchToDark)]  = "Tukar ke Mod Gelap",
            [nameof(Ui_ChangeLanguage)] = "Tukar bahasa",
            [nameof(Ui_SerialConnection)]  = "Sambungan Serial",
            [nameof(Ui_SerialQuickAccess)] = "Akses pantas serial",
            [nameof(Ui_TabSerial)]         = "Serial",
            [nameof(Ui_TabBluetooth)]      = "Bluetooth",
            [nameof(Ui_BluetoothConnection)] = "Sambungan Bluetooth",
            [nameof(Ui_AlertHistory)]   = "Sejarah Amaran",
            [nameof(Ui_NoAlerts)]       = "Tiada amaran lagi",
            [nameof(Ui_ClearAlerts)]    = "Hapus",
            [nameof(Ui_Menu_Customize)]  = "Sesuaikan",
            [nameof(Ui_Menu_View)]       = "Paparan",
            [nameof(Ui_Menu_Unit)]       = "Unit",
            [nameof(Ui_Menu_Temperature)] = "Suhu",
            [nameof(Ui_Menu_Voltage)]    = "Voltan",
            [nameof(Ui_Menu_Capacity)]   = "Kapasiti",
            [nameof(Ui_Menu_About)]      = "Perihal",
            [nameof(Ui_About_Product)]   = "Produk",
            [nameof(Ui_About_Version)]   = "Versi",
            [nameof(Ui_About_License)]   = "Lesen",
            [nameof(Ui_About_Copyright)] = "Hak cipta",
            [nameof(Ui_Menu_Tour)]       = "Tour",
            [nameof(Ui_Menu_RefreshApp)] = "Refresh",
            [nameof(Ui_Menu_Zoom)]       = "Zum",
            [nameof(Ui_Menu_ActualSize)] = "Saiz Sebenar",
            [nameof(Ui_Menu_ZoomIn)]     = "Zum Masuk",
            [nameof(Ui_Menu_ZoomOut)]    = "Zum Keluar",
            [nameof(Ui_Menu_Developer)]     = "Pembangun",
            [nameof(Ui_Menu_OpenLogFolder)] = "Buka Folder Log",
            [nameof(Ui_Menu_OpenSettings)]  = "Buka settings.json",
            [nameof(Ui_Menu_ReportBug)]     = "Laporkan Pepijat…",
            [nameof(Ui_Menu_CheckUpdate)]   = "Semak Kemas Kini…",

            [nameof(Upd_CheckingTitle)]  = "Menyemak Kemas Kini",
            [nameof(Upd_UpToDateTitle)]  = "Versi Terkini",
            [nameof(Upd_UpToDateMsg)]    = "Anda sedang menggunakan versi terkini.",
            [nameof(Upd_AvailableTitle)] = "Kemas Kini Tersedia",
            [nameof(Upd_CurrentVersion)] = "Versi semasa",
            [nameof(Upd_LatestVersion)]  = "Versi terkini",
            [nameof(Upd_ReleaseNotes)]   = "Nota Keluaran",
            [nameof(Upd_Download)]       = "Muat Turun & Guna",
            [nameof(Upd_OpenPage)]       = "Buka Halaman Keluaran",
            [nameof(Upd_Later)]          = "Kemudian",
            [nameof(Upd_Close)]          = "Tutup",
            [nameof(Upd_ErrorTitle)]     = "Semakan Kemas Kini Gagal",
            [nameof(Upd_Downloading)]    = "Memuat Turun Kemas Kini…",
            [nameof(Upd_InstallNow)]     = "Mulakan Semula untuk Guna",
            [nameof(Upd_Extracting)]     = "Menyediakan Kemas Kini…",

            [nameof(Com_Min)]    = "MIN",
            [nameof(Com_Max)]    = "MAKS",
            [nameof(Com_Avg)]    = "PURATA",
            [nameof(Com_Delta)]  = "DELTA",
            [nameof(Com_Status)] = "STATUS",

            [nameof(Dash_SecPackOverview)]  = "RINGKASAN PACK",
            [nameof(Dash_PackVoltage)]      = "VOLTAN PACK",
            [nameof(Dash_PackNominal)]      = "72V nominal",
            [nameof(Dash_StateOfCharge)]    = "STATUS CAS",
            [nameof(Dash_Remaining)]        = "SISA",
            [nameof(Dash_RemainingSub)]     = "berdasarkan SOC × kapasiti",
            [nameof(Dash_SubToEmpty)]       = "{0} hingga kosong",
            [nameof(Dash_SubToFull)]        = "{0} hingga penuh",
            [nameof(Dash_SubIdle)]          = "siaga",
            [nameof(Dash_Current)]          = "ARUS",
            [nameof(Dash_CurrentSub)]       = "+ = mengecas  − = nyahcas",
            [nameof(Dash_PackConfig)]       = "20S4P NMC",
            [nameof(Dash_SecSocHistory)]    = "SEJARAH SOC",
            [nameof(Dash_TimeAgo)]          = "← 2 min lalu",
            [nameof(Dash_Now)]              = "sekarang",
            [nameof(Dash_NowArrow)]         = "sekarang →",
            [nameof(Dash_SecViHistory)]     = "SEJARAH VOLTAN / ARUS",
            [nameof(Dash_VoltageV)]         = "Voltan (V)",
            [nameof(Dash_CurrentA)]         = "Arus (A)",
            [nameof(Dash_SecCellSummary)]   = "RINGKASAN VOLTAN SEL",
            [nameof(Dash_TempSensors)]      = "PENDERIA SUHU",
            [nameof(Dash_BalancingStatus)]  = "STATUS IMBANGAN",
            [nameof(Dash_ActiveCells)]      = "Sel Aktif",
            [nameof(Dash_CellDelta)]        = "Delta Sel",
            [nameof(Dash_Method)]           = "Kaedah",
            [nameof(Dash_ActiveMethod)]     = "Aktif (LTC8584)",
            [nameof(Dash_SecAlertsWarnings)] = "AMARAN & PERINGATAN",
            [nameof(Dash_SaveChart)]        = "Simpan graf sebagai PNG",
            [nameof(Dash_SecTempHistory)]   = "SEJARAH SUHU",
            [nameof(Dash_TempC)]            = "°C",
            [nameof(Dash_TempF)]            = "°F",

            [nameof(Cell_SecVoltageSummary)] = "RINGKASAN VOLTAN",
            [nameof(Cell_SecCellGrid)]       = "GRID SEL — 20S4P",
            [nameof(Cell_DeltaLabel)]        = "Δ DELTA",
            [nameof(Cell_Normal)]            = "Normal",
            [nameof(Cell_Low)]               = "Rendah",
            [nameof(Cell_Undervoltage)]      = "Voltan Rendah",
            [nameof(Cell_Overvoltage)]       = "Voltan Tinggi",
            [nameof(Cell_Balancing)]         = "Pengimbangan",
            [nameof(Cell_SecNtcReadings)]    = "BACAAN TERMISTOR NTC",
            [nameof(Cell_Thresholds)]        = "AMBANG",
            [nameof(Cell_ThreshWarn)]        = "Amaran :  60 °C",
            [nameof(Cell_ThreshCutoff)]      = "Pemutus  :  70 °C",
            [nameof(Cell_Legend)]            = "LEGENDA",
            [nameof(Cell_NormalDesc)]        = "Normal  (di bawah 60°C)",
            [nameof(Cell_WarnDesc)]          = "Amaran (60 – 70°C)",
            [nameof(Cell_CutoffDesc)]        = "Pemutus  (di atas 70°C)",
            [nameof(Cell_ResetStats)]        = "Tetapkan Semula Statistik",

            [nameof(Ctrl_SecConnection)]        = "SAMBUNGAN",
            [nameof(Ctrl_SecSerial)]            = "SAMBUNGAN SERIAL",
            [nameof(Ctrl_SerialPort)]        = "Port COM",
            [nameof(Ctrl_PhScanning)]        = "Mengimbas port…",
            [nameof(Ctrl_PhNoPorts)]         = "Tiada port COM dikesan",
            [nameof(Ctrl_Refresh)]           = "Muat Semula",
            [nameof(Ctrl_Connect)]           = "Sambungkan",
            [nameof(Ctrl_Disconnect)]        = "Putuskan",
            [nameof(Ctrl_SerialBaud)]        = "Kadar Baud",
            [nameof(Ctrl_ConnStatus)]        = "Status",
            [nameof(Ctrl_NotConnected)]      = "Tidak disambungkan",
            [nameof(Ctrl_AutoConnectStatus)] = "Auto-sambung aktif — menunggu data BMS dari ESP32…",
            [nameof(Ctrl_SecCapacity)]       = "KAPASITI BATERI",
            [nameof(Ctrl_NominalCapacity)]   = "Kapasiti Nominal",
            [nameof(Ctrl_CapacityHint)]      = "Digunakan untuk mengira kapasiti sisa (mAh) di papan pemuka.",
            [nameof(Ctrl_SecProtection)]     = "AMBANG PERLINDUNGAN",
            [nameof(Ctrl_OvervoltCutoff)]    = "Pemutus Voltan Tinggi",
            [nameof(Ctrl_HighVoltWarn)]      = "Amaran Voltan Tinggi",
            [nameof(Ctrl_UnderVoltCutoff)]   = "Pemutus Voltan Rendah",
            [nameof(Ctrl_LowVoltWarn)]       = "Amaran Voltan Rendah",
            [nameof(Ctrl_OverTempWarn)]      = "Amaran Suhu Tinggi",
            [nameof(Ctrl_OverTempCutoff)]    = "Pemutus Suhu Tinggi",
            [nameof(Ctrl_SecCurrentLimits)]  = "HAD ARUS",
            [nameof(Ctrl_MaxCharge)]         = "Arus Pengecasan Maks",
            [nameof(Ctrl_MaxDischarge)]      = "Arus Nyahcas Maks",
            [nameof(Ctrl_MaxDod)]            = "DoD Maks",
            [nameof(Ctrl_SecBalancing)]      = "PENGIMBANGAN AKTIF (LTC8584)",
            [nameof(Ctrl_StartDelta)]        = "Delta Mula",
            [nameof(Ctrl_StopDelta)]         = "Delta Berhenti",
            [nameof(Ctrl_ResetDefaults)]     = "Tetapkan Semula",
            [nameof(Ctrl_ApplySettings)]     = "Guna Tetapan",

            [nameof(Ctrl_SecSerialAdvanced)]    = "PARAMETER SERIAL",
            [nameof(Ctrl_AutoConnect)]       = "Auto-Sambung",
            [nameof(Ctrl_AutoConnectHint)]   = "Mengimbas port COM secara automatik dan menyambung ke port yang menghantar data BMS.",
            [nameof(Ctrl_ReconnectInterval)] = "Selang Imbasan",
            [nameof(Ctrl_ProbeTimeout)]      = "Tamat Masa Pemeriksaan",
            [nameof(Ctrl_FramesReceived)]    = "Bingkai diterima",
            [nameof(Ctrl_ParseErrors)]       = "Ralat penghuraian",

            [nameof(Ctrl_SecBluetooth)]      = "SAMBUNGAN BLUETOOTH",
            [nameof(Ctrl_BtDevice)]          = "Peranti",
            [nameof(Ctrl_BtScan)]            = "Imbas",
            [nameof(Ctrl_BtStopScan)]        = "Berhenti",
            [nameof(Ctrl_BtConnect)]         = "Sambung",
            [nameof(Ctrl_BtDisconnect)]      = "Putus",
            [nameof(Ctrl_BtPhSelect)]        = "Pilih peranti…",
            [nameof(Ctrl_BtPhNoDevices)]     = "Tiada peranti lagi — tekan Imbas",
            [nameof(Ctrl_BtHint)]            = "Peranti ESP32 BLE yang menyiarkan Nordic UART Service (NUS) akan muncul di sini. Gandingkan peranti dalam Tetapan Windows terlebih dahulu.",

            [nameof(Fb_SerialError)]            = "Ralat Serial",
            [nameof(Fb_SelectChannel)]       = "Pilih port",
            [nameof(Fb_SelectChannelMsg)]    = "Pilih port COM dari senarai juntai bawah terlebih dahulu.",
            [nameof(Fb_SettingsApplied)]     = "Tetapan digunakan",
            [nameof(Fb_SettingsAppliedMsg)]  = "Ambang baru adalah aktif.",
            [nameof(Fb_DefaultsRestored)]    = "Tetapan asal dipulihkan",
            [nameof(Fb_DefaultsRestoredMsg)] = "Nilai diset semula — klik Guna untuk mengaktifkan.",

            [nameof(Log_SecStatus)]         = "STATUS PEMBALAKAN",
            [nameof(Log_State)]             = "STATUS",
            [nameof(Log_Samples)]           = "SAMPEL",
            [nameof(Log_Duration)]          = "TEMPOH",
            [nameof(Log_SecFileSettings)]   = "TETAPAN FAIL",
            [nameof(Log_Folder)]            = "Folder",
            [nameof(Log_Browse)]            = "Semak Imbas…",
            [nameof(Log_Format)]            = "Format",
            [nameof(Log_Filename)]          = "Nama Fail",
            [nameof(Log_PhAutoFilename)]    = "dijana secara automatik jika kosong",
            [nameof(Log_SecControls)]       = "KAWALAN",
            [nameof(Log_ConnectHint)]       = "Sambung ke ESP32 terlebih dahulu, kemudian mulakan pembalakan.",
            [nameof(Log_RecordingHint)]     = "Rakaman sedang berlangsung. Hentikan untuk menutup / menulis fail.",
            [nameof(Log_ReadyHint)]         = "Sedia. Tekan Mula Pembalakan untuk mula merakam.",
            [nameof(Log_StartLogging)]      = "Mula Pembalakan",
            [nameof(Log_StopLogging)]       = "Hentikan Pembalakan",
            [nameof(Log_OpenFolder)]        = "Buka Folder",
            [nameof(Log_SecDataFormat)]     = "FORMAT DATA",
            [nameof(Log_DataDesc1)]         = "Setiap baris = satu bingkai data yang diterima dari ESP32 (∼1 Hz).",
            [nameof(Log_DataDesc2)]         = "Kolom: Timestamp · PackVoltage_V · SOC_pct · Current_A · Status · Cell1_V … Cell20_V · Bal1 … Bal20 · Temp1_C … Temp10_C",
            [nameof(Log_DataDesc3)]         = "CSV / TSV: disalirkan ke cakera setiap bingkai.  Excel / JSON: ditimbal dalam memori dan ditulis apabila anda tekan Stop.",
            [nameof(Log_SecLiveData)]       = "DATA LANGSUNG (20 TERBARU)",
            [nameof(Log_HdrTimestamp)]      = "TIMESTAMP",
            [nameof(Log_HdrSoc)]            = "SOC %",
            [nameof(Log_HdrPackV)]          = "PACK V",
            [nameof(Log_HdrCurrentA)]       = "ARUS A",
            [nameof(Log_HdrStatus)]         = "STATUS",
            [nameof(Log_HdrMinCell)]        = "MIN SEL V",
            [nameof(Log_HdrMaxCell)]        = "MAKS SEL V",
            [nameof(Log_HdrDeltaMv)]        = "DELTA mV",
            [nameof(Log_HdrBalCells)]       = "SEL SEIMBANG",
            [nameof(Log_NoData)]            = "Tiada data lagi — sambungkan ESP32 untuk melihat aliran data.",
            [nameof(Log_Idle)]              = "Diam",
            [nameof(Log_Logging)]           = "Merakam",

            [nameof(Pb_SecLoadFile)]   = "MUAT FAIL",
            [nameof(Pb_NoFileLoaded)]  = "Tiada fail dimuatkan",
            [nameof(Pb_Browse)]        = "Semak Imbas…",
            [nameof(Pb_Unload)]        = "Buang",
            [nameof(Pb_LoadStatus)]    = "Semak imbas dan buka fail log CSV TLIG Dashboard (.csv).",
            [nameof(Pb_SecFileInfo)]   = "INFO FAIL",
            [nameof(Pb_Frames)]        = "BINGKAI",
            [nameof(Pb_EstDuration)]   = "ANGGARAN TEMPOH",
            [nameof(Pb_PlaybackSpeed)] = "KELAJUAN MAIN SEMULA",
            [nameof(Pb_SecHowToUse)]   = "CARA MENGGUNAKAN",
            [nameof(Pb_HowToUse1)]     = "Semak imbas dan muat fail CSV di atas, kemudian gunakan bar main semula yang muncul di bahagian bawah tetingkap.",
            [nameof(Pb_HowToUse2)]     = "Semasa dimainkan, semua halaman dikemas kini secara masa nyata. Pembalakan dijeda secara automatik semasa main balik. Klik ✕ di bar main semula atau tekan Buang untuk kembali ke mod langsung.",
        },

        // ── NEDERLANDS ────────────────────────────────────────────────────
        ["nl"] = new()
        {
            [nameof(Nav_Dashboard)]    = "Dashboard",
            [nameof(Nav_CellView)]     = "Celweergave",
            [nameof(Nav_ControlPanel)] = "Configuratiescherm",
            [nameof(Nav_Logging)]      = "Logboek",
            [nameof(Nav_Playback)]     = "Afspelen",
            [nameof(Nav_Parameter)]    = "Parameter",
            [nameof(Nav_LiveView)]     = "Live weergave",
            [nameof(Nav_AI)]           = "AI",

            [nameof(Ui_Dark)]          = "DONKER",
            [nameof(Ui_Light)]         = "LICHT",
            [nameof(Ui_SwitchToLight)] = "Overschakelen naar lichte modus",
            [nameof(Ui_SwitchToDark)]  = "Overschakelen naar donkere modus",
            [nameof(Ui_ChangeLanguage)] = "Taal wijzigen",
            [nameof(Ui_SerialConnection)]  = "Seriële verbinding",
            [nameof(Ui_SerialQuickAccess)] = "Snelle seriële toegang",
            [nameof(Ui_TabSerial)]         = "Serieel",
            [nameof(Ui_TabBluetooth)]      = "Bluetooth",
            [nameof(Ui_BluetoothConnection)] = "Bluetooth-verbinding",
            [nameof(Ui_AlertHistory)]   = "Waarschuwingslog",
            [nameof(Ui_NoAlerts)]       = "Geen waarschuwingen",
            [nameof(Ui_ClearAlerts)]    = "Wissen",
            [nameof(Ui_Menu_Customize)]  = "Aanpassen",
            [nameof(Ui_Menu_View)]       = "Weergave",
            [nameof(Ui_Menu_Unit)]       = "Eenheden",
            [nameof(Ui_Menu_Temperature)] = "Temperatuur",
            [nameof(Ui_Menu_Voltage)]    = "Spanning",
            [nameof(Ui_Menu_Capacity)]   = "Capaciteit",
            [nameof(Ui_Menu_About)]      = "Info",
            [nameof(Ui_About_Product)]   = "Product",
            [nameof(Ui_About_Version)]   = "Versie",
            [nameof(Ui_About_License)]   = "Licentie",
            [nameof(Ui_About_Copyright)] = "Copyright",
            [nameof(Ui_Menu_Tour)]       = "Rondleiding",
            [nameof(Ui_Menu_RefreshApp)] = "Refresh",
            [nameof(Ui_Menu_Zoom)]       = "Zoomen",
            [nameof(Ui_Menu_ActualSize)] = "Werkelijke grootte",
            [nameof(Ui_Menu_ZoomIn)]     = "Inzoomen",
            [nameof(Ui_Menu_ZoomOut)]    = "Uitzoomen",
            [nameof(Ui_Menu_Developer)]     = "Ontwikkelaar",
            [nameof(Ui_Menu_OpenLogFolder)] = "Logmap openen",
            [nameof(Ui_Menu_OpenSettings)]  = "settings.json openen",
            [nameof(Ui_Menu_ReportBug)]     = "Bug melden…",
            [nameof(Ui_Menu_CheckUpdate)]   = "Controleren op updates…",

            [nameof(Upd_CheckingTitle)]  = "Controleren op updates",
            [nameof(Upd_UpToDateTitle)]  = "Up-to-date",
            [nameof(Upd_UpToDateMsg)]    = "U gebruikt de nieuwste versie.",
            [nameof(Upd_AvailableTitle)] = "Update beschikbaar",
            [nameof(Upd_CurrentVersion)] = "Huidige versie",
            [nameof(Upd_LatestVersion)]  = "Nieuwste versie",
            [nameof(Upd_ReleaseNotes)]   = "Release-opmerkingen",
            [nameof(Upd_Download)]       = "Downloaden & toepassen",
            [nameof(Upd_OpenPage)]       = "Release-pagina openen",
            [nameof(Upd_Later)]          = "Later",
            [nameof(Upd_Close)]          = "Sluiten",
            [nameof(Upd_ErrorTitle)]     = "Update-controle mislukt",
            [nameof(Upd_Downloading)]    = "Update downloaden…",
            [nameof(Upd_InstallNow)]     = "Opnieuw opstarten",
            [nameof(Upd_Extracting)]     = "Update voorbereiden…",

            [nameof(Com_Min)]    = "MIN",
            [nameof(Com_Max)]    = "MAX",
            [nameof(Com_Avg)]    = "GEM",
            [nameof(Com_Delta)]  = "DELTA",
            [nameof(Com_Status)] = "STATUS",

            [nameof(Dash_SecPackOverview)]  = "PACK OVERZICHT",
            [nameof(Dash_PackVoltage)]      = "PACK SPANNING",
            [nameof(Dash_PackNominal)]      = "72V nominaal",
            [nameof(Dash_StateOfCharge)]    = "LAADTOESTAND",
            [nameof(Dash_Remaining)]        = "RESTEREND",
            [nameof(Dash_RemainingSub)]     = "gebaseerd op SOC × capaciteit",
            [nameof(Dash_SubToEmpty)]       = "nog {0} tot leeg",
            [nameof(Dash_SubToFull)]        = "nog {0} tot vol",
            [nameof(Dash_SubIdle)]          = "inactief",
            [nameof(Dash_Current)]          = "STROOM",
            [nameof(Dash_CurrentSub)]       = "+ = laden  − = ontladen",
            [nameof(Dash_PackConfig)]       = "20S4P NMC",
            [nameof(Dash_SecSocHistory)]    = "SOC GESCHIEDENIS",
            [nameof(Dash_TimeAgo)]          = "← 2 min geleden",
            [nameof(Dash_Now)]              = "nu",
            [nameof(Dash_NowArrow)]         = "nu →",
            [nameof(Dash_SecViHistory)]     = "SPANNING / STROOM GESCHIEDENIS",
            [nameof(Dash_VoltageV)]         = "Spanning (V)",
            [nameof(Dash_CurrentA)]         = "Stroom (A)",
            [nameof(Dash_SecCellSummary)]   = "CEL SPANNING OVERZICHT",
            [nameof(Dash_TempSensors)]      = "TEMPERATUURSENSOREN",
            [nameof(Dash_BalancingStatus)]  = "BALANCEERSTATUS",
            [nameof(Dash_ActiveCells)]      = "Actieve Cellen",
            [nameof(Dash_CellDelta)]        = "Cel Delta",
            [nameof(Dash_Method)]           = "Methode",
            [nameof(Dash_ActiveMethod)]     = "Actief (LTC8584)",
            [nameof(Dash_SecAlertsWarnings)] = "ALARMEN & WAARSCHUWINGEN",
            [nameof(Dash_SaveChart)]        = "Grafiek opslaan als PNG",
            [nameof(Dash_SecTempHistory)]   = "TEMPERATUUR GESCHIEDENIS",
            [nameof(Dash_TempC)]            = "°C",
            [nameof(Dash_TempF)]            = "°F",

            [nameof(Cell_SecVoltageSummary)] = "SPANNING OVERZICHT",
            [nameof(Cell_SecCellGrid)]       = "CEL RASTER — 20S4P",
            [nameof(Cell_DeltaLabel)]        = "Δ DELTA",
            [nameof(Cell_Normal)]            = "Normaal",
            [nameof(Cell_Low)]               = "Laag",
            [nameof(Cell_Undervoltage)]      = "Onderspanning",
            [nameof(Cell_Overvoltage)]       = "Overspanning",
            [nameof(Cell_Balancing)]         = "Balanceren",
            [nameof(Cell_SecNtcReadings)]    = "NTC THERMISTOR METINGEN",
            [nameof(Cell_Thresholds)]        = "DREMPELWAARDEN",
            [nameof(Cell_ThreshWarn)]        = "Waarschuwing :  60 °C",
            [nameof(Cell_ThreshCutoff)]      = "Beveiliging  :  70 °C",
            [nameof(Cell_Legend)]            = "LEGENDA",
            [nameof(Cell_NormalDesc)]        = "Normaal  (onder 60°C)",
            [nameof(Cell_WarnDesc)]          = "Waarschuwing (60 – 70°C)",
            [nameof(Cell_CutoffDesc)]        = "Beveiliging  (boven 70°C)",
            [nameof(Cell_ResetStats)]        = "Statistieken resetten",

            [nameof(Ctrl_SecConnection)]        = "VERBINDING",
            [nameof(Ctrl_SecSerial)]            = "SERIËLE VERBINDING",
            [nameof(Ctrl_SerialPort)]        = "COM-poort",
            [nameof(Ctrl_PhScanning)]        = "Poorten scannen…",
            [nameof(Ctrl_PhNoPorts)]         = "Geen COM-poorten gevonden",
            [nameof(Ctrl_Refresh)]           = "Vernieuwen",
            [nameof(Ctrl_Connect)]           = "Verbinden",
            [nameof(Ctrl_Disconnect)]        = "Verbreken",
            [nameof(Ctrl_SerialBaud)]        = "Baudrate",
            [nameof(Ctrl_ConnStatus)]        = "Status",
            [nameof(Ctrl_NotConnected)]      = "Niet verbonden",
            [nameof(Ctrl_AutoConnectStatus)] = "Automatisch verbinden actief — wacht op BMS-data van ESP32…",
            [nameof(Ctrl_SecCapacity)]       = "BATTERIJCAPACITEIT",
            [nameof(Ctrl_NominalCapacity)]   = "Nominale Capaciteit",
            [nameof(Ctrl_CapacityHint)]      = "Gebruikt om de resterende capaciteit (mAh) op het dashboard te berekenen.",
            [nameof(Ctrl_SecProtection)]     = "BESCHERMINGSDREMPELS",
            [nameof(Ctrl_OvervoltCutoff)]    = "Overspanningsbeveiliging",
            [nameof(Ctrl_HighVoltWarn)]      = "Hoogspanningswaarschuwing",
            [nameof(Ctrl_UnderVoltCutoff)]   = "Onderspanningsbeveiliging",
            [nameof(Ctrl_LowVoltWarn)]       = "Laagspanningswaarschuwing",
            [nameof(Ctrl_OverTempWarn)]      = "Temperatuurwaarschuwing",
            [nameof(Ctrl_OverTempCutoff)]    = "Temperatuurbeveiliging",
            [nameof(Ctrl_SecCurrentLimits)]  = "STROOMLIMIETEN",
            [nameof(Ctrl_MaxCharge)]         = "Max. Laadstroom",
            [nameof(Ctrl_MaxDischarge)]      = "Max. Ontlaadstroom",
            [nameof(Ctrl_MaxDod)]            = "Max. DoD",
            [nameof(Ctrl_SecBalancing)]      = "ACTIEF BALANCEREN (LTC8584)",
            [nameof(Ctrl_StartDelta)]        = "Start Delta",
            [nameof(Ctrl_StopDelta)]         = "Stop Delta",
            [nameof(Ctrl_ResetDefaults)]     = "Standaard herstellen",
            [nameof(Ctrl_ApplySettings)]     = "Instellingen toepassen",

            [nameof(Ctrl_SecSerialAdvanced)]    = "SERIËLE PARAMETERS",
            [nameof(Ctrl_AutoConnect)]       = "Automatisch verbinden",
            [nameof(Ctrl_AutoConnectHint)]   = "Scan automatisch COM-poorten en maak verbinding met de poort die BMS-data verstuurt.",
            [nameof(Ctrl_ReconnectInterval)] = "Scan-interval",
            [nameof(Ctrl_ProbeTimeout)]      = "Detectie-timeout",
            [nameof(Ctrl_FramesReceived)]    = "Frames ontvangen",
            [nameof(Ctrl_ParseErrors)]       = "Parse-fouten",

            [nameof(Ctrl_SecBluetooth)]      = "BLUETOOTH-VERBINDING",
            [nameof(Ctrl_BtDevice)]          = "Apparaat",
            [nameof(Ctrl_BtScan)]            = "Scannen",
            [nameof(Ctrl_BtStopScan)]        = "Stoppen",
            [nameof(Ctrl_BtConnect)]         = "Verbinden",
            [nameof(Ctrl_BtDisconnect)]      = "Verbreken",
            [nameof(Ctrl_BtPhSelect)]        = "Selecteer een apparaat…",
            [nameof(Ctrl_BtPhNoDevices)]     = "Nog geen apparaten — klik Scannen",
            [nameof(Ctrl_BtHint)]            = "ESP32 BLE-apparaten die de Nordic UART Service (NUS) adverteren verschijnen hier. Koppel het apparaat eerst in Windows-instellingen.",

            [nameof(Fb_SerialError)]            = "Seriële fout",
            [nameof(Fb_SelectChannel)]       = "Selecteer een poort",
            [nameof(Fb_SelectChannelMsg)]    = "Selecteer eerst een COM-poort uit de vervolgkeuzelijst.",
            [nameof(Fb_SettingsApplied)]     = "Instellingen toegepast",
            [nameof(Fb_SettingsAppliedMsg)]  = "Nieuwe drempelwaarden zijn actief.",
            [nameof(Fb_DefaultsRestored)]    = "Standaard hersteld",
            [nameof(Fb_DefaultsRestoredMsg)] = "Waarden gereset — klik Toepassen om te activeren.",

            [nameof(Log_SecStatus)]         = "LOGBOEKSTATUS",
            [nameof(Log_State)]             = "STATUS",
            [nameof(Log_Samples)]           = "MONSTERS",
            [nameof(Log_Duration)]          = "DUUR",
            [nameof(Log_SecFileSettings)]   = "BESTANDSINSTELLINGEN",
            [nameof(Log_Folder)]            = "Map",
            [nameof(Log_Browse)]            = "Bladeren…",
            [nameof(Log_Format)]            = "Formaat",
            [nameof(Log_Filename)]          = "Bestandsnaam",
            [nameof(Log_PhAutoFilename)]    = "automatisch gegenereerd als leeg",
            [nameof(Log_SecControls)]       = "BEDIENINGSMIDDELEN",
            [nameof(Log_ConnectHint)]       = "Verbind eerst met de ESP32 en start dan met loggen.",
            [nameof(Log_RecordingHint)]     = "Opname bezig. Stop om het bestand te sluiten / schrijven.",
            [nameof(Log_ReadyHint)]         = "Klaar. Druk op Loggen starten om te beginnen.",
            [nameof(Log_StartLogging)]      = "Loggen starten",
            [nameof(Log_StopLogging)]       = "Loggen stoppen",
            [nameof(Log_OpenFolder)]        = "Map openen",
            [nameof(Log_SecDataFormat)]     = "GEGEVENSFORMAAT",
            [nameof(Log_DataDesc1)]         = "Elke rij = één gegevensframe ontvangen van de ESP32 (∼1 Hz).",
            [nameof(Log_DataDesc2)]         = "Velden: Timestamp · PackVoltage_V · SOC_pct · Current_A · Status · Cell1_V … Cell20_V · Bal1 … Bal20 · Temp1_C … Temp10_C",
            [nameof(Log_DataDesc3)]         = "CSV / TSV: elk frame naar schijf gestreamd.  Excel / JSON: gebufferd in geheugen en geschreven wanneer u op Stoppen drukt.",
            [nameof(Log_SecLiveData)]       = "LIVE DATA (LAATSTE 20)",
            [nameof(Log_HdrTimestamp)]      = "TIJDSTEMPEL",
            [nameof(Log_HdrSoc)]            = "SOC %",
            [nameof(Log_HdrPackV)]          = "PACK V",
            [nameof(Log_HdrCurrentA)]       = "STROOM A",
            [nameof(Log_HdrStatus)]         = "STATUS",
            [nameof(Log_HdrMinCell)]        = "MIN CEL V",
            [nameof(Log_HdrMaxCell)]        = "MAX CEL V",
            [nameof(Log_HdrDeltaMv)]        = "DELTA mV",
            [nameof(Log_HdrBalCells)]       = "BAL CELLEN",
            [nameof(Log_NoData)]            = "Nog geen data — verbind de ESP32 om de datastroom te zien.",
            [nameof(Log_Idle)]              = "Inactief",
            [nameof(Log_Logging)]           = "Loggen",

            [nameof(Pb_SecLoadFile)]   = "BESTAND LADEN",
            [nameof(Pb_NoFileLoaded)]  = "Geen bestand geladen",
            [nameof(Pb_Browse)]        = "Bladeren…",
            [nameof(Pb_Unload)]        = "Verwijderen",
            [nameof(Pb_LoadStatus)]    = "Blader naar en open een TLIG Dashboard CSV-logbestand (.csv).",
            [nameof(Pb_SecFileInfo)]   = "BESTANDSINFO",
            [nameof(Pb_Frames)]        = "FRAMES",
            [nameof(Pb_EstDuration)]   = "GESCHATTE DUUR",
            [nameof(Pb_PlaybackSpeed)] = "AFSPEELSNELHEID",
            [nameof(Pb_SecHowToUse)]   = "HOE TE GEBRUIKEN",
            [nameof(Pb_HowToUse1)]     = "Blader en laad een CSV-bestand hierboven, gebruik dan de afspeelbalk die onderaan het venster verschijnt.",
            [nameof(Pb_HowToUse2)]     = "Tijdens afspelen worden alle pagina's real-time bijgewerkt met de opgenomen data. Loggen wordt automatisch gepauzeerd. Klik ✕ in de afspeelbalk of druk op Verwijderen om terug te keren naar de live modus.",
        },

        // ── CHINESE (Simplified) ──────────────────────────────────────────
        ["zh"] = new()
        {
            [nameof(Nav_Dashboard)]    = "仪表盘",
            [nameof(Nav_CellView)]     = "电池格",
            [nameof(Nav_ControlPanel)] = "控制面板",
            [nameof(Nav_Logging)]      = "数据记录",
            [nameof(Nav_Playback)]     = "回放",
            [nameof(Nav_Parameter)]    = "参数",
            [nameof(Nav_LiveView)]     = "实时视图",
            [nameof(Nav_AI)]           = "AI",

            [nameof(Ui_Dark)]          = "深色",
            [nameof(Ui_Light)]         = "浅色",
            [nameof(Ui_SwitchToLight)] = "切换到浅色模式",
            [nameof(Ui_SwitchToDark)]  = "切换到深色模式",
            [nameof(Ui_ChangeLanguage)] = "更改语言",
            [nameof(Ui_SerialConnection)]  = "串口连接",
            [nameof(Ui_SerialQuickAccess)] = "串口快速访问",
            [nameof(Ui_TabSerial)]         = "串口",
            [nameof(Ui_TabBluetooth)]      = "蓝牙",
            [nameof(Ui_BluetoothConnection)] = "蓝牙连接",
            [nameof(Ui_AlertHistory)]   = "警报历史",
            [nameof(Ui_NoAlerts)]       = "暂无警报",
            [nameof(Ui_ClearAlerts)]    = "清除",
            [nameof(Ui_Menu_Customize)]  = "自定义",
            [nameof(Ui_Menu_View)]       = "视图",
            [nameof(Ui_Menu_Unit)]       = "单位",
            [nameof(Ui_Menu_Temperature)] = "温度",
            [nameof(Ui_Menu_Voltage)]    = "电压",
            [nameof(Ui_Menu_Capacity)]   = "容量",
            [nameof(Ui_Menu_About)]      = "关于",
            [nameof(Ui_About_Product)]   = "产品",
            [nameof(Ui_About_Version)]   = "版本",
            [nameof(Ui_About_License)]   = "许可",
            [nameof(Ui_About_Copyright)] = "版权",
            [nameof(Ui_Menu_Tour)]       = "导览",
            [nameof(Ui_Menu_RefreshApp)] = "Refresh",
            [nameof(Ui_Menu_Zoom)]       = "缩放",
            [nameof(Ui_Menu_ActualSize)] = "实际大小",
            [nameof(Ui_Menu_ZoomIn)]     = "放大",
            [nameof(Ui_Menu_ZoomOut)]    = "缩小",
            [nameof(Ui_Menu_Developer)]     = "开发者",
            [nameof(Ui_Menu_OpenLogFolder)] = "打开日志文件夹",
            [nameof(Ui_Menu_OpenSettings)]  = "打开 settings.json",
            [nameof(Ui_Menu_ReportBug)]     = "报告问题…",
            [nameof(Ui_Menu_CheckUpdate)]   = "检查更新…",

            [nameof(Upd_CheckingTitle)]  = "正在检查更新",
            [nameof(Upd_UpToDateTitle)]  = "已是最新版本",
            [nameof(Upd_UpToDateMsg)]    = "您正在运行最新版本。",
            [nameof(Upd_AvailableTitle)] = "有可用更新",
            [nameof(Upd_CurrentVersion)] = "当前版本",
            [nameof(Upd_LatestVersion)]  = "最新版本",
            [nameof(Upd_ReleaseNotes)]   = "版本说明",
            [nameof(Upd_Download)]       = "下载并应用",
            [nameof(Upd_OpenPage)]       = "打开发布页面",
            [nameof(Upd_Later)]          = "稍后",
            [nameof(Upd_Close)]          = "关闭",
            [nameof(Upd_ErrorTitle)]     = "检查更新失败",
            [nameof(Upd_Downloading)]    = "正在下载更新…",
            [nameof(Upd_InstallNow)]     = "重启以应用",
            [nameof(Upd_Extracting)]     = "正在准备更新…",

            [nameof(Com_Min)]    = "最低",
            [nameof(Com_Max)]    = "最高",
            [nameof(Com_Avg)]    = "均值",
            [nameof(Com_Delta)]  = "差值",
            [nameof(Com_Status)] = "状态",

            [nameof(Dash_SecPackOverview)]  = "电池组概览",
            [nameof(Dash_PackVoltage)]      = "组电压",
            [nameof(Dash_PackNominal)]      = "72V 额定",
            [nameof(Dash_StateOfCharge)]    = "电量状态",
            [nameof(Dash_Remaining)]        = "剩余",
            [nameof(Dash_RemainingSub)]     = "基于 SOC × 容量",
            [nameof(Dash_SubToEmpty)]       = "剩余 {0}",
            [nameof(Dash_SubToFull)]        = "充满需 {0}",
            [nameof(Dash_SubIdle)]          = "空闲",
            [nameof(Dash_Current)]          = "电流",
            [nameof(Dash_CurrentSub)]       = "+ = 充电  − = 放电",
            [nameof(Dash_PackConfig)]       = "20S4P NMC",
            [nameof(Dash_SecSocHistory)]    = "SOC 历史",
            [nameof(Dash_TimeAgo)]          = "← 2 分钟前",
            [nameof(Dash_Now)]              = "现在",
            [nameof(Dash_NowArrow)]         = "现在 →",
            [nameof(Dash_SecViHistory)]     = "电压 / 电流历史",
            [nameof(Dash_VoltageV)]         = "电压 (V)",
            [nameof(Dash_CurrentA)]         = "电流 (A)",
            [nameof(Dash_SecCellSummary)]   = "电池格电压摘要",
            [nameof(Dash_TempSensors)]      = "温度传感器",
            [nameof(Dash_BalancingStatus)]  = "均衡状态",
            [nameof(Dash_ActiveCells)]      = "活跃单元",
            [nameof(Dash_CellDelta)]        = "电压差",
            [nameof(Dash_Method)]           = "方法",
            [nameof(Dash_ActiveMethod)]     = "主动 (LTC8584)",
            [nameof(Dash_SecAlertsWarnings)] = "警报与警告",
            [nameof(Dash_SaveChart)]        = "保存图表为 PNG",
            [nameof(Dash_SecTempHistory)]   = "温度历史",
            [nameof(Dash_TempC)]            = "°C",
            [nameof(Dash_TempF)]            = "°F",

            [nameof(Cell_SecVoltageSummary)] = "电压摘要",
            [nameof(Cell_SecCellGrid)]       = "电池格阵列 — 20S4P",
            [nameof(Cell_DeltaLabel)]        = "Δ 差值",
            [nameof(Cell_Normal)]            = "正常",
            [nameof(Cell_Low)]               = "偏低",
            [nameof(Cell_Undervoltage)]      = "欠压",
            [nameof(Cell_Overvoltage)]       = "过压",
            [nameof(Cell_Balancing)]         = "均衡中",
            [nameof(Cell_SecNtcReadings)]    = "NTC 热敏电阔读数",
            [nameof(Cell_Thresholds)]        = "阈值",
            [nameof(Cell_ThreshWarn)]        = "警告 :  60 °C",
            [nameof(Cell_ThreshCutoff)]      = "断路 :  70 °C",
            [nameof(Cell_Legend)]            = "图例",
            [nameof(Cell_NormalDesc)]        = "正常  (低于 60°C)",
            [nameof(Cell_WarnDesc)]          = "警告 (60 – 70°C)",
            [nameof(Cell_CutoffDesc)]        = "断路 (高于 70°C)",
            [nameof(Cell_ResetStats)]        = "重置统计",

            [nameof(Ctrl_SecConnection)]        = "连接",
            [nameof(Ctrl_SecSerial)]            = "串口连接",
            [nameof(Ctrl_SerialPort)]        = "COM 端口",
            [nameof(Ctrl_PhScanning)]        = "正在扫描端口…",
            [nameof(Ctrl_PhNoPorts)]         = "未检测到 COM 端口",
            [nameof(Ctrl_Refresh)]           = "刷新",
            [nameof(Ctrl_Connect)]           = "连接",
            [nameof(Ctrl_Disconnect)]        = "断开",
            [nameof(Ctrl_SerialBaud)]        = "波特率",
            [nameof(Ctrl_ConnStatus)]        = "状态",
            [nameof(Ctrl_NotConnected)]      = "未连接",
            [nameof(Ctrl_AutoConnectStatus)] = "自动连接已启动 — 等待 ESP32 BMS 数据…",
            [nameof(Ctrl_SecCapacity)]       = "电池容量",
            [nameof(Ctrl_NominalCapacity)]   = "额定容量",
            [nameof(Ctrl_CapacityHint)]      = "用于计算仪表盘上的剩余容量 (mAh)。",
            [nameof(Ctrl_SecProtection)]     = "保护阈值",
            [nameof(Ctrl_OvervoltCutoff)]    = "过压截止",
            [nameof(Ctrl_HighVoltWarn)]      = "过压警告",
            [nameof(Ctrl_UnderVoltCutoff)]   = "欠压截止",
            [nameof(Ctrl_LowVoltWarn)]       = "低压警告",
            [nameof(Ctrl_OverTempWarn)]      = "过温警告",
            [nameof(Ctrl_OverTempCutoff)]    = "过温截止",
            [nameof(Ctrl_SecCurrentLimits)]  = "电流限制",
            [nameof(Ctrl_MaxCharge)]         = "最大充电电流",
            [nameof(Ctrl_MaxDischarge)]      = "最大放电电流",
            [nameof(Ctrl_MaxDod)]            = "最大放电深度",
            [nameof(Ctrl_SecBalancing)]      = "主动均衡 (LTC8584)",
            [nameof(Ctrl_StartDelta)]        = "启动差值",
            [nameof(Ctrl_StopDelta)]         = "停止差值",
            [nameof(Ctrl_ResetDefaults)]     = "恢复默认",
            [nameof(Ctrl_ApplySettings)]     = "应用设置",

            [nameof(Ctrl_SecSerialAdvanced)]    = "串口参数",
            [nameof(Ctrl_AutoConnect)]       = "自动连接",
            [nameof(Ctrl_AutoConnectHint)]   = "自动扫描 COM 端口并连接到广播 BMS 数据的端口。",
            [nameof(Ctrl_ReconnectInterval)] = "扫描间隔",
            [nameof(Ctrl_ProbeTimeout)]      = "探测超时",
            [nameof(Ctrl_FramesReceived)]    = "已接收帧数",
            [nameof(Ctrl_ParseErrors)]       = "解析错误",

            [nameof(Ctrl_SecBluetooth)]      = "蓝牙连接",
            [nameof(Ctrl_BtDevice)]          = "设备",
            [nameof(Ctrl_BtScan)]            = "扫描",
            [nameof(Ctrl_BtStopScan)]        = "停止",
            [nameof(Ctrl_BtConnect)]         = "连接",
            [nameof(Ctrl_BtDisconnect)]      = "断开",
            [nameof(Ctrl_BtPhSelect)]        = "选择设备…",
            [nameof(Ctrl_BtPhNoDevices)]     = "暂无设备 — 点击扫描",
            [nameof(Ctrl_BtHint)]            = "广播 Nordic UART 服务 (NUS) 的 ESP32 BLE 设备将出现在此处。请先在 Windows 设置中配对设备。",

            [nameof(Fb_SerialError)]            = "串口错误",
            [nameof(Fb_SelectChannel)]       = "选择端口",
            [nameof(Fb_SelectChannelMsg)]    = "请先从下拉列表中选择一个 COM 端口。",
            [nameof(Fb_SettingsApplied)]     = "设置已应用",
            [nameof(Fb_SettingsAppliedMsg)]  = "新阈值已生效。",
            [nameof(Fb_DefaultsRestored)]    = "已恢复默认",
            [nameof(Fb_DefaultsRestoredMsg)] = "值已重置 — 点击应用以生效。",

            [nameof(Log_SecStatus)]         = "记录状态",
            [nameof(Log_State)]             = "状态",
            [nameof(Log_Samples)]           = "样本",
            [nameof(Log_Duration)]          = "时长",
            [nameof(Log_SecFileSettings)]   = "文件设置",
            [nameof(Log_Folder)]            = "文件夹",
            [nameof(Log_Browse)]            = "浏览…",
            [nameof(Log_Format)]            = "格式",
            [nameof(Log_Filename)]          = "文件名",
            [nameof(Log_PhAutoFilename)]    = "留空则自动生成",
            [nameof(Log_SecControls)]       = "控制",
            [nameof(Log_ConnectHint)]       = "请先连接 ESP32，然后开始记录。",
            [nameof(Log_RecordingHint)]     = "正在记录。停止以关闭/写入文件。",
            [nameof(Log_ReadyHint)]         = "就绪。按下开始记录以开始录制。",
            [nameof(Log_StartLogging)]      = "开始记录",
            [nameof(Log_StopLogging)]       = "停止记录",
            [nameof(Log_OpenFolder)]        = "打开文件夹",
            [nameof(Log_SecDataFormat)]     = "数据格式",
            [nameof(Log_DataDesc1)]         = "每行 = 从 ESP32 接收的一个数据帧 (∼1 Hz)。",
            [nameof(Log_DataDesc2)]         = "字段: Timestamp · PackVoltage_V · SOC_pct · Current_A · Status · Cell1_V … Cell20_V · Bal1 … Bal20 · Temp1_C … Temp10_C",
            [nameof(Log_DataDesc3)]         = "CSV / TSV: 每帧流式写入磁盘。Excel / JSON: 在内存中缓冲，按停止时写入。",
            [nameof(Log_SecLiveData)]       = "实时数据 (最新 20 条)",
            [nameof(Log_HdrTimestamp)]      = "时间戳",
            [nameof(Log_HdrSoc)]            = "SOC %",
            [nameof(Log_HdrPackV)]          = "组电压",
            [nameof(Log_HdrCurrentA)]       = "电流 A",
            [nameof(Log_HdrStatus)]         = "状态",
            [nameof(Log_HdrMinCell)]        = "最低格压",
            [nameof(Log_HdrMaxCell)]        = "最高格压",
            [nameof(Log_HdrDeltaMv)]        = "差值 mV",
            [nameof(Log_HdrBalCells)]       = "均衡格",
            [nameof(Log_NoData)]            = "暂无数据 — 连接 ESP32 以查看数据流。",
            [nameof(Log_Idle)]              = "空闲",
            [nameof(Log_Logging)]           = "记录中",

            [nameof(Pb_SecLoadFile)]   = "加载文件",
            [nameof(Pb_NoFileLoaded)]  = "未加载文件",
            [nameof(Pb_Browse)]        = "浏览…",
            [nameof(Pb_Unload)]        = "卸载",
            [nameof(Pb_LoadStatus)]    = "浏览并打开 TLIG Dashboard CSV 日志文件 (.csv)。",
            [nameof(Pb_SecFileInfo)]   = "文件信息",
            [nameof(Pb_Frames)]        = "帧数",
            [nameof(Pb_EstDuration)]   = "估计时长",
            [nameof(Pb_PlaybackSpeed)] = "播放速度",
            [nameof(Pb_SecHowToUse)]   = "使用方法",
            [nameof(Pb_HowToUse1)]     = "浏览并加载上方的 CSV 文件，然后使用窗口底部出现的播放栏。",
            [nameof(Pb_HowToUse2)]     = "播放时，所有页面将实时更新录制数据。播放期间日志记录自动暂停。点击播放栏中的 ✕ 或按卸载返回实时模式。",
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
            ["Ui_InitialConnectionHint"] = "Not connected - open Control Panel to connect to the ESP32",

            ["Serial_StatusConnected"] = "Connected - {0} @ {1} baud",
            ["Serial_StatusDisconnected"] = "Disconnected",
            ["Serial_PortInUse"] = "Port {0} is already in use by another app.",
            ["Serial_OpenFailed"] = "Failed to open {0}: {1}",
            ["Serial_ReadError"] = "Serial read error: {0}",
            ["Serial_ParseError"] = "ESP JSON parse error (total: {0}) - data: \"{1}\"",
            ["AutoConnect_Suspended"] = "Auto-connect paused - click Connect to reconnect.",
            ["AutoConnect_Probing"] = "Detecting {0} @ {1} - waiting for BMS data...",
            ["AutoConnect_NoData"] = "{0} - no BMS data, skipped.",
            ["AutoConnect_Verified"] = "{0} verified - connecting...",
            ["AutoConnect_ConnectFailed"] = "{0} - failed to connect after verification.",

            ["Bt_StatusConnected"] = "Connected - {0} (Bluetooth)",
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

            ["Dash_BalancingCells"] = "{0} cells balancing",
            ["Dash_NotBalancing"] = "Not balancing",
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
            ["Log_ColPackVoltage"] = "Pack Voltage (V)",
            ["Log_ColCurrent"] = "Current (A)",
            ["Log_ColCell"] = "Cell {0} (V)",
            ["Log_ColBalancing"] = "Balancing {0}",
            ["Log_ColTemp"] = "Temp {0} (C)",

            ["Pb_LoadedFramesFromFile"] = "Loaded {0:N0} frames from \"{1}\".",
            ["Pb_Loading"] = "Loading...",
            ["Pb_Error"] = "Error: {0}",
            ["Pb_FileNoDataRows"] = "File has no data rows.",
            ["Pb_ExcelNoDataRows"] = "Excel file has no data rows.",
            ["Pb_JsonNoRows"] = "JSON file contains no rows.",
            ["Pb_NoValidDataRows"] = "No valid data rows found.",

            ["Cell_NtcThermistor"] = "10kOhm NTC thermistor",
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
            ["Alert_OvervoltageTitle"] = "BMS - Overvoltage",
            ["Alert_HighVoltageTitle"] = "BMS - High Voltage Warning",
            ["Alert_UndervoltageTitle"] = "BMS - Undervoltage",
            ["Alert_LowVoltageTitle"] = "BMS - Low Voltage Warning",
            ["Alert_OvercurrentTitle"] = "BMS - Overcurrent",
            ["Alert_TempCriticalTitle"] = "BMS - Temperature Critical",
            ["Alert_TempWarningTitle"] = "BMS - Temperature Warning",
            ["Alert_ImbalanceTitle"] = "BMS - Cell Imbalance",
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
            ["Ui_InitialConnectionHint"] = "Tidak terhubung - buka Panel Kontrol untuk menghubungkan ke ESP32",

            ["Serial_StatusConnected"] = "Terhubung - {0} @ {1} baud",
            ["Serial_StatusDisconnected"] = "Terputus",
            ["Serial_PortInUse"] = "Port {0} sedang dipakai aplikasi lain.",
            ["Serial_OpenFailed"] = "Gagal membuka {0}: {1}",
            ["Serial_ReadError"] = "Error baca serial: {0}",
            ["Serial_ParseError"] = "Error parsing JSON ESP (total: {0}) - data: \"{1}\"",
            ["AutoConnect_Suspended"] = "Auto-connect dijeda - klik Hubungkan untuk menghubungkan kembali.",
            ["AutoConnect_Probing"] = "Mendeteksi {0} @ {1} - menunggu data BMS...",
            ["AutoConnect_NoData"] = "{0} - tidak ada data BMS, dilewati.",
            ["AutoConnect_Verified"] = "{0} terverifikasi - menghubungkan...",
            ["AutoConnect_ConnectFailed"] = "{0} - gagal terhubung setelah verifikasi.",

            ["Bt_StatusConnected"] = "Terhubung - {0} (Bluetooth)",
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

            ["Dash_BalancingCells"] = "{0} sel menyeimbangkan",
            ["Dash_NotBalancing"] = "Tidak menyeimbangkan",
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
            ["Log_ColPackVoltage"] = "Tegangan Pack (V)",
            ["Log_ColCurrent"] = "Arus (A)",
            ["Log_ColCell"] = "Sel {0} (V)",
            ["Log_ColBalancing"] = "Balancing {0}",
            ["Log_ColTemp"] = "Suhu {0} (C)",

            ["Pb_LoadedFramesFromFile"] = "Memuat {0:N0} frame dari \"{1}\".",
            ["Pb_Loading"] = "Memuat...",
            ["Pb_Error"] = "Error: {0}",
            ["Pb_FileNoDataRows"] = "File tidak memiliki baris data.",
            ["Pb_ExcelNoDataRows"] = "File Excel tidak memiliki baris data.",
            ["Pb_JsonNoRows"] = "File JSON tidak berisi baris data.",
            ["Pb_NoValidDataRows"] = "Tidak ada baris data valid yang ditemukan.",

            ["Cell_NtcThermistor"] = "Termistor NTC 10kOhm",
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
            ["Alert_OvervoltageTitle"] = "BMS - Tegangan Berlebih",
            ["Alert_HighVoltageTitle"] = "BMS - Peringatan Tegangan Tinggi",
            ["Alert_UndervoltageTitle"] = "BMS - Tegangan Rendah",
            ["Alert_LowVoltageTitle"] = "BMS - Peringatan Tegangan Rendah",
            ["Alert_OvercurrentTitle"] = "BMS - Arus Berlebih",
            ["Alert_TempCriticalTitle"] = "BMS - Suhu Kritis",
            ["Alert_TempWarningTitle"] = "BMS - Peringatan Suhu",
            ["Alert_ImbalanceTitle"] = "BMS - Ketidakseimbangan Sel",
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
        ["ms"] = new()
        {
            ["Ui_StartupError"] = "Ralat Permulaan",
            ["Ui_Ok"] = "OK",
            ["Ui_Close"] = "Tutup",
            ["Ui_Cancel"] = "Batal",
            ["Ui_Save"] = "Simpan",
            ["Ui_SaveEllipsis"] = "Simpan...",
            ["Ui_SaveFailed"] = "Gagal menyimpan",
            ["Ui_Error"] = "Ralat",
            ["Ui_ErrorWithMessage"] = "Ralat: {0}",
            ["Ui_SourceNotConnected"] = "SUMBER: TIDAK DISAMBUNGKAN",
            ["Ui_SourceConnected"] = "SUMBER: {0} @ {1}",
            ["Ui_InitialConnectionHint"] = "Tidak disambungkan - buka Panel Kawalan untuk menyambung ke ESP32",
            ["Serial_StatusConnected"] = "Disambungkan - {0} @ {1} baud",
            ["Serial_StatusDisconnected"] = "Terputus",
            ["Serial_PortInUse"] = "Port {0} sedang digunakan aplikasi lain.",
            ["Serial_OpenFailed"] = "Gagal membuka {0}: {1}",
            ["Serial_ReadError"] = "Ralat baca serial: {0}",
            ["Serial_ParseError"] = "Ralat hurai JSON ESP (jumlah: {0}) - data: \"{1}\"",
            ["AutoConnect_Suspended"] = "Auto-sambung dijeda - klik Sambungkan untuk menyambung semula.",
            ["AutoConnect_Probing"] = "Mengesan {0} @ {1} - menunggu data BMS...",
            ["AutoConnect_NoData"] = "{0} - tiada data BMS, dilangkau.",
            ["AutoConnect_Verified"] = "{0} disahkan - menyambung...",
            ["AutoConnect_ConnectFailed"] = "{0} - gagal menyambung selepas pengesahan.",

            ["Bt_StatusConnected"] = "Disambungkan - {0} (Bluetooth)",
            ["Bt_StatusDisconnected"] = "Bluetooth terputus",
            ["Bt_ScanError"] = "Imbasan Bluetooth gagal: {0}",
            ["Bt_OpenFailed"] = "Gagal menyambung ke {0}: {1}",
            ["Bt_NoNusService"] = "{0} tidak menawarkan Nordic UART Service.",
            ["Bt_NoTxCharacteristic"] = "{0} - ciri notify tidak ditemui.",
            ["Bt_SubscribeFailed"] = "{0} - tidak dapat melanggan pemberitahuan.",
            ["Bt_ReadError"] = "Ralat baca Bluetooth: {0}",
            ["Bt_ParseError"] = "Ralat hurai JSON BLE (jumlah: {0}) - data: \"{1}\"",
            ["Bt_FbSelect"] = "Pilih peranti",
            ["Bt_FbSelectMsg"] = "Pilih peranti Bluetooth dari menu lungsur terlebih dahulu.",

            ["Dash_BalancingCells"] = "{0} sel sedang diimbangkan",
            ["Dash_NotBalancing"] = "Tidak mengimbang",
            ["Chart_TimeAxis"] = "masa ({0})",
            ["Chart_SampleRateUnknown"] = "- sampel/s",
            ["Chart_SamplesPerSecond"] = "{0} sampel/s",
            ["Chart_SecondsPerSample"] = "{0} s/sampel",
            ["Chart_Seconds"] = "saat",
            ["Chart_Minutes"] = "minit",
            ["Chart_Hours"] = "jam",
            ["PackStatus_Idle"] = "Siaga",
            ["PackStatus_Charging"] = "Mengecas",
            ["PackStatus_Discharging"] = "Nyahcas",
            ["PackStatus_Full"] = "Penuh",
            ["PackStatus_Error"] = "Ralat",
            ["Log_FormatCsv"] = "CSV - dipisah koma (.csv)",
            ["Log_FormatTsv"] = "TSV - dipisah tab (.tsv)",
            ["Log_FormatExcel"] = "Excel - buku kerja (.xlsx)",
            ["Log_FormatJson"] = "JSON - array objek (.json)",
            ["Log_ColPackVoltage"] = "Voltan Pack (V)",
            ["Log_ColCurrent"] = "Arus (A)",
            ["Log_ColCell"] = "Sel {0} (V)",
            ["Log_ColBalancing"] = "Pengimbangan {0}",
            ["Log_ColTemp"] = "Suhu {0} (C)",
            ["Pb_LoadedFramesFromFile"] = "Memuat {0:N0} bingkai daripada \"{1}\".",
            ["Pb_Loading"] = "Memuat...",
            ["Pb_Error"] = "Ralat: {0}",
            ["Pb_FileNoDataRows"] = "Fail tidak mempunyai baris data.",
            ["Pb_ExcelNoDataRows"] = "Fail Excel tidak mempunyai baris data.",
            ["Pb_JsonNoRows"] = "Fail JSON tidak mengandungi baris data.",
            ["Pb_NoValidDataRows"] = "Tiada baris data sah ditemui.",
            ["Cell_NtcThermistor"] = "Termistor NTC 10kOhm",
            ["Cell_TimeRangeTrim"] = "JULAT MASA (TRIM)",
            ["Cell_TrimNoData"] = "Tiada data direkodkan lagi",
            ["Cell_TrimReset"] = "Tetap Semula",
            ["Cell_TrimHint"] = "Seret pemegang untuk memilih julat masa. Carta suhu dikemas kini langsung mengikut tetingkap terpilih. Tetap semula untuk kembali ke paparan bergerak.",
            ["Cell_TrimFullRange"] = "Julat penuh: {0} -> {1}  ·  {2}  (seret pemegang untuk trim)",
            ["Cell_TrimTrimmedRange"] = "Trim: {0} -> {1}  ·  {2}",
            ["Cell_TempHistoryTitle"] = "Sejarah Suhu NTC {0}",
            ["Cell_VoltageHistoryTitle"] = "Sejarah Voltan Sel C{0:D2}",
            ["Cell_CurrentValue"] = "Semasa: {0}",
            ["Cell_TempAxis"] = "Suhu",
            ["Cell_VoltageAxis"] = "Voltan",
            ["Cell_SampleNumber"] = "Sampel #",
            ["Cell_ElapsedSeconds"] = "Masa berlalu (s)",
            ["Cell_ElapsedClock"] = "Masa berlalu ({0})",
            ["Cell_OneSample"] = "1 sampel dikumpul. Menunggu sampel tambahan untuk melukis garisan.",
            ["Cell_NoTempHistory"] = "Tiada sejarah suhu lagi. Menunggu sampel live/playback.",
            ["Cell_NoVoltageHistory"] = "Tiada sejarah voltan lagi. Menunggu sampel live/playback.",
            ["Cell_RangeSummary"] = "{0} sampel  ·  {1}-{2} {3}  ·  Julat {4} {3}",
            ["Cell_DeltaSummary"] = "{0} sampel  ·  {1}-{2} {3}  ·  Delta {4} {3}",
            ["Export_Title"] = "Eksport carta",
            ["Export_PreviewLive"] = "Pratonton (live)",
            ["Export_TimeRange"] = "Julat masa",
            ["Export_TimeRangeHint"] = "Seret pemegang untuk memilih segmen. Julat penuh: 0 - {0} s ({1:F1} min)",
            ["Export_Dimensions"] = "Dimensi",
            ["Export_AspectRatio"] = "Nisbah aspek",
            ["Export_WidthPx"] = "Lebar (px)",
            ["Export_HeightPx"] = "Tinggi (px)",
            ["Export_FileFormat"] = "Format fail",
            ["Export_Aspect43"] = "4:3 - kertas / lalai Origin",
            ["Export_Aspect32"] = "3:2 - foto / kertas lebar",
            ["Export_Aspect169"] = "16:9 - slaid / video",
            ["Export_AspectGolden"] = "Golden 1.618:1",
            ["Export_Aspect11"] = "1:1 - segi empat (korelasi)",
            ["Export_AspectCustom"] = "Tersuai - tetapkan tinggi manual",
            ["Export_FormatPng"] = "PNG - raster, lossless",
            ["Export_FormatJpg"] = "JPG - raster, lebih kecil",
            ["Export_FormatSvg"] = "SVG - vektor, boleh diedit",
            ["Export_FileTypePng"] = "Imej PNG",
            ["Export_FileTypeJpeg"] = "Imej JPEG",
            ["Export_FileTypeSvg"] = "Vektor SVG",
        },
        ["nl"] = new()
        {
            ["Ui_StartupError"] = "Opstartfout",
            ["Ui_Ok"] = "OK",
            ["Ui_Close"] = "Sluiten",
            ["Ui_Cancel"] = "Annuleren",
            ["Ui_Save"] = "Opslaan",
            ["Ui_SaveEllipsis"] = "Opslaan...",
            ["Ui_SaveFailed"] = "Opslaan mislukt",
            ["Ui_Error"] = "Fout",
            ["Ui_ErrorWithMessage"] = "Fout: {0}",
            ["Ui_SourceNotConnected"] = "BRON: NIET VERBONDEN",
            ["Ui_SourceConnected"] = "BRON: {0} @ {1}",
            ["Ui_InitialConnectionHint"] = "Niet verbonden - open Configuratiescherm om met ESP32 te verbinden",
            ["Serial_StatusConnected"] = "Verbonden - {0} @ {1} baud",
            ["Serial_StatusDisconnected"] = "Verbroken",
            ["Serial_PortInUse"] = "Poort {0} is al in gebruik door een andere app.",
            ["Serial_OpenFailed"] = "{0} openen mislukt: {1}",
            ["Serial_ReadError"] = "Seriele leesfout: {0}",
            ["Serial_ParseError"] = "ESP JSON-parsefout (totaal: {0}) - data: \"{1}\"",
            ["AutoConnect_Suspended"] = "Automatisch verbinden gepauzeerd - klik Verbinden om opnieuw te verbinden.",
            ["AutoConnect_Probing"] = "{0} @ {1} detecteren - wacht op BMS-data...",
            ["AutoConnect_NoData"] = "{0} - geen BMS-data, overgeslagen.",
            ["AutoConnect_Verified"] = "{0} geverifieerd - verbinden...",
            ["AutoConnect_ConnectFailed"] = "{0} - verbinden mislukt na verificatie.",

            ["Bt_StatusConnected"] = "Verbonden - {0} (Bluetooth)",
            ["Bt_StatusDisconnected"] = "Bluetooth verbroken",
            ["Bt_ScanError"] = "Bluetooth-scan mislukt: {0}",
            ["Bt_OpenFailed"] = "Verbinden met {0} mislukt: {1}",
            ["Bt_NoNusService"] = "{0} biedt geen Nordic UART Service.",
            ["Bt_NoTxCharacteristic"] = "{0} - notify-kenmerk niet gevonden.",
            ["Bt_SubscribeFailed"] = "{0} - kan niet abonneren op meldingen.",
            ["Bt_ReadError"] = "Bluetooth-leesfout: {0}",
            ["Bt_ParseError"] = "BLE JSON-parsefout (totaal: {0}) - data: \"{1}\"",
            ["Bt_FbSelect"] = "Selecteer een apparaat",
            ["Bt_FbSelectMsg"] = "Kies eerst een Bluetooth-apparaat in de keuzelijst.",

            ["Dash_BalancingCells"] = "{0} cellen balanceren",
            ["Dash_NotBalancing"] = "Niet balanceren",
            ["Chart_TimeAxis"] = "tijd ({0})",
            ["Chart_SampleRateUnknown"] = "- monster/s",
            ["Chart_SamplesPerSecond"] = "{0} monsters/s",
            ["Chart_SecondsPerSample"] = "{0} s/monster",
            ["Chart_Seconds"] = "seconden",
            ["Chart_Minutes"] = "minuten",
            ["Chart_Hours"] = "uren",
            ["PackStatus_Idle"] = "Inactief",
            ["PackStatus_Charging"] = "Laden",
            ["PackStatus_Discharging"] = "Ontladen",
            ["PackStatus_Full"] = "Vol",
            ["PackStatus_Error"] = "Fout",
            ["Log_FormatCsv"] = "CSV - kommagescheiden (.csv)",
            ["Log_FormatTsv"] = "TSV - tabgescheiden (.tsv)",
            ["Log_FormatExcel"] = "Excel - werkmap (.xlsx)",
            ["Log_FormatJson"] = "JSON - array van objecten (.json)",
            ["Log_ColPackVoltage"] = "Packspanning (V)",
            ["Log_ColCurrent"] = "Stroom (A)",
            ["Log_ColCell"] = "Cel {0} (V)",
            ["Log_ColBalancing"] = "Balanceren {0}",
            ["Log_ColTemp"] = "Temp {0} (C)",
            ["Pb_LoadedFramesFromFile"] = "{0:N0} frames geladen uit \"{1}\".",
            ["Pb_Loading"] = "Laden...",
            ["Pb_Error"] = "Fout: {0}",
            ["Pb_FileNoDataRows"] = "Bestand bevat geen datarijen.",
            ["Pb_ExcelNoDataRows"] = "Excel-bestand bevat geen datarijen.",
            ["Pb_JsonNoRows"] = "JSON-bestand bevat geen rijen.",
            ["Pb_NoValidDataRows"] = "Geen geldige datarijen gevonden.",
            ["Cell_NtcThermistor"] = "10kOhm NTC-thermistor",
            ["Cell_TimeRangeTrim"] = "TIJDBEREIK (TRIM)",
            ["Cell_TrimNoData"] = "Nog geen data vastgelegd",
            ["Cell_TrimReset"] = "Resetten",
            ["Cell_TrimHint"] = "Sleep de grepen om een tijdbereik te kiezen. De temperatuurgrafiek werkt live bij naar het geselecteerde venster. Reset om terug te keren naar de rollende weergave.",
            ["Cell_TrimFullRange"] = "Volledig bereik: {0} -> {1}  ·  {2}  (sleep een greep om te trimmen)",
            ["Cell_TrimTrimmedRange"] = "Trim: {0} -> {1}  ·  {2}",
            ["Cell_TempHistoryTitle"] = "NTC {0} Temperatuurgeschiedenis",
            ["Cell_VoltageHistoryTitle"] = "Cel C{0:D2} Spanningsgeschiedenis",
            ["Cell_CurrentValue"] = "Huidig: {0}",
            ["Cell_TempAxis"] = "Temp",
            ["Cell_VoltageAxis"] = "Spanning",
            ["Cell_SampleNumber"] = "Monster #",
            ["Cell_ElapsedSeconds"] = "Verstreken tijd (s)",
            ["Cell_ElapsedClock"] = "Verstreken tijd ({0})",
            ["Cell_OneSample"] = "1 monster verzameld. Wacht op meer monsters om een lijn te tekenen.",
            ["Cell_NoTempHistory"] = "Nog geen temperatuurgeschiedenis. Wacht op live-/afspeelmonsters.",
            ["Cell_NoVoltageHistory"] = "Nog geen spanningsgeschiedenis. Wacht op live-/afspeelmonsters.",
            ["Cell_RangeSummary"] = "{0} monsters  ·  {1}-{2} {3}  ·  Bereik {4} {3}",
            ["Cell_DeltaSummary"] = "{0} monsters  ·  {1}-{2} {3}  ·  Delta {4} {3}",
            ["Export_Title"] = "Grafiek exporteren",
            ["Export_PreviewLive"] = "Voorbeeld (live)",
            ["Export_TimeRange"] = "Tijdbereik",
            ["Export_TimeRangeHint"] = "Sleep de grepen om een segment te kiezen. Volledig bereik: 0 - {0} s ({1:F1} min)",
            ["Export_Dimensions"] = "Afmetingen",
            ["Export_AspectRatio"] = "Beeldverhouding",
            ["Export_WidthPx"] = "Breedte (px)",
            ["Export_HeightPx"] = "Hoogte (px)",
            ["Export_FileFormat"] = "Bestandsformaat",
            ["Export_Aspect43"] = "4:3 - papier / Origin-standaard",
            ["Export_Aspect32"] = "3:2 - foto / breed papier",
            ["Export_Aspect169"] = "16:9 - dia / video",
            ["Export_AspectGolden"] = "Golden 1.618:1",
            ["Export_Aspect11"] = "1:1 - vierkant (correlatie)",
            ["Export_AspectCustom"] = "Aangepast - hoogte handmatig instellen",
            ["Export_FormatPng"] = "PNG - raster, lossless",
            ["Export_FormatJpg"] = "JPG - raster, kleiner",
            ["Export_FormatSvg"] = "SVG - vector, bewerkbaar",
            ["Export_FileTypePng"] = "PNG-afbeelding",
            ["Export_FileTypeJpeg"] = "JPEG-afbeelding",
            ["Export_FileTypeSvg"] = "SVG-vector",
        },
        ["zh"] = new()
        {
            ["Ui_StartupError"] = "启动错误",
            ["Ui_Ok"] = "确定",
            ["Ui_Close"] = "关闭",
            ["Ui_Cancel"] = "取消",
            ["Ui_Save"] = "保存",
            ["Ui_SaveEllipsis"] = "保存...",
            ["Ui_SaveFailed"] = "保存失败",
            ["Ui_Error"] = "错误",
            ["Ui_ErrorWithMessage"] = "错误: {0}",
            ["Ui_SourceNotConnected"] = "来源: 未连接",
            ["Ui_SourceConnected"] = "来源: {0} @ {1}",
            ["Ui_InitialConnectionHint"] = "未连接 - 打开控制面板连接 ESP32",
            ["Serial_StatusConnected"] = "已连接 - {0} @ {1} baud",
            ["Serial_StatusDisconnected"] = "已断开",
            ["Serial_PortInUse"] = "端口 {0} 已被其他应用使用。",
            ["Serial_OpenFailed"] = "无法打开 {0}: {1}",
            ["Serial_ReadError"] = "串口读取错误: {0}",
            ["Serial_ParseError"] = "ESP JSON 解析错误 (总计: {0}) - 数据: \"{1}\"",
            ["AutoConnect_Suspended"] = "自动连接已暂停 - 点击连接以重新连接。",
            ["AutoConnect_Probing"] = "正在检测 {0} @ {1} - 等待 BMS 数据...",
            ["AutoConnect_NoData"] = "{0} - 无 BMS 数据，已跳过。",
            ["AutoConnect_Verified"] = "{0} 已验证 - 正在连接...",
            ["AutoConnect_ConnectFailed"] = "{0} - 验证后连接失败。",

            ["Bt_StatusConnected"] = "已连接 - {0} (蓝牙)",
            ["Bt_StatusDisconnected"] = "蓝牙已断开",
            ["Bt_ScanError"] = "蓝牙扫描失败: {0}",
            ["Bt_OpenFailed"] = "无法连接到 {0}: {1}",
            ["Bt_NoNusService"] = "{0} 不提供 Nordic UART 服务。",
            ["Bt_NoTxCharacteristic"] = "{0} - 未找到 notify 特征。",
            ["Bt_SubscribeFailed"] = "{0} - 无法订阅通知。",
            ["Bt_ReadError"] = "蓝牙读取错误: {0}",
            ["Bt_ParseError"] = "BLE JSON 解析错误 (总计: {0}) - 数据: \"{1}\"",
            ["Bt_FbSelect"] = "选择设备",
            ["Bt_FbSelectMsg"] = "请先从下拉列表中选择一个蓝牙设备。",

            ["Dash_BalancingCells"] = "{0} 个电芯正在均衡",
            ["Dash_NotBalancing"] = "未均衡",
            ["Chart_TimeAxis"] = "时间 ({0})",
            ["Chart_SampleRateUnknown"] = "- 样本/秒",
            ["Chart_SamplesPerSecond"] = "{0} 样本/秒",
            ["Chart_SecondsPerSample"] = "{0} 秒/样本",
            ["Chart_Seconds"] = "秒",
            ["Chart_Minutes"] = "分钟",
            ["Chart_Hours"] = "小时",
            ["PackStatus_Idle"] = "空闲",
            ["PackStatus_Charging"] = "充电中",
            ["PackStatus_Discharging"] = "放电中",
            ["PackStatus_Full"] = "已满",
            ["PackStatus_Error"] = "错误",
            ["Log_FormatCsv"] = "CSV - 逗号分隔 (.csv)",
            ["Log_FormatTsv"] = "TSV - 制表符分隔 (.tsv)",
            ["Log_FormatExcel"] = "Excel - 工作簿 (.xlsx)",
            ["Log_FormatJson"] = "JSON - 对象数组 (.json)",
            ["Log_ColPackVoltage"] = "组电压 (V)",
            ["Log_ColCurrent"] = "电流 (A)",
            ["Log_ColCell"] = "电芯 {0} (V)",
            ["Log_ColBalancing"] = "均衡 {0}",
            ["Log_ColTemp"] = "温度 {0} (C)",
            ["Pb_LoadedFramesFromFile"] = "已从 \"{1}\" 加载 {0:N0} 帧。",
            ["Pb_Loading"] = "正在加载...",
            ["Pb_Error"] = "错误: {0}",
            ["Pb_FileNoDataRows"] = "文件没有数据行。",
            ["Pb_ExcelNoDataRows"] = "Excel 文件没有数据行。",
            ["Pb_JsonNoRows"] = "JSON 文件不包含数据行。",
            ["Pb_NoValidDataRows"] = "未找到有效数据行。",
            ["Alert_SerialErrorTitle"] = "串口错误",
            ["Alert_ConnectionTitle"] = "连接",
            ["Alert_OvervoltageTitle"] = "BMS - 过压",
            ["Alert_HighVoltageTitle"] = "BMS - 高电压警告",
            ["Alert_UndervoltageTitle"] = "BMS - 欠压",
            ["Alert_LowVoltageTitle"] = "BMS - 低电压警告",
            ["Alert_OvercurrentTitle"] = "BMS - 过流",
            ["Alert_TempCriticalTitle"] = "BMS - 温度严重",
            ["Alert_TempWarningTitle"] = "BMS - 温度警告",
            ["Alert_ImbalanceTitle"] = "BMS - 电芯不均衡",
            ["Alert_CellOvervoltageBody"] = "电芯 {0} 为 {1:F3}V - 超过 {2:F2}V 截止值",
            ["Alert_CellHighVoltageBody"] = "电芯 {0} 为 {1:F3}V - 超过 {2:F2}V 警告值",
            ["Alert_CellUndervoltageBody"] = "电芯 {0} 为 {1:F3}V - 低于 {2:F2}V 截止值",
            ["Alert_CellLowVoltageBody"] = "电芯 {0} 为 {1:F3}V - 低于 {2:F2}V 警告值",
            ["Alert_ChargeCurrentBody"] = "充电电流 {0:F1}A - 超过 {1:F0}A 限值",
            ["Alert_DischargeCurrentBody"] = "放电电流 {0:F1}A - 超过 {1:F0}A 限值",
            ["Alert_TempCriticalBody"] = "传感器 {0} 为 {1:F0}C - 超过 {2:F0}C 截止值",
            ["Alert_TempWarningBody"] = "传感器 {0} 为 {1:F0}C - 超过 {2:F0}C 警告值",
            ["Alert_ImbalanceBody"] = "压差 {0:F1}mV - 超过 {1:F0}mV 阈值",
            ["Cell_NtcThermistor"] = "10kOhm NTC 热敏电阻",
            ["Cell_TimeRangeTrim"] = "时间范围 (裁剪)",
            ["Cell_TrimNoData"] = "尚未采集数据",
            ["Cell_TrimReset"] = "重置",
            ["Cell_TrimHint"] = "拖动手柄选择时间范围。温度图会实时更新到所选窗口。重置可返回滚动视图。",
            ["Cell_TrimFullRange"] = "完整范围: {0} -> {1}  ·  {2}  (拖动手柄裁剪)",
            ["Cell_TrimTrimmedRange"] = "裁剪: {0} -> {1}  ·  {2}",
            ["Cell_TempHistoryTitle"] = "NTC {0} 温度历史",
            ["Cell_VoltageHistoryTitle"] = "电芯 C{0:D2} 电压历史",
            ["Cell_CurrentValue"] = "当前: {0}",
            ["Cell_TempAxis"] = "温度",
            ["Cell_VoltageAxis"] = "电压",
            ["Cell_SampleNumber"] = "样本 #",
            ["Cell_ElapsedSeconds"] = "已过时间 (秒)",
            ["Cell_ElapsedClock"] = "已过时间 ({0})",
            ["Cell_OneSample"] = "已采集 1 个样本。等待更多样本以绘制曲线。",
            ["Cell_NoTempHistory"] = "尚无温度历史。等待实时/回放样本。",
            ["Cell_NoVoltageHistory"] = "尚无电压历史。等待实时/回放样本。",
            ["Cell_RangeSummary"] = "{0} 个样本  ·  {1}-{2} {3}  ·  范围 {4} {3}",
            ["Cell_DeltaSummary"] = "{0} 个样本  ·  {1}-{2} {3}  ·  Delta {4} {3}",
            ["Export_Title"] = "导出图表",
            ["Export_PreviewLive"] = "预览 (实时)",
            ["Export_TimeRange"] = "时间范围",
            ["Export_TimeRangeHint"] = "拖动手柄选择片段。完整范围: 0 - {0} 秒 ({1:F1} 分钟)",
            ["Export_Dimensions"] = "尺寸",
            ["Export_AspectRatio"] = "宽高比",
            ["Export_WidthPx"] = "宽度 (px)",
            ["Export_HeightPx"] = "高度 (px)",
            ["Export_FileFormat"] = "文件格式",
            ["Export_Aspect43"] = "4:3 - 纸张 / Origin 默认",
            ["Export_Aspect32"] = "3:2 - 照片 / 宽纸",
            ["Export_Aspect169"] = "16:9 - 幻灯片 / 视频",
            ["Export_AspectGolden"] = "黄金 1.618:1",
            ["Export_Aspect11"] = "1:1 - 方形 (相关)",
            ["Export_AspectCustom"] = "自定义 - 手动设置高度",
            ["Export_FormatPng"] = "PNG - 光栅，无损",
            ["Export_FormatJpg"] = "JPG - 光栅，较小",
            ["Export_FormatSvg"] = "SVG - 矢量，可编辑",
            ["Export_FileTypePng"] = "PNG 图像",
            ["Export_FileTypeJpeg"] = "JPEG 图像",
            ["Export_FileTypeSvg"] = "SVG 矢量",
        },
    };
}
