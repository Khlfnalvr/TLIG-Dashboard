using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TLIGDashboard.Helpers;
using TLIGDashboard.Models;
using TLIGDashboard.Services;
using TLIGDashboard.Services.ControlEngineering;

namespace TLIGDashboard.Views;

public sealed partial class ParameterPage : Page
{
    private LocalizationManager Lang   => App.Lang;
    private SystemStatusService Status => App.Status;

    public ParameterPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        WirePidInputs();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _ = RespChart.InitializeAsync();
#if CLIENT
        RefreshParamCounter();
#endif
    }

#if CLIENT
    private void RefreshParamCounter()
    {
        int used = ActivityStore.Instance.GetAll()
            .Count(a => a.Category == ActivityCategory.ControlParameter);
        int remaining = Math.Max(0, 3 - used);
        bool limitReached = used >= 3;

        ParamCounterBadge.Visibility = Visibility.Visible;
        ParamCounterText.Text = $"{Math.Min(used, 3)}/3";
        ParamCounterBadge.Background = limitReached
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 80, 20, 20))
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(60, 255, 255, 255));
        ParamCounterText.Foreground = limitReached
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 100, 100))
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 200, 200, 200));
        ApplyPidBtn.IsEnabled = !limitReached;
    }
#endif

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // (Re)subscribe on every navigation — OnNavigatedFrom unsubscribes and the page
        // is cached (NavigationCacheMode.Required), so a Loaded-time subscription would
        // not survive leaving and re-entering the page. `-=` first keeps it single even
        // if navigation events ever unbalance.
        App.SimType.SimulationTypeChanged -= OnSimulationTypeChanged;
        App.SimType.SimulationTypeChanged += OnSimulationTypeChanged;
        ApplySimulationType(App.SimType.CurrentType);

        SubscribePidSession();
        // Catch up on anything run from the Dashboard's System Model panel while this
        // page was away — this page is that panel's extended screen, not a separate one.
        // The button state has to be re-synced by hand: a run in flight while we were
        // unsubscribed would otherwise leave RUN disabled for good.
        RunBtn.IsEnabled = !App.PidSession.IsRunning;
        PullPidInputs();
        if (App.PidSession.LastResult is { } last) RenderPidResult(last);
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        App.SimType.SimulationTypeChanged -= OnSimulationTypeChanged;
        UnsubscribePidSession();
    }

    private void OnSimulationTypeChanged(object? sender, SimulationType type)
        => DispatcherQueue.TryEnqueue(() => ApplySimulationType(type));

    private void ApplySimulationType(SimulationType type)
    {
        var svc = App.SimType;
        if (BlkSetpointLabel != null) BlkSetpointLabel.Text = svc.SetpointLabel;
        if (BlkPlantLabel    != null) BlkPlantLabel.Text    = svc.PlantLabel;
        if (CtlSetpointLabel != null) CtlSetpointLabel.Text = svc.SetpointLabel;
        if (CtlSetpointUnit  != null) CtlSetpointUnit.Text  = svc.ProcessVariableUnit;
        // NOTE: TransferFunctionText is intentionally NOT updated here — that card shows
        // the Smart PID Designer's fixed plant (see PidSimulator.cs), not the selected
        // System Model's own transfer function. Same rule as the Dashboard.
    }

    // ── PID Apply ────────────────────────────────────────────────────────────

    private void ApplyPid_Click(object sender, RoutedEventArgs e)
    {
#if CLIENT
        int paramUsed = ActivityStore.Instance.GetAll()
            .Count(a => a.Category == ActivityCategory.ControlParameter);
        if (paramUsed >= 3)
        {
            ParamLimitInfoBar.IsOpen = true;
            return;
        }
        ParamLimitInfoBar.IsOpen = false;
#endif

        double kp = double.IsNaN(KpBox.Value) ? 0 : KpBox.Value;
        double ki = double.IsNaN(KiBox.Value) ? 0 : KiBox.Value;
        double kd = double.IsNaN(KdBox.Value) ? 0 : KdBox.Value;

        // Tulis parameter ke PLC melalui HMI LabVIEW (TCP) bila terhubung.
        App.ViewModel?.Plc.WriteTags(("kp", kp), ("ki", ki), ("kd", kd));

        ActivityStore.Instance.LogSession(
            ActivityCategory.ControlParameter,
            ActivityActions.ParameterChanged,
            $"Parameter PID diterapkan: Kp={kp:F2}, Ki={ki:F2}, Kd={kd:F2}",
            metadata: new()
            {
                ["Kp"] = kp.ToString("F2"),
                ["Ki"] = ki.ToString("F2"),
                ["Kd"] = kd.ToString("F2"),
                ["system"] = App.SimType.CurrentType.ToString(),
            });

        ShowApplyFeedback();
#if CLIENT
        RefreshParamCounter();
#endif
    }

    // ── Simulation Run / Stop ────────────────────────────────────────────────

    // RUN drives the real process over TCP *and* runs the Smart PID Designer's RK4
    // step-response preview for the Kp/Ki/Kd in the boxes above — the same simulate
    // path as the Dashboard's Control card, which this page is the extended screen of.
    private async void RunBtn_Click(object sender, RoutedEventArgs e)
    {
        double kp = double.IsNaN(KpBox.Value) ? 0 : KpBox.Value;
        double ki = double.IsNaN(KiBox.Value) ? 0 : KiBox.Value;
        double kd = double.IsNaN(KdBox.Value) ? 0 : KdBox.Value;

        // Perintahkan HMI LabVIEW menjalankan proses (cmd=run) bila terhubung.
        App.ViewModel?.Plc.WriteTag("cmd", "run");

        ActivityStore.Instance.LogSession(
            ActivityCategory.Simulation,
            ActivityActions.SimulationStarted,
            $"Simulasi {App.SimType.CurrentType} dimulai: Kp={kp:F2}, Ki={ki:F2}, Kd={kd:F2}",
            metadata: new()
            {
                ["Kp"] = kp.ToString("F2"),
                ["Ki"] = ki.ToString("F2"),
                ["Kd"] = kd.ToString("F2"),
                ["system"] = App.SimType.CurrentType.ToString(),
            });

        await RunPidAsync();
    }

    private void StopBtn_Click(object sender, RoutedEventArgs e)
    {
        // Perintahkan HMI LabVIEW menghentikan proses (cmd=stop) bila terhubung.
        App.ViewModel?.Plc.WriteTag("cmd", "stop");

        ActivityStore.Instance.LogSession(
            ActivityCategory.Simulation,
            ActivityActions.SimulationCompleted,
            $"Simulasi {App.SimType.CurrentType} dihentikan");
    }

    // ── Smart PID Designer (AI-assisted tuning + RK4 step-response chart) ────
    //
    // Gains, setpoint and the last run live in App.PidSession, shared with the
    // Dashboard's System Model panel that this page is the extended screen of.

    // Guards the echo when PullPidInputs() writes the boxes.
    private bool _syncingPidInputs;

    /// <summary>
    /// Attached here rather than via ValueChanged= in XAML: the markup assigns Value on
    /// each box as it is parsed, which fires the handler while the boxes declared after
    /// it are still null — taking the page down with a XamlParseException on startup.
    /// </summary>
    private void WirePidInputs()
    {
        // Ki=0.015 needs more than the NumberBox default 2 fraction digits; a
        // significant-digits formatter shows small gains exactly without trailing zeros.
        var gainFmt = new Windows.Globalization.NumberFormatting.DecimalFormatter
        {
            IntegerDigits = 1,
            FractionDigits = 0,
            NumberRounder = new Windows.Globalization.NumberFormatting.SignificantDigitsNumberRounder { SignificantDigits = 5 },
        };
        foreach (var box in new[] { KpBox, KiBox, KdBox, CtlSetpointBox })
            box.NumberFormatter = gainFmt;

        KpBox.ValueChanged          += PidInput_ValueChanged;
        KiBox.ValueChanged          += PidInput_ValueChanged;
        KdBox.ValueChanged          += PidInput_ValueChanged;
        CtlSetpointBox.ValueChanged += PidInput_ValueChanged;
    }

    private void PidInput_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncingPidInputs) return;
        PushPidInputs();
    }

    private void PushPidInputs()
    {
        var s = App.PidSession;
        s.Kp       = KpBox.Value;
        s.Ki       = KiBox.Value;
        s.Kd       = KdBox.Value;
        s.Setpoint = CtlSetpointBox.Value;
    }

    private void PullPidInputs()
    {
        var s = App.PidSession;
        _syncingPidInputs = true;
        KpBox.Value          = s.Kp;
        KiBox.Value          = s.Ki;
        KdBox.Value          = s.Kd;
        CtlSetpointBox.Value = s.Setpoint;
        _syncingPidInputs = false;
    }

    // Subscribed only while this page is navigated to. `-=` first keeps each single
    // even if navigation events ever unbalance.
    private void SubscribePidSession()
    {
        var s = App.PidSession;
        s.ResultChanged         -= OnPidResultChanged;
        s.ResultChanged         += OnPidResultChanged;
        s.RunningChanged        -= OnPidRunningChanged;
        s.RunningChanged        += OnPidRunningChanged;
        s.RunFailed             -= OnPidRunFailed;
        s.RunFailed             += OnPidRunFailed;
        s.RecommendationCleared -= OnPidRecommendationCleared;
        s.RecommendationCleared += OnPidRecommendationCleared;
    }

    private void UnsubscribePidSession()
    {
        var s = App.PidSession;
        s.ResultChanged         -= OnPidResultChanged;
        s.RunningChanged        -= OnPidRunningChanged;
        s.RunFailed             -= OnPidRunFailed;
        s.RecommendationCleared -= OnPidRecommendationCleared;
    }

    private void OnPidResultChanged(object? sender, PidDesignResult result)
        => DispatcherQueue.TryEnqueue(() =>
        {
            PidErrorInfoBar.IsOpen = false;
            RenderPidResult(result);
        });

    private void OnPidRunningChanged(object? sender, bool running)
        => DispatcherQueue.TryEnqueue(() => RunBtn.IsEnabled = !running);

    private void OnPidRunFailed(object? sender, EventArgs e)
        => DispatcherQueue.TryEnqueue(() =>
        {
            PidErrorInfoBar.Message = Lang.Pid_ErrorUnavailable;
            PidErrorInfoBar.IsOpen  = true;
        });

    private void OnPidRecommendationCleared(object? sender, EventArgs e)
        => DispatcherQueue.TryEnqueue(() => PidAdvisorPanel.Visibility = Visibility.Collapsed);

    private async Task RunPidAsync()
    {
        PushPidInputs();
        // Fires ResultChanged (-> RenderPidResult) / RunFailed on the way through.
        await App.PidSession.RunAsync();
        PullPidInputs();  // pick up the normalized setpoint
    }

    /// <summary>
    /// Draws a run into the panel. Safe to call repeatedly for the same result — it is
    /// also how the page catches up on a run started from the Dashboard.
    /// </summary>
    private void RenderPidResult(PidDesignResult result)
    {
        RespChart.Update(result.Simulation.Time, result.Simulation.Amplitude, result.Setpoint);

        // result.Metrics is read off the exact RK4 curve above — always consistent with
        // what's plotted.
        RiseTimeValue.Text  = result.Metrics.RiseTime.ToString("0.00");
        OvershootValue.Text = result.Metrics.Overshoot.ToString("0.0");
        SettlingValue.Text  = result.Metrics.SettlingTime.ToString("0.00");
        SteadyErrValue.Text = result.Metrics.SteadyStateError.ToString("0.000");

        // result.Diagnosis is a code, not display text — localize it here so a Client
        // shows its own language rather than whatever the Server is set to.
        DiagnosisValue.Text = string.IsNullOrEmpty(result.Diagnosis)
            ? "--" : PidDiagnosisCalculator.Describe(result.Diagnosis, result.Metrics);

        // The Dashboard routes the advisor's prose into its chat panel and folds it into
        // App.Ai history; this page has no chat, so it renders the text in the card and
        // deliberately leaves App.Ai history alone — injecting a synthetic "user" turn
        // from here would surface as a message the student never typed once the (cached)
        // Dashboard re-syncs its bubbles from history.
        bool hasExplanation = !string.IsNullOrWhiteSpace(result.AdvisorExplanation);
        PidAdvisorExplanationHost.Child = hasExplanation
            ? MarkdownRenderer.Render(result.AdvisorExplanation, 12, ActualTheme == ElementTheme.Dark)
            : null;

        // Read the pending gains from the session, not from result: a decline made on
        // the Dashboard must stay declined here too.
        var pending = App.PidSession.PendingRecommendation;
        if (pending is { } rec)
        {
            PidAdvisorText.Text = Lang.Pid_AdvisorPrompt;
            PidAdvisorRecommendationText.Text = $"Kp={rec.Kp:F3}  Ki={rec.Ki:F3}  Kd={rec.Kd:F3}";
            PidAdvisorActions.Visibility = Visibility.Visible;
        }
        else
        {
            PidAdvisorActions.Visibility = Visibility.Collapsed;
        }

        PidAdvisorPanel.Visibility = hasExplanation || pending is not null
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // "Ya (Terapkan)" — fills Kp/Ki/Kd with the Advisor's recommendation and
    // immediately re-runs the simulation (Auto-Run).
    private async void PidAdvisorAccept_Click(object sender, RoutedEventArgs e)
    {
        await App.PidSession.AcceptRecommendationAsync();
        PullPidInputs();
    }

    // "Tidak" — leaves Kp/Ki/Kd untouched so the student can keep tuning manually.
    private void PidAdvisorDecline_Click(object sender, RoutedEventArgs e)
        => App.PidSession.ClearRecommendation();

    // ── Feedback visual saat Terapkan diklik ─────────────────────────────────

    private async void ShowApplyFeedback()
    {
        var original = ApplyPidBtn.Content;
        ApplyPidBtn.IsEnabled = false;
        ApplyPidBtn.Content = new Microsoft.UI.Xaml.Controls.StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new FontIcon
                {
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"),
                    Glyph = "", FontSize = 11
                },
                new TextBlock { Text = "Diterapkan!" }
            }
        };
        await System.Threading.Tasks.Task.Delay(1500);
        ApplyPidBtn.Content   = original;
        ApplyPidBtn.IsEnabled = true;
    }
}
