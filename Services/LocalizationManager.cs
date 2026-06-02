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
    public string Nav_LearningAnalytic => T(nameof(Nav_LearningAnalytic));
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
    public string Ui_Menu_EarlyAccess   => T(nameof(Ui_Menu_EarlyAccess));
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
    public string Account_LoggedInAs     => T(nameof(Account_LoggedInAs));
    public string Account_NotLoggedInYet => T(nameof(Account_NotLoggedInYet));
    public string Account_Logout         => T(nameof(Account_Logout));

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

    // ── Live view / HMI ──────────────────────────────────────────────────
    public string Live_Header      => T(nameof(Live_Header));
    public string Live_CamInfo     => T(nameof(Live_CamInfo));
    public string Live_Waiting     => T(nameof(Live_Waiting));
    public string Live_LiveBtn     => T(nameof(Live_LiveBtn));
    public string Live_SelectCamera => T(nameof(Live_SelectCamera));
    public string Live_NoCamera     => T(nameof(Live_NoCamera));
    public string Live_CameraDenied => T(nameof(Live_CameraDenied));
    public string Live_CameraError  => T(nameof(Live_CameraError));
    public string Live_CameraStop   => T(nameof(Live_CameraStop));
    public string Hmi_Header       => T(nameof(Hmi_Header));
    public string Hmi_SelectSource => T(nameof(Hmi_SelectSource));
    public string Hmi_RefreshSources => T(nameof(Hmi_RefreshSources));
    public string Hmi_StopShare    => T(nameof(Hmi_StopShare));
    public string Hmi_NoSource     => T(nameof(Hmi_NoSource));
    public string Hmi_SourceSelected => T(nameof(Hmi_SourceSelected));
    public string Hmi_SourceClosed => T(nameof(Hmi_SourceClosed));
    public string Hmi_SelectPlaceholder => T(nameof(Hmi_SelectPlaceholder));
    public string Hmi_CaptureUnsupported => T(nameof(Hmi_CaptureUnsupported));
    public string Hmi_CaptureError => T(nameof(Hmi_CaptureError));
    public string Hmi_CaptureErrorUnknown => T(nameof(Hmi_CaptureErrorUnknown));
    public string Hmi_WaitingStream => T(nameof(Hmi_WaitingStream));

    // ── Sharing (server broadcast / client connect) ──────────────────────────
    public string Share_TabBroadcast   => T(nameof(Share_TabBroadcast));
    public string Share_TabConnect     => T(nameof(Share_TabConnect));
    public string Share_Port           => T(nameof(Share_Port));
    public string Share_Token          => T(nameof(Share_Token));
    public string Share_ShareCamera    => T(nameof(Share_ShareCamera));
    public string Share_ShareHmi       => T(nameof(Share_ShareHmi));
    public string Share_Start          => T(nameof(Share_Start));
    public string Share_Stop           => T(nameof(Share_Stop));
    public string Share_Running        => T(nameof(Share_Running));
    public string Share_Stopped        => T(nameof(Share_Stopped));
    public string Share_Clients        => T(nameof(Share_Clients));
    public string Share_LocalAddress   => T(nameof(Share_LocalAddress));
    public string Share_PublicAddress  => T(nameof(Share_PublicAddress));
    public string Share_PublicFetching => T(nameof(Share_PublicFetching));
    public string Share_PublicFailed   => T(nameof(Share_PublicFailed));
    public string Share_ServerHost     => T(nameof(Share_ServerHost));
    public string Share_Connect        => T(nameof(Share_Connect));
    public string Share_Disconnect     => T(nameof(Share_Disconnect));
    public string Share_Connected      => T(nameof(Share_Connected));
    public string Share_Disconnected   => T(nameof(Share_Disconnected));
    public string Share_ConnError      => T(nameof(Share_ConnError));

    // ── Learning analytic ────────────────────────────────────────────────
    public string Learn_Title       => T(nameof(Learn_Title));
    public string Learn_Status      => T(nameof(Learn_Status));
    public string Learn_ModelQuality => T(nameof(Learn_ModelQuality));
    public string Learn_Accuracy    => T(nameof(Learn_Accuracy));
    public string Learn_Loss        => T(nameof(Learn_Loss));
    public string Learn_Dataset     => T(nameof(Learn_Dataset));
    public string Learn_LastTraining => T(nameof(Learn_LastTraining));
    public string Learn_Waiting     => T(nameof(Learn_Waiting));
    public string Learn_TrainingTrend => T(nameof(Learn_TrainingTrend));
    public string Learn_FeatureImpact => T(nameof(Learn_FeatureImpact));
    public string Learn_InsightQueue => T(nameof(Learn_InsightQueue));
    public string Learn_DataCoverage => T(nameof(Learn_DataCoverage));
    public string Learn_AnomalyScore => T(nameof(Learn_AnomalyScore));
    public string Learn_OpenFull    => T(nameof(Learn_OpenFull));
    public string Learn_Insight1    => T(nameof(Learn_Insight1));
    public string Learn_Insight2    => T(nameof(Learn_Insight2));
    public string Learn_Insight3    => T(nameof(Learn_Insight3));

    // ── Learning dashboard (client course-analytics page) ─────────────────
    public string LearnDash_Subtitle        => T(nameof(LearnDash_Subtitle));
    public string LearnDash_OverallPerf      => T(nameof(LearnDash_OverallPerf));
    public string LearnDash_CompletionRate   => T(nameof(LearnDash_CompletionRate));
    public string LearnDash_ProLearner       => T(nameof(LearnDash_ProLearner));
    public string LearnDash_TotalEnroll      => T(nameof(LearnDash_TotalEnroll));
    public string LearnDash_CourseCompleted  => T(nameof(LearnDash_CourseCompleted));
    public string LearnDash_HoursSpent       => T(nameof(LearnDash_HoursSpent));
    public string LearnDash_HoursSpentSub    => T(nameof(LearnDash_HoursSpentSub));
    public string LearnDash_LiveAttended     => T(nameof(LearnDash_LiveAttended));
    public string LearnDash_QuizPractised    => T(nameof(LearnDash_QuizPractised));
    public string LearnDash_AssignmentDone   => T(nameof(LearnDash_AssignmentDone));
    public string LearnDash_TotalCourses     => T(nameof(LearnDash_TotalCourses));
    public string LearnDash_ColCourseName    => T(nameof(LearnDash_ColCourseName));
    public string LearnDash_ColProgress      => T(nameof(LearnDash_ColProgress));
    public string LearnDash_ColScore         => T(nameof(LearnDash_ColScore));
    public string LearnDash_ColStatus        => T(nameof(LearnDash_ColStatus));
    public string LearnDash_InProgress       => T(nameof(LearnDash_InProgress));
    public string LearnDash_Completed        => T(nameof(LearnDash_Completed));
    public string LearnDash_Chapter          => T(nameof(LearnDash_Chapter));
    public string LearnDash_Lecture          => T(nameof(LearnDash_Lecture));
    public string LearnDash_Assignment       => T(nameof(LearnDash_Assignment));
    public string LearnDash_View             => T(nameof(LearnDash_View));
    public string LearnDash_Upload           => T(nameof(LearnDash_Upload));
    public string LearnDash_SubmitBefore     => T(nameof(LearnDash_SubmitBefore));
    public string LearnDash_PendingQuizzes   => T(nameof(LearnDash_PendingQuizzes));
    public string LearnDash_SeeAll           => T(nameof(LearnDash_SeeAll));
    public string LearnDash_Start            => T(nameof(LearnDash_Start));
    public string LearnDash_Question         => T(nameof(LearnDash_Question));
    public string LearnDash_Min              => T(nameof(LearnDash_Min));

    // Composed sample lines (numbers are placeholder data; words are localized).
    public string LearnDash_CourseMeta       => $"5 {LearnDash_Chapter} · 30 {LearnDash_Lecture}";
    public string LearnDash_QuizMeta         => $"10 {LearnDash_Question} · 15 {LearnDash_Min}";
    public string LearnDash_SubmitBeforeSample => $"{LearnDash_SubmitBefore} : 15 Oct 2024 · 12:00 PM";

    // ── Control section ───────────────────────────────────────────────────
    public string Ctl_Header       => T(nameof(Ctl_Header));
    public string Ctl_Setpoint     => T(nameof(Ctl_Setpoint));
    public string Ctl_Mode         => T(nameof(Ctl_Mode));
    public string Ctl_Manual       => T(nameof(Ctl_Manual));
    public string Ctl_Auto         => T(nameof(Ctl_Auto));
    public string Ctl_Cascade      => T(nameof(Ctl_Cascade));
    public string Ctl_Run          => T(nameof(Ctl_Run));
    public string Ctl_Stop         => T(nameof(Ctl_Stop));
    public string Ctl_Reset        => T(nameof(Ctl_Reset));
    public string Ctl_EStop        => T(nameof(Ctl_EStop));

    // ── Status system & alarm ─────────────────────────────────────────────
    public string Sys_StatusTitle  => T(nameof(Sys_StatusTitle));
    public string Sys_Plc          => T(nameof(Sys_Plc));
    public string Sys_Camera       => T(nameof(Sys_Camera));
    public string Sys_Sensor       => T(nameof(Sys_Sensor));
    public string Sys_AiAssistant  => T(nameof(Sys_AiAssistant));
    public string Sys_Online       => T(nameof(Sys_Online));
    public string Sys_Active       => T(nameof(Sys_Active));
    public string Sys_Offline      => T(nameof(Sys_Offline));
    public string Sys_Inactive     => T(nameof(Sys_Inactive));
    public string Alarm_Title      => T(nameof(Alarm_Title));
    public string Alarm_None       => T(nameof(Alarm_None));

    // ── Block diagram labels ───────────────────────────────────────────────
    public string Blk_Setpoint     => T(nameof(Blk_Setpoint));
    public string Blk_Pid          => T(nameof(Blk_Pid));
    public string Blk_Plant        => T(nameof(Blk_Plant));
    public string Resp_Step        => T(nameof(Resp_Step));

    // ── AI quick suggestion ────────────────────────────────────────────────
    public string Ai_QuickSuggestion     => T(nameof(Ai_QuickSuggestion));
    public string Ai_SugTuningPid        => T(nameof(Ai_SugTuningPid));
    public string Ai_SugOvershoot        => T(nameof(Ai_SugOvershoot));
    public string Ai_SugHeatExchanger    => T(nameof(Ai_SugHeatExchanger));
    public string Ai_SugTroubleshooting  => T(nameof(Ai_SugTroubleshooting));

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

    // ── Login (credential connect) ────────────────────────────────────────
    public string Login_ServerAddress     => T(nameof(Login_ServerAddress));
    public string Login_ServerAddressHint => T(nameof(Login_ServerAddressHint));
    public string Login_Connecting         => T(nameof(Login_Connecting));
    public string Login_ErrorNoServer       => T(nameof(Login_ErrorNoServer));
    public string Login_ErrorUnreachable     => T(nameof(Login_ErrorUnreachable));

    // ── Signup (client self-registration, @its.ac.id) ─────────────────────
    public string Signup_Title               => T(nameof(Signup_Title));
    public string Signup_Subtitle            => T(nameof(Signup_Subtitle));
    public string Signup_Email               => T(nameof(Signup_Email));
    public string Signup_EmailHint           => T(nameof(Signup_EmailHint));
    public string Signup_ConfirmPassword     => T(nameof(Signup_ConfirmPassword));
    public string Signup_ConfirmPasswordHint => T(nameof(Signup_ConfirmPasswordHint));
    public string Signup_Submit              => T(nameof(Signup_Submit));
    public string Signup_BackToLogin         => T(nameof(Signup_BackToLogin));
    public string Signup_CreateAccountLink   => T(nameof(Signup_CreateAccountLink));
    public string Signup_Success             => T(nameof(Signup_Success));
    public string Signup_ErrEmpty            => T(nameof(Signup_ErrEmpty));
    public string Signup_ErrEmailFormat      => T(nameof(Signup_ErrEmailFormat));
    public string Signup_ErrEmailDomain      => T(nameof(Signup_ErrEmailDomain));
    public string Signup_ErrExists           => T(nameof(Signup_ErrExists));
    public string Signup_ErrPasswordMismatch => T(nameof(Signup_ErrPasswordMismatch));
    public string Signup_ErrPasswordShort    => T(nameof(Signup_ErrPasswordShort));
    public string Signup_ErrUnknown          => T(nameof(Signup_ErrUnknown));

    // ── Sharing: session / connect panel ──────────────────────────────────
    public string Share_ServerAccount  => T(nameof(Share_ServerAccount));
    public string Share_NotLoggedIn    => T(nameof(Share_NotLoggedIn));
    public string Share_LoginAgain     => T(nameof(Share_LoginAgain));
    public string Share_AuthHint         => T(nameof(Share_AuthHint));

    // ── Firewall / port-forwarding status ─────────────────────────────────
    public string Share_FwTitle          => T(nameof(Share_FwTitle));
    public string Share_FwActive         => T(nameof(Share_FwActive));
    public string Share_FwMissing        => T(nameof(Share_FwMissing));
    public string Share_FwUnknown        => T(nameof(Share_FwUnknown));
    public string Share_FwAddBtn         => T(nameof(Share_FwAddBtn));
    public string Share_FwAdding         => T(nameof(Share_FwAdding));
    public string Share_FwAddFailed      => T(nameof(Share_FwAddFailed));
    public string Share_NatHint          => T(nameof(Share_NatHint));
    public string Share_TunnelHint       => T(nameof(Share_TunnelHint));

    // ── Cloudflare Tunnel ─────────────────────────────────────────────────────
    public string Tunnel_Title         => T(nameof(Tunnel_Title));
    public string Tunnel_Start         => T(nameof(Tunnel_Start));
    public string Tunnel_Stop          => T(nameof(Tunnel_Stop));
    public string Tunnel_Starting      => T(nameof(Tunnel_Starting));
    public string Tunnel_Running       => T(nameof(Tunnel_Running));
    public string Tunnel_Stopped       => T(nameof(Tunnel_Stopped));
    public string Tunnel_Error         => T(nameof(Tunnel_Error));
    public string Tunnel_NotFound      => T(nameof(Tunnel_NotFound));
    public string Tunnel_Download      => T(nameof(Tunnel_Download));
    public string Tunnel_Downloading   => T(nameof(Tunnel_Downloading));
    public string Tunnel_DownloadFail  => T(nameof(Tunnel_DownloadFail));
    public string Tunnel_CopyUrl       => T(nameof(Tunnel_CopyUrl));
    public string Tunnel_Copied        => T(nameof(Tunnel_Copied));
    public string Tunnel_ShareHint     => T(nameof(Tunnel_ShareHint));

    // ── Cloudflare Tunnel — custom domain (named tunnel) ────────────────────────
    public string Tunnel_UseCustomDomain => T(nameof(Tunnel_UseCustomDomain));
    public string Tunnel_CustomDomainDesc => T(nameof(Tunnel_CustomDomainDesc));
    public string Tunnel_DomainLabel     => T(nameof(Tunnel_DomainLabel));
    public string Tunnel_DomainHint      => T(nameof(Tunnel_DomainHint));
    public string Tunnel_Login           => T(nameof(Tunnel_Login));
    public string Tunnel_Relogin         => T(nameof(Tunnel_Relogin));
    public string Tunnel_LoggingIn       => T(nameof(Tunnel_LoggingIn));
    public string Tunnel_LoggedIn        => T(nameof(Tunnel_LoggedIn));
    public string Tunnel_NotLoggedIn     => T(nameof(Tunnel_NotLoggedIn));
    public string Tunnel_LoginUrlHint    => T(nameof(Tunnel_LoginUrlHint));
    public string Tunnel_DomainRequired  => T(nameof(Tunnel_DomainRequired));

    // ── Settings page (server-only full configuration) ────────────────────────
    public string Nav_Settings         => T(nameof(Nav_Settings));
    public string Settings_Subtitle    => T(nameof(Settings_Subtitle));
    public string Settings_BroadcastSection => T(nameof(Settings_BroadcastSection));
    public string Nav_Broadcast        => T(nameof(Nav_Broadcast));
    public string Bcast_Subtitle       => T(nameof(Bcast_Subtitle));
    public string Bcast_ServerSection  => T(nameof(Bcast_ServerSection));
    public string Bcast_TunnelDesc     => T(nameof(Bcast_TunnelDesc));

    // ── User Management page ───────────────────────────────────────────────
    public string Nav_UserManagement => T(nameof(Nav_UserManagement));
    public string Um_Title           => T(nameof(Um_Title));
    public string Um_Subtitle        => T(nameof(Um_Subtitle));
    public string Um_AddUser         => T(nameof(Um_AddUser));
    public string Um_ColUser         => T(nameof(Um_ColUser));
    public string Um_ColName         => T(nameof(Um_ColName));
    public string Um_ColRole         => T(nameof(Um_ColRole));
    public string Um_ColStatus       => T(nameof(Um_ColStatus));
    public string Um_ColLastLogin    => T(nameof(Um_ColLastLogin));
    public string Um_Enabled         => T(nameof(Um_Enabled));
    public string Um_Disabled        => T(nameof(Um_Disabled));
    public string Um_Enable          => T(nameof(Um_Enable));
    public string Um_Disable         => T(nameof(Um_Disable));
    public string Um_ResetPassword   => T(nameof(Um_ResetPassword));
    public string Um_Edit            => T(nameof(Um_Edit));
    public string Um_Delete          => T(nameof(Um_Delete));
    public string Um_Never           => T(nameof(Um_Never));
    public string Um_DlgAddTitle     => T(nameof(Um_DlgAddTitle));
    public string Um_DlgEditTitle    => T(nameof(Um_DlgEditTitle));
    public string Um_DlgResetTitle   => T(nameof(Um_DlgResetTitle));
    public string Um_DlgDeleteTitle  => T(nameof(Um_DlgDeleteTitle));
    public string Um_DlgDeleteMsg    => T(nameof(Um_DlgDeleteMsg));
    public string Um_FieldUsername   => T(nameof(Um_FieldUsername));
    public string Um_FieldDisplayName => T(nameof(Um_FieldDisplayName));
    public string Um_FieldPassword   => T(nameof(Um_FieldPassword));
    public string Um_FieldNewPassword => T(nameof(Um_FieldNewPassword));
    public string Um_FieldRole       => T(nameof(Um_FieldRole));
    public string Um_Save            => T(nameof(Um_Save));
    public string Um_Cancel          => T(nameof(Um_Cancel));
    public string Um_Confirm         => T(nameof(Um_Confirm));
    public string Um_RoleAdmin       => T(nameof(Um_RoleAdmin));
    public string Um_RoleOperator    => T(nameof(Um_RoleOperator));
    public string Um_RoleViewer      => T(nameof(Um_RoleViewer));
    public string Um_Empty           => T(nameof(Um_Empty));
    public string Um_ErrUsernameEmpty => T(nameof(Um_ErrUsernameEmpty));
    public string Um_ErrPasswordEmpty => T(nameof(Um_ErrPasswordEmpty));
    public string Um_ErrUserExists    => T(nameof(Um_ErrUserExists));
    public string Um_ErrUserNotFound  => T(nameof(Um_ErrUserNotFound));
    public string Um_ErrLastAdmin     => T(nameof(Um_ErrLastAdmin));
    public string Um_ErrInvalidRole   => T(nameof(Um_ErrInvalidRole));

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
            [nameof(Nav_LearningAnalytic)] = "Learning Analytic",
            [nameof(Nav_AI)]           = "AI Chat",

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
            [nameof(Ui_Menu_EarlyAccess)]   = "Early Access (include prereleases)",
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

            [nameof(Account_LoggedInAs)]     = "Logged in as",
            [nameof(Account_NotLoggedInYet)] = "Not logged in yet",
            [nameof(Account_Logout)]         = "Logout",

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

            [nameof(Live_Header)]      = "LIVE - INSTRUMENTATION CONTROL AND OPTIMIZATION LABORATORY",
            [nameof(Live_CamInfo)]     = "1080p - 30fps",
            [nameof(Live_Waiting)]     = "Waiting for Camera Connection...",
            [nameof(Live_LiveBtn)]     = "Live",
            [nameof(Live_SelectCamera)] = "Select camera",
            [nameof(Live_NoCamera)]     = "No camera detected",
            [nameof(Live_CameraDenied)] = "Camera access denied",
            [nameof(Live_CameraError)]  = "Camera preview failed: {0}",
            [nameof(Live_CameraStop)]   = "Stop",
            [nameof(Hmi_Header)]       = "HMI - HEAT EXCHANGER",
            [nameof(Hmi_SelectSource)] = "Select source",
            [nameof(Hmi_RefreshSources)] = "Refresh",
            [nameof(Hmi_StopShare)]    = "Stop",
            [nameof(Hmi_NoSource)]     = "No source",
            [nameof(Hmi_SourceSelected)] = "Source selected",
            [nameof(Hmi_SourceClosed)] = "Shared source was closed.",
            [nameof(Hmi_SelectPlaceholder)] = "Select a LabVIEW HMI window or display to show here.",
            [nameof(Hmi_CaptureUnsupported)] = "Screen sharing is not supported on this device.",
            [nameof(Hmi_CaptureError)] = "Screen sharing failed: {0}",
            [nameof(Hmi_CaptureErrorUnknown)] = "Screen sharing failed.",
            [nameof(Hmi_WaitingStream)] = "Waiting for the server stream...",

            [nameof(Share_TabBroadcast)] = "Broadcast",
            [nameof(Share_TabConnect)]   = "Connect",
            [nameof(Share_Port)]         = "Port",
            [nameof(Share_Token)]        = "Access token",
            [nameof(Share_ShareCamera)]  = "Share camera",
            [nameof(Share_ShareHmi)]     = "Share HMI screen",
            [nameof(Share_Start)]        = "Start server",
            [nameof(Share_Stop)]         = "Stop server",
            [nameof(Share_Running)]      = "Server running on port {0}",
            [nameof(Share_Stopped)]      = "Server stopped",
            [nameof(Share_Clients)]      = "{0} client(s) connected",
            [nameof(Share_LocalAddress)]   = "LAN: {0}",
            [nameof(Share_PublicAddress)]  = "Public: {0}",
            [nameof(Share_PublicFetching)] = "Public IP: fetching...",
            [nameof(Share_PublicFailed)]   = "Public IP: unavailable (no internet?)",
            [nameof(Share_ServerHost)]   = "Server address (host:port)",
            [nameof(Share_Connect)]      = "Connect",
            [nameof(Share_Disconnect)]   = "Disconnect",
            [nameof(Share_Connected)]    = "Connected to {0}",
            [nameof(Share_Disconnected)] = "Not connected",
            [nameof(Share_ConnError)]    = "Connection failed: {0}",
            [nameof(Share_ServerAccount)] = "Server session",
            [nameof(Share_NotLoggedIn)]   = "Not signed in to a server",
            [nameof(Share_LoginAgain)]    = "Sign in / switch server",
            [nameof(Share_AuthHint)]      = "Clients sign in with their own user accounts. Manage accounts in the User Management page.",

            // ── Firewall / NAT ──
            [nameof(Share_FwTitle)]     = "Windows Firewall",
            [nameof(Share_FwActive)]    = "Inbound rule active — LAN clients can connect.",
            [nameof(Share_FwMissing)]   = "No inbound rule found for this port. LAN may work; public IP will be blocked.",
            [nameof(Share_FwUnknown)]   = "Could not check firewall status.",
            [nameof(Share_FwAddBtn)]    = "Add firewall exception (requires Admin)…",
            [nameof(Share_FwAdding)]    = "Adding firewall rule…",
            [nameof(Share_FwAddFailed)] = "Failed to add rule — make sure you approved the Admin prompt.",
            [nameof(Share_NatHint)]     = "Public IP access also requires port forwarding on your router: forward external port {0} → this machine's LAN IP:{0}.",
            [nameof(Share_TunnelHint)]  = "For campus/university networks (Eduroam, client-isolated WiFi): run \"cloudflared tunnel --url http://localhost:{0}\" on this machine. Clients enter the generated *.trycloudflare.com address — no port forwarding or VPN needed.",

            // ── Cloudflare Tunnel ──
            [nameof(Tunnel_Title)]        = "Cloudflare Tunnel",
            [nameof(Tunnel_Start)]        = "Start tunnel",
            [nameof(Tunnel_Stop)]         = "Stop tunnel",
            [nameof(Tunnel_Starting)]     = "Starting tunnel…",
            [nameof(Tunnel_Running)]      = "Tunnel active",
            [nameof(Tunnel_Stopped)]      = "Tunnel stopped",
            [nameof(Tunnel_Error)]        = "Error: {0}",
            [nameof(Tunnel_NotFound)]     = "cloudflared.exe not bundled. Click below to download it once (~35 MB) — it will be saved and reused.",
            [nameof(Tunnel_Download)]     = "Download cloudflared…",
            [nameof(Tunnel_Downloading)]  = "Downloading cloudflared… {0}%",
            [nameof(Tunnel_DownloadFail)] = "Download failed. Check your internet connection and try again.",
            [nameof(Tunnel_CopyUrl)]      = "Copy",
            [nameof(Tunnel_Copied)]       = "Copied!",
            [nameof(Tunnel_ShareHint)]    = "Share this URL with clients — works from any network (Eduroam, home, mobile) without port forwarding or VPN.",
            [nameof(Tunnel_UseCustomDomain)] = "Use a custom domain (fixed address)",
            [nameof(Tunnel_CustomDomainDesc)] = "A quick tunnel's address changes every run. Use your own domain for a permanent URL. The domain must already be added to Cloudflare (free) with its nameservers pointing to Cloudflare. Sign in once below; the app will create the tunnel and DNS record automatically.",
            [nameof(Tunnel_DomainLabel)]  = "Custom domain (hostname)",
            [nameof(Tunnel_DomainHint)]   = "e.g. tlig.example.com",
            [nameof(Tunnel_Login)]        = "Connect domain (sign in)",
            [nameof(Tunnel_Relogin)]      = "Sign in again",
            [nameof(Tunnel_LoggingIn)]    = "Waiting for browser sign-in… authorize your domain in the page that opened.",
            [nameof(Tunnel_LoggedIn)]     = "Signed in to Cloudflare ✓",
            [nameof(Tunnel_NotLoggedIn)]  = "Not signed in yet.",
            [nameof(Tunnel_LoginUrlHint)] = "If the browser didn't open, go to: {0}",
            [nameof(Tunnel_DomainRequired)] = "Enter a custom domain first.",

            // ── Settings page ──
            [nameof(Nav_Settings)]            = "Settings",
            [nameof(Settings_Subtitle)]       = "Full configuration for this server — broadcast, OPC UA, and AI. The title-bar flyout offers the same options for quick changes.",
            [nameof(Settings_BroadcastSection)] = "Broadcast",
            [nameof(Nav_Broadcast)]       = "Broadcast",
            [nameof(Bcast_Subtitle)]      = "Broadcast this server's camera + HMI screen and expose it to clients.",
            [nameof(Bcast_ServerSection)] = "Share server",
            [nameof(Bcast_TunnelDesc)]    = "Expose the server to the public internet without port forwarding or a VPN — ideal for campus networks (Eduroam). cloudflared is bundled with this app.",

            // ── Login (credential connect) ──
            [nameof(Login_ServerAddress)]     = "Server address",
            [nameof(Login_ServerAddressHint)] = "192.168.1.10:8088  or  abc.trycloudflare.com",
            [nameof(Login_Connecting)]        = "Connecting to server…",
            [nameof(Login_ErrorNoServer)]     = "Please enter the server address.",
            [nameof(Login_ErrorUnreachable)]  = "Cannot reach the server. Check the address and make sure the server is running.",

            // ── Signup (client self-registration) ──
            [nameof(Signup_Title)]               = "Create account",
            [nameof(Signup_Subtitle)]            = "Sign up with your @its.ac.id email",
            [nameof(Signup_Email)]               = "Email",
            [nameof(Signup_EmailHint)]           = "name@its.ac.id",
            [nameof(Signup_ConfirmPassword)]     = "Confirm password",
            [nameof(Signup_ConfirmPasswordHint)] = "Re-enter password",
            [nameof(Signup_Submit)]              = "Register",
            [nameof(Signup_BackToLogin)]         = "Back to sign in",
            [nameof(Signup_CreateAccountLink)]   = "Don't have an account? Sign up",
            [nameof(Signup_Success)]             = "Account created. Please sign in.",
            [nameof(Signup_ErrEmpty)]            = "Email and password cannot be empty.",
            [nameof(Signup_ErrEmailFormat)]      = "Enter a valid email address.",
            [nameof(Signup_ErrEmailDomain)]      = "Email must use an @its.ac.id address (subdomains allowed).",
            [nameof(Signup_ErrExists)]           = "An account with this email already exists.",
            [nameof(Signup_ErrPasswordMismatch)] = "Passwords do not match.",
            [nameof(Signup_ErrPasswordShort)]    = "Password must be at least 6 characters.",
            [nameof(Signup_ErrUnknown)]          = "Could not create the account. Please try again.",

            // ── User Management ──
            [nameof(Nav_UserManagement)] = "Users",
            [nameof(Um_Title)]      = "User Management",
            [nameof(Um_Subtitle)]   = "Manage the accounts that can sign in to this server.",
            [nameof(Um_AddUser)]    = "Add user",
            [nameof(Um_ColUser)]    = "Username",
            [nameof(Um_ColName)]    = "Display name",
            [nameof(Um_ColRole)]    = "Role",
            [nameof(Um_ColStatus)]  = "Status",
            [nameof(Um_ColLastLogin)] = "Last login",
            [nameof(Um_Enabled)]    = "Enabled",
            [nameof(Um_Disabled)]   = "Disabled",
            [nameof(Um_Enable)]     = "Enable",
            [nameof(Um_Disable)]    = "Disable",
            [nameof(Um_ResetPassword)] = "Reset password",
            [nameof(Um_Edit)]       = "Edit",
            [nameof(Um_Delete)]     = "Delete",
            [nameof(Um_Never)]      = "Never",
            [nameof(Um_DlgAddTitle)]    = "Add user",
            [nameof(Um_DlgEditTitle)]   = "Edit user",
            [nameof(Um_DlgResetTitle)]  = "Reset password",
            [nameof(Um_DlgDeleteTitle)] = "Delete user",
            [nameof(Um_DlgDeleteMsg)]   = "Delete user \"{0}\"? This cannot be undone.",
            [nameof(Um_FieldUsername)]    = "Username",
            [nameof(Um_FieldDisplayName)] = "Display name",
            [nameof(Um_FieldPassword)]    = "Password",
            [nameof(Um_FieldNewPassword)] = "New password",
            [nameof(Um_FieldRole)]        = "Role",
            [nameof(Um_Save)]    = "Save",
            [nameof(Um_Cancel)]  = "Cancel",
            [nameof(Um_Confirm)] = "Confirm",
            [nameof(Um_RoleAdmin)]    = "Administrator",
            [nameof(Um_RoleOperator)] = "Operator",
            [nameof(Um_RoleViewer)]   = "Viewer",
            [nameof(Um_Empty)]        = "No users yet.",
            [nameof(Um_ErrUsernameEmpty)] = "Username cannot be empty.",
            [nameof(Um_ErrPasswordEmpty)] = "Password cannot be empty.",
            [nameof(Um_ErrUserExists)]    = "A user with that name already exists.",
            [nameof(Um_ErrUserNotFound)]  = "User not found.",
            [nameof(Um_ErrLastAdmin)]     = "There must be at least one enabled administrator.",
            [nameof(Um_ErrInvalidRole)]   = "Invalid role.",

            [nameof(Learn_Title)]       = "LEARNING ANALYTIC",
            [nameof(Learn_Status)]      = "Learning Status",
            [nameof(Learn_ModelQuality)] = "Model Quality",
            [nameof(Learn_Accuracy)]    = "Accuracy",
            [nameof(Learn_Loss)]        = "Loss",
            [nameof(Learn_Dataset)]     = "Dataset",
            [nameof(Learn_LastTraining)] = "Last Training",
            [nameof(Learn_Waiting)]     = "Waiting for training data",
            [nameof(Learn_TrainingTrend)] = "Training Trend",
            [nameof(Learn_FeatureImpact)] = "Feature Impact",
            [nameof(Learn_InsightQueue)] = "Insight Queue",
            [nameof(Learn_DataCoverage)] = "Data Coverage",
            [nameof(Learn_AnomalyScore)] = "Anomaly Score",
            [nameof(Learn_OpenFull)]    = "Open Learning Analytic",
            [nameof(Learn_Insight1)]    = "No closed-loop learning session has been recorded yet.",
            [nameof(Learn_Insight2)]    = "Connect live data to start collecting training samples.",
            [nameof(Learn_Insight3)]    = "PID response and process stability will appear here.",

            [nameof(LearnDash_Subtitle)]       = "Track your learning progress",
            [nameof(LearnDash_OverallPerf)]    = "Overall performance",
            [nameof(LearnDash_CompletionRate)] = "Course completion rate",
            [nameof(LearnDash_ProLearner)]     = "PRO LEARNER",
            [nameof(LearnDash_TotalEnroll)]    = "Total enroll courses",
            [nameof(LearnDash_CourseCompleted)] = "Course completed",
            [nameof(LearnDash_HoursSpent)]     = "Hours spent",
            [nameof(LearnDash_HoursSpentSub)]  = "Total hours spent in courses",
            [nameof(LearnDash_LiveAttended)]   = "Live class attended",
            [nameof(LearnDash_QuizPractised)]  = "Quiz practised",
            [nameof(LearnDash_AssignmentDone)] = "Assignment done",
            [nameof(LearnDash_TotalCourses)]   = "Total courses",
            [nameof(LearnDash_ColCourseName)]  = "Course name",
            [nameof(LearnDash_ColProgress)]    = "Progress",
            [nameof(LearnDash_ColScore)]       = "Overall score",
            [nameof(LearnDash_ColStatus)]      = "Status",
            [nameof(LearnDash_InProgress)]     = "In progress",
            [nameof(LearnDash_Completed)]      = "Completed",
            [nameof(LearnDash_Chapter)]        = "chapter",
            [nameof(LearnDash_Lecture)]        = "lecture",
            [nameof(LearnDash_Assignment)]     = "Assignment",
            [nameof(LearnDash_View)]           = "View",
            [nameof(LearnDash_Upload)]         = "Upload",
            [nameof(LearnDash_SubmitBefore)]   = "Submit before",
            [nameof(LearnDash_PendingQuizzes)] = "Pending quizzes",
            [nameof(LearnDash_SeeAll)]         = "See all",
            [nameof(LearnDash_Start)]          = "Start",
            [nameof(LearnDash_Question)]       = "question",
            [nameof(LearnDash_Min)]            = "min",

            [nameof(Ctl_Header)]       = "CONTROL",
            [nameof(Ctl_Setpoint)]     = "Setpoint Temperature",
            [nameof(Ctl_Mode)]         = "Mode",
            [nameof(Ctl_Manual)]       = "Manual",
            [nameof(Ctl_Auto)]         = "Auto",
            [nameof(Ctl_Cascade)]      = "Cascade",
            [nameof(Ctl_Run)]          = "RUN",
            [nameof(Ctl_Stop)]         = "STOP",
            [nameof(Ctl_Reset)]        = "RESET",
            [nameof(Ctl_EStop)]        = "E-STOP",

            [nameof(Sys_StatusTitle)]  = "STATUS SYSTEM",
            [nameof(Sys_Plc)]          = "PLC",
            [nameof(Sys_Camera)]       = "Camera",
            [nameof(Sys_Sensor)]       = "Sensor",
            [nameof(Sys_AiAssistant)]  = "AI Assistant",
            [nameof(Sys_Online)]       = "Online",
            [nameof(Sys_Active)]       = "Active",
            [nameof(Sys_Offline)]      = "Offline",
            [nameof(Sys_Inactive)]     = "Inactive",
            [nameof(Alarm_Title)]      = "ALARM",
            [nameof(Alarm_None)]       = "No Active Alarm",

            [nameof(Blk_Setpoint)]     = "Setpoint",
            [nameof(Blk_Pid)]          = "PID",
            [nameof(Blk_Plant)]        = "Plant",
            [nameof(Resp_Step)]        = "Step Response",

            [nameof(Ai_QuickSuggestion)]    = "Quick Suggestion",
            [nameof(Ai_SugTuningPid)]       = "Tuning PID",
            [nameof(Ai_SugOvershoot)]       = "Analysis Overshoot",
            [nameof(Ai_SugHeatExchanger)]   = "Heat Exchanger Theory",
            [nameof(Ai_SugTroubleshooting)] = "Troubleshooting",
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
            [nameof(Nav_LearningAnalytic)] = "Learning Analytic",
            [nameof(Nav_AI)]           = "AI Chat",

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
            [nameof(Ui_Menu_EarlyAccess)]   = "Early Access (termasuk prerelease)",
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

            [nameof(Account_LoggedInAs)]     = "Masuk sebagai",
            [nameof(Account_NotLoggedInYet)] = "Belum login",
            [nameof(Account_Logout)]         = "Keluar",

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

            [nameof(Live_Header)]      = "LIVE - LABORATORIUM INSTRUMENTASI KONTROL DAN OPTIMASI",
            [nameof(Live_CamInfo)]     = "1080p - 30fps",
            [nameof(Live_Waiting)]     = "Menunggu Koneksi Kamera...",
            [nameof(Live_LiveBtn)]     = "Live",
            [nameof(Live_SelectCamera)] = "Pilih kamera",
            [nameof(Live_NoCamera)]     = "Tidak ada kamera terdeteksi",
            [nameof(Live_CameraDenied)] = "Akses kamera ditolak",
            [nameof(Live_CameraError)]  = "Preview kamera gagal: {0}",
            [nameof(Live_CameraStop)]   = "Stop",
            [nameof(Hmi_Header)]       = "HMI - HEAT EXCHANGER",
            [nameof(Hmi_SelectSource)] = "Pilih source",
            [nameof(Hmi_RefreshSources)] = "Refresh",
            [nameof(Hmi_StopShare)]    = "Stop",
            [nameof(Hmi_NoSource)]     = "Belum ada source",
            [nameof(Hmi_SourceSelected)] = "Source dipilih",
            [nameof(Hmi_SourceClosed)] = "Source share sudah ditutup.",
            [nameof(Hmi_SelectPlaceholder)] = "Pilih window LabVIEW HMI atau layar untuk ditampilkan di sini.",
            [nameof(Hmi_CaptureUnsupported)] = "Screen sharing tidak didukung di perangkat ini.",
            [nameof(Hmi_CaptureError)] = "Screen sharing gagal: {0}",
            [nameof(Hmi_CaptureErrorUnknown)] = "Screen sharing gagal.",
            [nameof(Hmi_WaitingStream)] = "Menunggu stream dari server...",

            [nameof(Share_TabBroadcast)] = "Siaran",
            [nameof(Share_TabConnect)]   = "Sambung",
            [nameof(Share_Port)]         = "Port",
            [nameof(Share_Token)]        = "Token akses",
            [nameof(Share_ShareCamera)]  = "Bagikan kamera",
            [nameof(Share_ShareHmi)]     = "Bagikan layar HMI",
            [nameof(Share_Start)]        = "Mulai server",
            [nameof(Share_Stop)]         = "Hentikan server",
            [nameof(Share_Running)]      = "Server berjalan di port {0}",
            [nameof(Share_Stopped)]      = "Server berhenti",
            [nameof(Share_Clients)]      = "{0} client terhubung",
            [nameof(Share_LocalAddress)]   = "LAN: {0}",
            [nameof(Share_PublicAddress)]  = "Publik: {0}",
            [nameof(Share_PublicFetching)] = "IP Publik: mengambil...",
            [nameof(Share_PublicFailed)]   = "IP Publik: tidak tersedia (tidak ada internet?)",
            [nameof(Share_ServerHost)]   = "Alamat server (host:port)",
            [nameof(Share_Connect)]      = "Sambungkan",
            [nameof(Share_Disconnect)]   = "Putuskan",
            [nameof(Share_Connected)]    = "Tersambung ke {0}",
            [nameof(Share_Disconnected)] = "Tidak tersambung",
            [nameof(Share_ConnError)]    = "Koneksi gagal: {0}",
            [nameof(Share_ServerAccount)] = "Sesi server",
            [nameof(Share_NotLoggedIn)]   = "Belum masuk ke server",
            [nameof(Share_LoginAgain)]    = "Masuk / ganti server",
            [nameof(Share_AuthHint)]      = "Client masuk dengan akun pengguna masing-masing. Kelola akun di halaman Manajemen Pengguna.",

            // ── Firewall / NAT ──
            [nameof(Share_FwTitle)]     = "Windows Firewall",
            [nameof(Share_FwActive)]    = "Aturan inbound aktif — client LAN bisa terhubung.",
            [nameof(Share_FwMissing)]   = "Tidak ada aturan inbound untuk port ini. LAN mungkin berjalan; IP publik akan diblokir.",
            [nameof(Share_FwUnknown)]   = "Tidak dapat memeriksa status firewall.",
            [nameof(Share_FwAddBtn)]    = "Tambah pengecualian firewall (perlu Admin)…",
            [nameof(Share_FwAdding)]    = "Menambahkan aturan firewall…",
            [nameof(Share_FwAddFailed)] = "Gagal menambah aturan — pastikan Anda menyetujui prompt Admin.",
            [nameof(Share_NatHint)]     = "Akses IP publik juga memerlukan port forwarding di router: teruskan port eksternal {0} → IP LAN mesin ini:{0}.",
            [nameof(Share_TunnelHint)]  = "Untuk jaringan kampus/eduroam (WiFi terisolasi): jalankan \"cloudflared tunnel --url http://localhost:{0}\" di mesin ini. Client masukkan alamat *.trycloudflare.com yang dihasilkan — tanpa port forwarding atau VPN.",

            // ── Cloudflare Tunnel ──
            [nameof(Tunnel_Title)]        = "Cloudflare Tunnel",
            [nameof(Tunnel_Start)]        = "Mulai tunnel",
            [nameof(Tunnel_Stop)]         = "Hentikan tunnel",
            [nameof(Tunnel_Starting)]     = "Memulai tunnel…",
            [nameof(Tunnel_Running)]      = "Tunnel aktif",
            [nameof(Tunnel_Stopped)]      = "Tunnel berhenti",
            [nameof(Tunnel_Error)]        = "Error: {0}",
            [nameof(Tunnel_NotFound)]     = "cloudflared.exe tidak ditemukan. Klik di bawah untuk mengunduh sekali (~35 MB) — akan disimpan dan digunakan kembali.",
            [nameof(Tunnel_Download)]     = "Unduh cloudflared…",
            [nameof(Tunnel_Downloading)]  = "Mengunduh cloudflared… {0}%",
            [nameof(Tunnel_DownloadFail)] = "Unduhan gagal. Periksa koneksi internet Anda dan coba lagi.",
            [nameof(Tunnel_CopyUrl)]      = "Salin",
            [nameof(Tunnel_Copied)]       = "Tersalin!",
            [nameof(Tunnel_ShareHint)]    = "Bagikan URL ini ke client — berfungsi dari jaringan apapun (Eduroam, rumah, seluler) tanpa port forwarding atau VPN.",
            [nameof(Tunnel_UseCustomDomain)] = "Gunakan domain kustom (alamat tetap)",
            [nameof(Tunnel_CustomDomainDesc)] = "Alamat quick tunnel berubah setiap kali dijalankan. Gunakan domain Anda sendiri agar URL permanen. Domain harus sudah ditambahkan ke Cloudflare (gratis) dengan nameserver mengarah ke Cloudflare. Masuk sekali di bawah; aplikasi akan membuat tunnel dan record DNS secara otomatis.",
            [nameof(Tunnel_DomainLabel)]  = "Domain kustom (hostname)",
            [nameof(Tunnel_DomainHint)]   = "mis. tlig.example.com",
            [nameof(Tunnel_Login)]        = "Hubungkan domain (masuk)",
            [nameof(Tunnel_Relogin)]      = "Masuk ulang",
            [nameof(Tunnel_LoggingIn)]    = "Menunggu login di browser… izinkan domain Anda pada halaman yang terbuka.",
            [nameof(Tunnel_LoggedIn)]     = "Sudah masuk ke Cloudflare ✓",
            [nameof(Tunnel_NotLoggedIn)]  = "Belum masuk.",
            [nameof(Tunnel_LoginUrlHint)] = "Jika browser tidak terbuka, kunjungi: {0}",
            [nameof(Tunnel_DomainRequired)] = "Masukkan domain kustom terlebih dahulu.",

            // ── Halaman pengaturan ──
            [nameof(Nav_Settings)]            = "Pengaturan",
            [nameof(Settings_Subtitle)]       = "Konfigurasi lengkap server ini — broadcast, OPC UA, dan AI. Flyout di title-bar menyediakan opsi yang sama untuk perubahan cepat.",
            [nameof(Settings_BroadcastSection)] = "Broadcast",
            [nameof(Nav_Broadcast)]       = "Siaran",
            [nameof(Bcast_Subtitle)]      = "Siarkan kamera + layar HMI server ini dan paparkan ke client.",
            [nameof(Bcast_ServerSection)] = "Server berbagi",
            [nameof(Bcast_TunnelDesc)]    = "Paparkan server ke internet publik tanpa port forwarding atau VPN — ideal untuk jaringan kampus (Eduroam). cloudflared sudah disertakan dalam aplikasi ini.",

            // ── Login (sambung dengan kredensial) ──
            [nameof(Login_ServerAddress)]     = "Alamat server",
            [nameof(Login_ServerAddressHint)] = "192.168.1.10:8088  atau  abc.trycloudflare.com",
            [nameof(Login_Connecting)]        = "Menyambung ke server…",
            [nameof(Login_ErrorNoServer)]     = "Masukkan alamat server terlebih dahulu.",
            [nameof(Login_ErrorUnreachable)]  = "Tidak dapat menjangkau server. Periksa alamat dan pastikan server berjalan.",

            // ── Pendaftaran (registrasi mandiri klien) ──
            [nameof(Signup_Title)]               = "Buat akun",
            [nameof(Signup_Subtitle)]            = "Daftar dengan email @its.ac.id Anda",
            [nameof(Signup_Email)]               = "Email",
            [nameof(Signup_EmailHint)]           = "nama@its.ac.id",
            [nameof(Signup_ConfirmPassword)]     = "Konfirmasi kata sandi",
            [nameof(Signup_ConfirmPasswordHint)] = "Masukkan ulang kata sandi",
            [nameof(Signup_Submit)]              = "Daftar",
            [nameof(Signup_BackToLogin)]         = "Kembali ke masuk",
            [nameof(Signup_CreateAccountLink)]   = "Belum punya akun? Daftar",
            [nameof(Signup_Success)]             = "Akun berhasil dibuat. Silakan masuk.",
            [nameof(Signup_ErrEmpty)]            = "Email dan kata sandi tidak boleh kosong.",
            [nameof(Signup_ErrEmailFormat)]      = "Masukkan alamat email yang valid.",
            [nameof(Signup_ErrEmailDomain)]      = "Email harus memakai alamat @its.ac.id (subdomain diperbolehkan).",
            [nameof(Signup_ErrExists)]           = "Akun dengan email ini sudah terdaftar.",
            [nameof(Signup_ErrPasswordMismatch)] = "Konfirmasi kata sandi tidak cocok.",
            [nameof(Signup_ErrPasswordShort)]    = "Kata sandi minimal 6 karakter.",
            [nameof(Signup_ErrUnknown)]          = "Gagal membuat akun. Silakan coba lagi.",

            // ── Manajemen Pengguna ──
            [nameof(Nav_UserManagement)] = "Pengguna",
            [nameof(Um_Title)]      = "Manajemen Pengguna",
            [nameof(Um_Subtitle)]   = "Kelola akun yang dapat masuk ke server ini.",
            [nameof(Um_AddUser)]    = "Tambah pengguna",
            [nameof(Um_ColUser)]    = "Nama pengguna",
            [nameof(Um_ColName)]    = "Nama tampilan",
            [nameof(Um_ColRole)]    = "Peran",
            [nameof(Um_ColStatus)]  = "Status",
            [nameof(Um_ColLastLogin)] = "Masuk terakhir",
            [nameof(Um_Enabled)]    = "Aktif",
            [nameof(Um_Disabled)]   = "Nonaktif",
            [nameof(Um_Enable)]     = "Aktifkan",
            [nameof(Um_Disable)]    = "Nonaktifkan",
            [nameof(Um_ResetPassword)] = "Atur ulang sandi",
            [nameof(Um_Edit)]       = "Ubah",
            [nameof(Um_Delete)]     = "Hapus",
            [nameof(Um_Never)]      = "Belum pernah",
            [nameof(Um_DlgAddTitle)]    = "Tambah pengguna",
            [nameof(Um_DlgEditTitle)]   = "Ubah pengguna",
            [nameof(Um_DlgResetTitle)]  = "Atur ulang sandi",
            [nameof(Um_DlgDeleteTitle)] = "Hapus pengguna",
            [nameof(Um_DlgDeleteMsg)]   = "Hapus pengguna \"{0}\"? Tindakan ini tidak dapat dibatalkan.",
            [nameof(Um_FieldUsername)]    = "Nama pengguna",
            [nameof(Um_FieldDisplayName)] = "Nama tampilan",
            [nameof(Um_FieldPassword)]    = "Kata sandi",
            [nameof(Um_FieldNewPassword)] = "Kata sandi baru",
            [nameof(Um_FieldRole)]        = "Peran",
            [nameof(Um_Save)]    = "Simpan",
            [nameof(Um_Cancel)]  = "Batal",
            [nameof(Um_Confirm)] = "Konfirmasi",
            [nameof(Um_RoleAdmin)]    = "Administrator",
            [nameof(Um_RoleOperator)] = "Operator",
            [nameof(Um_RoleViewer)]   = "Pengamat",
            [nameof(Um_Empty)]        = "Belum ada pengguna.",
            [nameof(Um_ErrUsernameEmpty)] = "Nama pengguna tidak boleh kosong.",
            [nameof(Um_ErrPasswordEmpty)] = "Kata sandi tidak boleh kosong.",
            [nameof(Um_ErrUserExists)]    = "Pengguna dengan nama itu sudah ada.",
            [nameof(Um_ErrUserNotFound)]  = "Pengguna tidak ditemukan.",
            [nameof(Um_ErrLastAdmin)]     = "Harus ada minimal satu administrator aktif.",
            [nameof(Um_ErrInvalidRole)]   = "Peran tidak valid.",

            [nameof(Learn_Title)]       = "LEARNING ANALYTIC",
            [nameof(Learn_Status)]      = "Status Learning",
            [nameof(Learn_ModelQuality)] = "Kualitas Model",
            [nameof(Learn_Accuracy)]    = "Akurasi",
            [nameof(Learn_Loss)]        = "Loss",
            [nameof(Learn_Dataset)]     = "Dataset",
            [nameof(Learn_LastTraining)] = "Training Terakhir",
            [nameof(Learn_Waiting)]     = "Menunggu data training",
            [nameof(Learn_TrainingTrend)] = "Tren Training",
            [nameof(Learn_FeatureImpact)] = "Dampak Fitur",
            [nameof(Learn_InsightQueue)] = "Antrian Insight",
            [nameof(Learn_DataCoverage)] = "Cakupan Data",
            [nameof(Learn_AnomalyScore)] = "Skor Anomali",
            [nameof(Learn_OpenFull)]    = "Buka Learning Analytic",
            [nameof(Learn_Insight1)]    = "Sesi learning closed-loop belum direkam.",
            [nameof(Learn_Insight2)]    = "Hubungkan data live untuk mulai mengumpulkan sampel training.",
            [nameof(Learn_Insight3)]    = "Respon PID dan stabilitas proses akan tampil di sini.",

            [nameof(LearnDash_Subtitle)]       = "Pantau progres belajar Anda",
            [nameof(LearnDash_OverallPerf)]    = "Performa keseluruhan",
            [nameof(LearnDash_CompletionRate)] = "Tingkat penyelesaian kursus",
            [nameof(LearnDash_ProLearner)]     = "PELAJAR PRO",
            [nameof(LearnDash_TotalEnroll)]    = "Total kursus terdaftar",
            [nameof(LearnDash_CourseCompleted)] = "Kursus selesai",
            [nameof(LearnDash_HoursSpent)]     = "Jam belajar",
            [nameof(LearnDash_HoursSpentSub)]  = "Total jam belajar di kursus",
            [nameof(LearnDash_LiveAttended)]   = "Kelas live dihadiri",
            [nameof(LearnDash_QuizPractised)]  = "Kuis dikerjakan",
            [nameof(LearnDash_AssignmentDone)] = "Tugas selesai",
            [nameof(LearnDash_TotalCourses)]   = "Total kursus",
            [nameof(LearnDash_ColCourseName)]  = "Nama kursus",
            [nameof(LearnDash_ColProgress)]    = "Progres",
            [nameof(LearnDash_ColScore)]       = "Nilai keseluruhan",
            [nameof(LearnDash_ColStatus)]      = "Status",
            [nameof(LearnDash_InProgress)]     = "Berlangsung",
            [nameof(LearnDash_Completed)]      = "Selesai",
            [nameof(LearnDash_Chapter)]        = "bab",
            [nameof(LearnDash_Lecture)]        = "materi",
            [nameof(LearnDash_Assignment)]     = "Tugas",
            [nameof(LearnDash_View)]           = "Lihat",
            [nameof(LearnDash_Upload)]         = "Unggah",
            [nameof(LearnDash_SubmitBefore)]   = "Kumpulkan sebelum",
            [nameof(LearnDash_PendingQuizzes)] = "Kuis tertunda",
            [nameof(LearnDash_SeeAll)]         = "Lihat semua",
            [nameof(LearnDash_Start)]          = "Mulai",
            [nameof(LearnDash_Question)]       = "soal",
            [nameof(LearnDash_Min)]            = "mnt",

            [nameof(Ctl_Header)]       = "KONTROL",
            [nameof(Ctl_Setpoint)]     = "Setpoint Temperatur",
            [nameof(Ctl_Mode)]         = "Mode",
            [nameof(Ctl_Manual)]       = "Manual",
            [nameof(Ctl_Auto)]         = "Auto",
            [nameof(Ctl_Cascade)]      = "Cascade",
            [nameof(Ctl_Run)]          = "JALANKAN",
            [nameof(Ctl_Stop)]         = "BERHENTI",
            [nameof(Ctl_Reset)]        = "RESET",
            [nameof(Ctl_EStop)]        = "E-STOP",

            [nameof(Sys_StatusTitle)]  = "STATUS SISTEM",
            [nameof(Sys_Plc)]          = "PLC",
            [nameof(Sys_Camera)]       = "Kamera",
            [nameof(Sys_Sensor)]       = "Sensor",
            [nameof(Sys_AiAssistant)]  = "Asisten AI",
            [nameof(Sys_Online)]       = "Online",
            [nameof(Sys_Active)]       = "Aktif",
            [nameof(Sys_Offline)]      = "Offline",
            [nameof(Sys_Inactive)]     = "Nonaktif",
            [nameof(Alarm_Title)]      = "ALARM",
            [nameof(Alarm_None)]       = "Tidak Ada Alarm Aktif",

            [nameof(Blk_Setpoint)]     = "Setpoint",
            [nameof(Blk_Pid)]          = "PID",
            [nameof(Blk_Plant)]        = "Plant",
            [nameof(Resp_Step)]        = "Respon Step",

            [nameof(Ai_QuickSuggestion)]    = "Saran Cepat",
            [nameof(Ai_SugTuningPid)]       = "Tuning PID",
            [nameof(Ai_SugOvershoot)]       = "Analisis Overshoot",
            [nameof(Ai_SugHeatExchanger)]   = "Teori Heat Exchanger",
            [nameof(Ai_SugTroubleshooting)] = "Troubleshooting",
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
            ["Ui_ServerOpcNotConnected"] = "OPC UA: NOT CONNECTED",
            ["Ui_ServerOpcHint"] = "OPC UA not connected — open the connection flyout to connect to a PLC/OPC UA server",
            ["Ui_ClientNotConnected"] = "SERVER: NOT CONNECTED",
            ["Ui_ClientNotConnectedHint"] = "Not connected to TLIG Dashboard Server — open the connection flyout and enter the server address",
            ["Ui_ClientConnected"] = "SERVER: {0}",

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
            ["Ui_ServerOpcNotConnected"] = "OPC UA: TIDAK TERHUBUNG",
            ["Ui_ServerOpcHint"] = "OPC UA belum terhubung — buka flyout koneksi untuk menyambung ke PLC/server OPC UA",
            ["Ui_ClientNotConnected"] = "SERVER: TIDAK TERHUBUNG",
            ["Ui_ClientNotConnectedHint"] = "Belum terhubung ke TLIG Dashboard Server — buka flyout koneksi dan masukkan alamat server",
            ["Ui_ClientConnected"] = "SERVER: {0}",

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
