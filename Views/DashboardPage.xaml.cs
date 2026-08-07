using System.Reflection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TLIGDashboard.Helpers;
using TLIGDashboard.Services;
using TLIGDashboard.Services.ControlEngineering;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using Windows.UI;

namespace TLIGDashboard.Views;

public sealed partial class DashboardPage : Page
{
    private LocalizationManager Lang => App.Lang;
    private SystemStatusService Status => App.Status;

    // Shared AI service — same instance as AIPage
    private AiService _ai => App.Ai;
    private CancellationTokenSource? _chatCts;

    private readonly bool _clientMode = BuildInfo.IsClient;

    private readonly SemaphoreSlim _dashboardCameraSwitchLock = new(1, 1);
    private DeviceInformationCollection? _dashboardCameraDevices;
    private MediaCapture? _dashboardMediaCapture;
    private MediaFrameSource? _dashboardFrameSource;
    private MediaPlayer? _dashboardMediaPlayer;
    private bool _isDashboardCameraPopulating;
    private bool _isDashboardPageActive;

    private bool _dragging1, _dragging2;
    private double _dragStartX;
    private double _leftStartW, _centerStartW, _rightStartW;

    private double _ratioL = 0.31, _ratioC = 0.40, _ratioR = 0.29;

    // Horizontal splitter inside center panel
    private bool _draggingH;
    private double _dragStartY;
    private double _topStartH, _bottomStartH;
    private double _ratioTop = 0.55, _ratioBottom = 0.45;

    public DashboardPage()
    {
        InitializeComponent();
        // Keep page cached so layout & chat bubbles survive navigation
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        WirePidInputs();
        Loaded += OnLoaded;
    }

    // How many history entries are already rendered in ChatPanel.
    private int _renderedCount;
    private ElementTheme _renderedTheme = ElementTheme.Default;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        var cursorH = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        SetCursor(Splitter1, cursorH);
        SetCursor(Splitter2, cursorH);
        var cursorV = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
        SetCursor(HSplitter, cursorV);

        double total = AvailableWidth;
        if (total > 0)
        {
            double left = Math.Floor(total * _ratioL);
            double center = Math.Floor(total * _ratioC);
            SetColumnWidths(left, center, total - left - center);
        }

        ActualThemeChanged += OnActualThemeChanged;

        // Subscribe to simulation type changes so all HMI labels update.
        App.SimType.SimulationTypeChanged += OnSimulationTypeChanged;
        ApplySimulationType(App.SimType.CurrentType);

        _ = RespChart.InitializeAsync();

        ApplyLearningPanelContent();
        App.Session.Changed += OnSessionChanged;
    }

    // Progress tracking in the bottom "Learning Analytic" panel is
    // student-facing: on the Server flavor and for staff (Dosen/Asisten) on
    // the Client the panel shows the Challenge Learning manager instead.
    private static bool StaffLearningPanel =>
        BuildInfo.IsServer || (App.Session.IsSignedIn && App.Session.IsStaff);

    private void OnSessionChanged()
        => DispatcherQueue.TryEnqueue(ApplyLearningPanelContent);

    private void ApplyLearningPanelContent()
    {
        bool staff = StaffLearningPanel;
        DashLearningView.Visibility   = staff ? Visibility.Collapsed : Visibility.Visible;
        DashChallengeFrame.Visibility = staff ? Visibility.Visible   : Visibility.Collapsed;
        if (staff && DashChallengeFrame.Content is null)
            DashChallengeFrame.Navigate(typeof(ChallengeLearningPage));
    }

    private void OnSimulationTypeChanged(object? sender, Services.SimulationType type)
        => DispatcherQueue.TryEnqueue(() => ApplySimulationType(type));

    private void ApplySimulationType(Services.SimulationType type)
    {
        var svc = App.SimType;
        // The block diagram + transfer function now depict the fixed cascade lab plant (two
        // loops, Gp1/Gp2) and are decoupled from the System Model selector — as the transfer-
        // function card already was. Only the Control panel's setpoint label/unit follow it.
        if (CtlSetpointLabel != null) CtlSetpointLabel.Text = svc.SetpointLabel;
        if (CtlSetpointUnit  != null) CtlSetpointUnit.Text  = svc.ProcessVariableUnit;
    }

    // ── Smart PID Designer (AI-assisted tuning + RK4 step-response chart) ──
    //
    // The Control panel edits the cascade's OUTER temperature PID (Kp/Ki/Kd) + setpoint;
    // the inner flow PI stays at its SIMC values and is edited on the Cascade page, which is
    // this panel's extended screen. Gains, setpoint and the last run live in App.CascadeSession
    // (shared with that page), not in this page. This page keeps one extra job of its own —
    // folding the advisor's review into the AI chat on the right, which only runs started here do.

    // Guards the echo when PullPidInputs() writes the boxes.
    private bool _syncingPidInputs;

    // Subscribed only while this page is navigated to — the Cascade page owns the
    // rendering while it is up. `-=` first keeps each single if navigation unbalances.
    private void SubscribePidSession()
    {
        var s = App.CascadeSession;
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
        var s = App.CascadeSession;
        s.ResultChanged         -= OnPidResultChanged;
        s.RunningChanged        -= OnPidRunningChanged;
        s.RunFailed             -= OnPidRunFailed;
        s.RecommendationCleared -= OnPidRecommendationCleared;
    }

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
        // The Control panel edits the cascade's OUTER temperature PID + setpoint; the inner
        // flow PI keeps its session values (edited on the Cascade page).
        var s = App.CascadeSession;
        s.OuterKp  = KpBox.Value;
        s.OuterKi  = KiBox.Value;
        s.OuterKd  = KdBox.Value;
        s.Setpoint = CtlSetpointBox.Value;
    }

    private void PullPidInputs()
    {
        var s = App.CascadeSession;
        _syncingPidInputs = true;
        KpBox.Value          = s.OuterKp;
        KiBox.Value          = s.OuterKi;
        KdBox.Value          = s.OuterKd;
        CtlSetpointBox.Value = s.Setpoint;
        _syncingPidInputs = false;
    }

    // RUN in the Control card is the designer's "Simulate": it runs the RK4 step-response
    // preview for the cascade's current gains, diagnoses the temperature loop by calculation
    // (PidDiagnosisCalculator), searches the simulator for better gains (CascadeRecommender),
    // and asks the AI Advisor (LLM) to review and explain them.
    private async void CtlRun_Click(object sender, RoutedEventArgs e) => await RunPidAsync();

    private async Task RunPidAsync()
    {
        PushPidInputs();

        // One RUN drives the cascade session that both this panel and the Cascade page show.
        // Fires ResultChanged (-> RenderCascadeResult) / RunFailed on the way through.
        var result = await App.CascadeSession.RunAsync();
        PullPidInputs();  // pick up the normalized setpoint
        if (result is not null) FoldAdvisorIntoChat(result);
    }

    private void OnPidResultChanged(object? sender, CascadeDesignResult result)
        => DispatcherQueue.TryEnqueue(() => RenderCascadeResult(result));

    private void OnPidRunningChanged(object? sender, bool running)
        => DispatcherQueue.TryEnqueue(() => CtlRunBtn.IsEnabled = !running);

    private void OnPidRunFailed(object? sender, EventArgs e)
        => DispatcherQueue.TryEnqueue(() =>
        {
            AddChatBubble("ai", Lang.Pid_ErrorUnavailable);
            ScrollChat();
        });

    private void OnPidRecommendationCleared(object? sender, EventArgs e)
        => DispatcherQueue.TryEnqueue(() => PidAdvisorPanel.Visibility = Visibility.Collapsed);

    /// <summary>
    /// Draws a run into the panel. Safe to call repeatedly for the same result — it is
    /// also how the page catches up on a run started from the Cascade page.
    /// </summary>
    private void RenderCascadeResult(CascadeDesignResult result)
    {
        var sim = result.Simulation;
        // Cascade runs adaptively (~thousands of samples); thin for the chart, metrics use the
        // full arrays. The panel plots the temperature response vs the setpoint, same as the
        // single-loop chart did — the full two-loop view lives on the Cascade page.
        int stride = System.Math.Max(1, sim.Time.Length / 1500);
        RespChart.Update(Sample(sim.Time, stride), Sample(sim.Temperature, stride), result.Input.Setpoint);

        // result.Metrics is read off the exact RK4 curve above — always consistent with what's
        // plotted (the temperature step metrics of the outer loop).
        var m = result.Metrics;
        RiseTimeValue.Text  = m.RiseTime.ToString("0.00");
        OvershootValue.Text = m.Overshoot.ToString("0.0");
        SettlingValue.Text  = m.SettlingTime.ToString("0.00");
        SteadyErrValue.Text = m.SteadyStateError.ToString("0.000");

        IaeValue.Text  = FormatIndex(m.IAE);
        IseValue.Text  = FormatIndex(m.ISE);
        ItaeValue.Text = FormatIndex(m.ITAE);

        // result.Diagnosis is a code, not display text — localize it here so a Client shows its
        // own language. The outer loop is the identical Gp1 plant PidDiagnosisCalculator is
        // anchored to (see CascadeDesignService).
        DiagnosisValue.Text = string.IsNullOrEmpty(result.Diagnosis)
            ? "--" : PidDiagnosisCalculator.Describe(result.Diagnosis, m.PrimaryStepMetrics());

        // Read the pending gains from the session, not from result: a decline made on the
        // Cascade page must stay declined here too. The panel shows the outer gains it edits;
        // accepting also resets the inner PI to SIMC (see CascadeRecommender).
        if (App.CascadeSession.PendingRecommendation is { } rec)
        {
            PidAdvisorText.Text = Lang.Pid_AdvisorPrompt;
            PidAdvisorRecommendationText.Text = $"Kp={rec.OuterKp:F3}  Ki={rec.OuterKi:F3}  Kd={rec.OuterKd:F3}";
            PidAdvisorPanel.Visibility = Visibility.Visible;
        }
        else
        {
            PidAdvisorPanel.Visibility = Visibility.Collapsed;
        }
    }

    // Compact form for the (often large) error-index integrals, e.g. 7399 -> "7.4k", 1.8e6 -> "1.80M".
    private static string FormatIndex(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return "--";
        double a = System.Math.Abs(v);
        if (a >= 1e6) return (v / 1e6).ToString("0.00") + "M";
        if (a >= 1e3) return (v / 1e3).ToString("0.0") + "k";
        return v.ToString("0.0");
    }

    // Every stride-th sample, always keeping the last point so the curve reaches the end.
    private static double[] Sample(double[] a, int stride)
    {
        if (a.Length == 0 || stride <= 1) return a;
        var list = new System.Collections.Generic.List<double>(a.Length / stride + 2);
        for (int i = 0; i < a.Length; i += stride) list.Add(a[i]);
        if ((a.Length - 1) % stride != 0) list.Add(a[a.Length - 1]);
        return list.ToArray();
    }

    /// <summary>
    /// Puts the advisor's review in the chat panel and folds the exchange into App.Ai's
    /// own history (not just a visual bubble) so a follow-up question typed in the chat
    /// box below carries the actual simulation numbers as context instead of the LLM
    /// answering blind. Only ever called for a run started from this page — replaying it
    /// on navigation would re-post bubbles for a run the student already saw.
    /// </summary>
    private void FoldAdvisorIntoChat(CascadeDesignResult result)
    {
        if (string.IsNullOrWhiteSpace(result.AdvisorExplanation)) return;

        AddChatBubble("ai", result.AdvisorExplanation);
        ScrollChat();

        // The synthetic "user" turn is never rendered as its own bubble — bump
        // _renderedCount past it so SyncBubblesWithHistory() doesn't re-render it
        // as a message the student never actually typed.
        var g = result.Input;
        var m = result.Metrics;
        App.Ai.AddHistoryEntry("user",
            $"[Ringkasan simulasi RUN cascade] Setpoint={g.Setpoint:F1}°C, " +
            $"OUTER Kp={g.OuterKp:F3}, Ki={g.OuterKi:F3}, Kd={g.OuterKd:F3}; " +
            $"INNER Kp={g.InnerKp:F3}, Ki={g.InnerKi:F3} " +
            $"-> hasil simulasi RK4: Overshoot={m.Overshoot:F2}%, Rise Time={m.RiseTime:F3}s, " +
            $"Settling Time={m.SettlingTime:F2}s, Steady-State Error={m.SteadyStateError:F3}. " +
            $"Diagnosis: {PidDiagnosisCalculator.Describe(result.Diagnosis, m.PrimaryStepMetrics())}");
        App.Ai.AddHistoryEntry("assistant", result.AdvisorExplanation);
        _renderedCount = App.Ai.History.Count;
    }

    // "Ya (Terapkan)" — fills Kp/Ki/Kd with the Advisor's recommendation and
    // immediately re-runs the simulation (Auto-Run), same as the JS-driven
    // apply-and-rerun a web frontend would do, just via native C# instead of DOM.
    private async void PidAdvisorAccept_Click(object sender, RoutedEventArgs e)
    {
        var result = await App.CascadeSession.AcceptRecommendationAsync();
        PullPidInputs();
        if (result is not null) FoldAdvisorIntoChat(result);
    }

    // "Tidak" — leaves the gains untouched so the student can keep tuning manually.
    private void PidAdvisorDecline_Click(object sender, RoutedEventArgs e)
        => App.CascadeSession.ClearRecommendation();

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        if (_chatCts != null) return; // don't disrupt active streaming
        ClearChatPanel();
        SyncBubblesWithHistory();
    }

    protected override async void OnNavigatedTo(
        Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _isDashboardPageActive = true;

        // (Re)subscribe on every navigation — OnNavigatedFrom unsubscribes and the
        // page is cached (NavigationCacheMode.Required), so a Loaded-time
        // subscription would not survive leaving and re-entering the page.
        // `-=` first keeps it single even if navigation events ever unbalance.
        App.SimType.SimulationTypeChanged -= OnSimulationTypeChanged;
        App.SimType.SimulationTypeChanged += OnSimulationTypeChanged;
        ApplySimulationType(App.SimType.CurrentType);

        SubscribePidSession();
        // Catch up on anything run from the Cascade page while this page was away.
        // The button state has to be re-synced by hand: a run in flight while we were
        // unsubscribed would otherwise leave RUN disabled for good.
        CtlRunBtn.IsEnabled = !App.CascadeSession.IsRunning;
        PullPidInputs();
        if (App.CascadeSession.LastResult is { } last) RenderCascadeResult(last);

        // If theme changed while this page was not in the visual tree, force full re-render.
        if (_renderedCount > 0 && _renderedTheme != ActualTheme)
            ClearChatPanel();
        SyncBubblesWithHistory();
        _ = ModelPicker.ReloadAsync();

        if (_clientMode)
            EnterDashboardCameraClientMode();
        else
            await PopulateDashboardCameraListAsync();
    }

    protected override async void OnNavigatedFrom(
        Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _isDashboardPageActive = false;
        App.SimType.SimulationTypeChanged -= OnSimulationTypeChanged;
        UnsubscribePidSession();

        if (_clientMode)
        {
            ShareClient.Instance.FrameReceived -= OnRemoteDashboardCameraFrame;
            return;
        }

        await _dashboardCameraSwitchLock.WaitAsync();
        try
        {
            StopDashboardCameraPreview(updateUi: false);
        }
        finally
        {
            _dashboardCameraSwitchLock.Release();
        }
    }

    // ── Client mode: render camera frames received from the server ────────────

    private void EnterDashboardCameraClientMode()
    {
        DashboardCameraSelector.Visibility = Visibility.Collapsed;
        DashboardCameraInfoText.Text = "-";
        if (DashboardCameraReceiveImage.Source is null)
            ShowDashboardCameraPlaceholder(Lang.Hmi_WaitingStream);

        ShareClient.Instance.FrameReceived -= OnRemoteDashboardCameraFrame;
        ShareClient.Instance.FrameReceived += OnRemoteDashboardCameraFrame;
    }

    private void OnRemoteDashboardCameraFrame(byte channel, byte[] bytes)
    {
        if (channel != ShareProtocol.ChannelCamera) return;
        DispatcherQueue.TryEnqueue(async () => await RenderRemoteDashboardCameraAsync(bytes));
    }

    private async Task RenderRemoteDashboardCameraAsync(byte[] bytes)
    {
        try
        {
            var bitmap = new BitmapImage();
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
            DashboardCameraReceiveImage.Source = bitmap;
            DashboardCameraReceiveImage.Visibility = Visibility.Visible;
            DashboardCameraPlaceholder.Visibility = Visibility.Collapsed;
            App.Status.CameraConnected = true;
        }
        catch { /* drop a bad frame */ }
    }

    /// <summary>Appends any history not yet shown in the Dashboard chat panel.</summary>
    private void SyncBubblesWithHistory()
    {
        var history = App.Ai.History;
        for (int i = _renderedCount; i < history.Count; i++)
        {
            var msg = history[i];
            if (msg.Role == "user")
            {
                AddChatBubble("user", msg.Content);
            }
            else if (msg.Role == "assistant")
            {
                var (border, _) = AddChatBubble("ai", msg.Content);
                border.Child = MarkdownRenderer.Render(
                    msg.Content, 12, ActualTheme == ElementTheme.Dark);
            }
        }
        _renderedCount = history.Count;
        _renderedTheme = ActualTheme;
    }

    /// <summary>Called by AIPage clear button to reset this panel too.</summary>
    public void ClearChatPanel()
    {
        while (ChatPanel.Children.Count > 0)
            ChatPanel.Children.RemoveAt(ChatPanel.Children.Count - 1);
        _renderedCount = 0;
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double total = AvailableWidth;
        if (total <= 0) return;

        double minL = total * 0.25, minC = total * 0.32, minR = total * 0.23;
        double L = Math.Max(_ratioL * total, minL);
        double R = Math.Max(_ratioR * total, minR);
        double C = total - L - R;

        if (C < minC)
        {
            C = minC;
            double lrTotal = L + R;
            if (lrTotal > total - minC)
            {
                double excess = lrTotal - (total - minC);
                L = Math.Max(minL, L - excess * (L / lrTotal));
                R = Math.Max(minR, R - excess * (R / lrTotal));
            }
        }

        LeftColumn.Width   = new GridLength(L, GridUnitType.Pixel);
        CenterColumn.Width = new GridLength(C, GridUnitType.Pixel);
        RightColumn.Width  = new GridLength(R, GridUnitType.Pixel);

        // Re-apply vertical split ratio whenever the center panel height changes
        ApplyCenterRowHeights();
    }

    private double AvailableWidth =>
        RootGrid.ActualWidth > 8 ? RootGrid.ActualWidth - 8 : 0;

    // ── Horizontal splitter (top/bottom inside CenterPanel) ─────────────

    private double CenterAvailableH =>
        CenterPanel.ActualHeight > 38 ? CenterPanel.ActualHeight - 4 - 30 : 0; // subtract splitter(4) + header(30)

    private void ApplyCenterRowHeights()
    {
        double avail = CenterAvailableH;
        if (avail <= 0) return;
        double minTop    = avail * 0.20;
        double minBottom = avail * 0.15;
        double top    = Math.Max(_ratioTop    * avail, minTop);
        double bottom = Math.Max(_ratioBottom * avail, minBottom);
        double sum = top + bottom;
        if (sum > 0) { top = top / sum * avail; bottom = avail - top; }
        CenterTopRow.Height    = new GridLength(top,    GridUnitType.Pixel);
        CenterBottomRow.Height = new GridLength(bottom, GridUnitType.Pixel);
    }

    private void SetCenterRowHeights(double top, double bottom)
    {
        double sum = top + bottom;
        if (sum > 0) { _ratioTop = top / sum; _ratioBottom = bottom / sum; }
        CenterTopRow.Height    = new GridLength(top,    GridUnitType.Pixel);
        CenterBottomRow.Height = new GridLength(bottom, GridUnitType.Pixel);
    }

    private void HSplitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(HSplitter).Properties.IsLeftButtonPressed) return;
        _draggingH    = true;
        _dragStartY   = e.GetCurrentPoint(CenterPanel).Position.Y;
        _topStartH    = CenterTopRow.ActualHeight;
        _bottomStartH = CenterBottomRow.ActualHeight;
        (sender as UIElement)?.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void HSplitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingH) return;
        double delta = e.GetCurrentPoint(CenterPanel).Position.Y - _dragStartY;
        ApplyHSplitter(delta);
        e.Handled = true;
    }

    private void HSplitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _draggingH = false;
        (sender as UIElement)?.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void HSplitter_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        => _draggingH = false;

    private void ApplyHSplitter(double delta)
    {
        double avail = CenterAvailableH;
        if (avail <= 0) return;
        double minTop    = avail * 0.20;
        double minBottom = avail * 0.15;

        double top    = Math.Clamp(_topStartH    + delta, minTop,    avail - minBottom);
        double bottom = Math.Clamp(_bottomStartH - delta, minBottom, avail - minTop);
        // Correct floating-point drift
        if (top + bottom != avail) bottom = avail - top;
        SetCenterRowHeights(top, bottom);
    }

    private void SetColumnWidths(double L, double C, double R)
    {
        double sum = L + C + R;
        if (sum > 0) { _ratioL = L / sum; _ratioC = C / sum; _ratioR = R / sum; }
        LeftColumn.Width   = new GridLength(L, GridUnitType.Pixel);
        CenterColumn.Width = new GridLength(C, GridUnitType.Pixel);
        RightColumn.Width  = new GridLength(R, GridUnitType.Pixel);
    }

    // ── Splitter 1 (between Left and Center) ────────────────────────────
    private void Splitter1_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(Splitter1).Properties.IsLeftButtonPressed) return;
        _dragging1    = true;
        _dragStartX   = e.GetCurrentPoint(RootGrid).Position.X;
        _leftStartW   = LeftPanel.ActualWidth;
        _centerStartW = CenterPanel.ActualWidth;
        _rightStartW  = RightPanel.ActualWidth;
        (sender as UIElement)?.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Splitter1_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging1) return;
        ApplySplitter1(e.GetCurrentPoint(RootGrid).Position.X - _dragStartX);
        e.Handled = true;
    }

    private void Splitter1_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragging1 = false;
        (sender as UIElement)?.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void Splitter1_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        => _dragging1 = false;

    private void ApplySplitter1(double delta)
    {
        double total = AvailableWidth;
        if (total <= 0) return;
        double minL = total * 0.25, minC = total * 0.32, minR = total * 0.23;

        double L = _leftStartW + delta;

        if (delta <= 0)
        {
            // Drag left: left shrinks → only center grows, right fixed
            L = Math.Max(L, minL);
            double C = Math.Max(_centerStartW + (_leftStartW - L), minC);
            double R = _rightStartW;
            C = Math.Min(C, total - L - R);
            if (C < minC) C = minC;
            L = total - C - R;
            SetColumnWidths(L, C, R);
        }
        else
        {
            // Drag right: left grows → center + right shrink proportionally
            L = Math.Min(L, total - minC - minR);
            double actualGain = L - _leftStartW;
            double crSum = _centerStartW + _rightStartW;
            if (crSum <= 0) return;
            double C = _centerStartW - actualGain * (_centerStartW / crSum);
            double R = _rightStartW  - actualGain * (_rightStartW  / crSum);
            if (C < minC) { C = minC; R = total - L - C; }
            if (R < minR) { R = minR; C = total - L - R; }
            if (C < minC) { C = minC; L = total - C - R; }
            SetColumnWidths(L, C, R);
        }
    }

    // ── Splitter 2 (between Center and Right) ───────────────────────────
    private void Splitter2_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(Splitter2).Properties.IsLeftButtonPressed) return;
        _dragging2    = true;
        _dragStartX   = e.GetCurrentPoint(RootGrid).Position.X;
        _leftStartW   = LeftPanel.ActualWidth;
        _centerStartW = CenterPanel.ActualWidth;
        _rightStartW  = RightPanel.ActualWidth;
        (sender as UIElement)?.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Splitter2_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging2) return;
        ApplySplitter2(e.GetCurrentPoint(RootGrid).Position.X - _dragStartX);
        e.Handled = true;
    }

    private void Splitter2_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragging2 = false;
        (sender as UIElement)?.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void Splitter2_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        => _dragging2 = false;

    private void ApplySplitter2(double delta)
    {
        double total = AvailableWidth;
        if (total <= 0) return;
        double minL = total * 0.25, minC = total * 0.32, minR = total * 0.23;

        // delta > 0: splitter moved right → right shrinks
        // delta < 0: splitter moved left  → right grows
        double R = _rightStartW - delta;

        if (delta >= 0)
        {
            // Drag right: right shrinks → only center grows, left fixed
            R = Math.Max(R, minR);
            double C = Math.Max(_centerStartW + (_rightStartW - R), minC);
            double L = _leftStartW;
            C = Math.Min(C, total - L - R);
            if (C < minC) C = minC;
            R = total - L - C;
            SetColumnWidths(L, C, R);
        }
        else
        {
            // Drag left: right grows → left + center shrink proportionally
            R = Math.Min(R, total - minL - minC);
            double actualGain = R - _rightStartW;
            double lcSum = _leftStartW + _centerStartW;
            if (lcSum <= 0) return;
            double L = _leftStartW   - actualGain * (_leftStartW   / lcSum);
            double C = _centerStartW - actualGain * (_centerStartW / lcSum);
            if (L < minL) { L = minL; C = total - L - R; }
            if (C < minC) { C = minC; L = total - C - R; }
            if (L < minL) { L = minL; R = total - L - C; }
            SetColumnWidths(L, C, R);
        }
    }

    // ── Chat (right panel) ───────────────────────────────────────────────
    // ── Dashboard camera preview ──────────────────────────────────────────
    private async Task PopulateDashboardCameraListAsync()
    {
        _isDashboardCameraPopulating = true;
        DashboardCameraSelector.Items.Clear();
        DashboardCameraSelector.IsEnabled = false;
        ShowDashboardCameraPlaceholder(Lang.Live_Waiting);

        try
        {
            _dashboardCameraDevices = await DeviceInformation.FindAllAsync(MediaDevice.GetVideoCaptureSelector());

            if (_dashboardCameraDevices.Count == 0)
            {
                DashboardCameraSelector.Items.Add(Lang.Live_NoCamera);
                DashboardCameraSelector.SelectedIndex = 0;
                ShowDashboardCameraPlaceholder(Lang.Live_NoCamera);
                return;
            }

            foreach (var device in _dashboardCameraDevices)
                DashboardCameraSelector.Items.Add(device.Name);

            DashboardCameraSelector.IsEnabled = true;
            DashboardCameraSelector.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            DashboardCameraSelector.Items.Add(Lang.Live_NoCamera);
            DashboardCameraSelector.SelectedIndex = 0;
            ShowDashboardCameraPlaceholder(Lang.Format(nameof(LocalizationManager.Live_CameraError), ex.Message));
            return;
        }
        finally
        {
            _isDashboardCameraPopulating = false;
        }

        await StartSelectedDashboardCameraAsync(0);
    }

    private async void DashboardCameraSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isDashboardCameraPopulating ||
            DashboardCameraSelector.SelectedIndex < 0 ||
            _dashboardCameraDevices is null ||
            DashboardCameraSelector.SelectedIndex >= _dashboardCameraDevices.Count)
        {
            return;
        }

        await StartSelectedDashboardCameraAsync(DashboardCameraSelector.SelectedIndex);
    }

    private async Task StartSelectedDashboardCameraAsync(int cameraIndex)
    {
        if (!_isDashboardPageActive ||
            _dashboardCameraDevices is null ||
            cameraIndex < 0 ||
            cameraIndex >= _dashboardCameraDevices.Count)
        {
            return;
        }

        await _dashboardCameraSwitchLock.WaitAsync();
        try
        {
            StopDashboardCameraPreview(updateUi: false);
            ShowDashboardCameraPlaceholder(Lang.Live_Waiting);

            var selectedCamera = _dashboardCameraDevices[cameraIndex];
            _dashboardMediaCapture = new MediaCapture();

            var settings = new MediaCaptureInitializationSettings
            {
                VideoDeviceId = selectedCamera.Id,
                SharingMode = MediaCaptureSharingMode.SharedReadOnly,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Auto
            };

            await _dashboardMediaCapture.InitializeAsync(settings);

            _dashboardFrameSource = FindDashboardPreviewFrameSource(_dashboardMediaCapture);
            if (_dashboardFrameSource is null)
            {
                ShowDashboardCameraPlaceholder(Lang.Live_NoCamera);
                StopDashboardCameraPreview(updateUi: false);
                return;
            }

            _dashboardMediaPlayer = new MediaPlayer
            {
                RealTimePlayback = true,
                AutoPlay = false,
                Source = MediaSource.CreateFromMediaFrameSource(_dashboardFrameSource)
            };
            _dashboardMediaPlayer.MediaFailed += DashboardMediaPlayer_MediaFailed;

            DashboardCameraPreview.SetMediaPlayer(_dashboardMediaPlayer);
            _dashboardMediaPlayer.Play();

            if (!_isDashboardPageActive)
            {
                StopDashboardCameraPreview(updateUi: false);
                return;
            }

            DashboardCameraPreview.Visibility = Visibility.Visible;
            DashboardCameraPlaceholder.Visibility = Visibility.Collapsed;
            DashboardCameraInfoText.Text = FormatDashboardCameraInfo(_dashboardFrameSource);
            App.Status.CameraConnected = true;
        }
        catch (UnauthorizedAccessException)
        {
            StopDashboardCameraPreview(updateUi: false);
            ShowDashboardCameraPlaceholder(Lang.Live_CameraDenied);
        }
        catch (Exception ex)
        {
            StopDashboardCameraPreview(updateUi: false);
            ShowDashboardCameraPlaceholder(Lang.Format(nameof(LocalizationManager.Live_CameraError), ex.Message));
        }
        finally
        {
            _dashboardCameraSwitchLock.Release();
        }
    }

    private static MediaFrameSource? FindDashboardPreviewFrameSource(MediaCapture mediaCapture)
    {
        var previewSource = mediaCapture.FrameSources
            .FirstOrDefault(source =>
                source.Value.Info.MediaStreamType == MediaStreamType.VideoPreview &&
                source.Value.Info.SourceKind == MediaFrameSourceKind.Color)
            .Value;

        if (previewSource is not null)
            return previewSource;

        return mediaCapture.FrameSources
            .FirstOrDefault(source =>
                source.Value.Info.MediaStreamType == MediaStreamType.VideoRecord &&
                source.Value.Info.SourceKind == MediaFrameSourceKind.Color)
            .Value;
    }

    private static string FormatDashboardCameraInfo(MediaFrameSource frameSource)
    {
        var format = frameSource.CurrentFormat;
        double fps = 0;
        if (format.FrameRate.Denominator != 0)
            fps = (double)format.FrameRate.Numerator / format.FrameRate.Denominator;

        string fpsText = fps > 0
            ? Math.Round(fps).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "0";

        return $"{format.VideoFormat.Width}x{format.VideoFormat.Height} - {fpsText}fps";
    }

    private void StopDashboardCameraPreview(bool updateUi)
    {
        if (_dashboardMediaPlayer is not null)
        {
            _dashboardMediaPlayer.MediaFailed -= DashboardMediaPlayer_MediaFailed;
            _dashboardMediaPlayer.Pause();
            DashboardCameraPreview.SetMediaPlayer(null);
            _dashboardMediaPlayer.Dispose();
            _dashboardMediaPlayer = null;
        }

        _dashboardMediaCapture?.Dispose();
        _dashboardMediaCapture = null;
        _dashboardFrameSource = null;
        App.Status.CameraConnected = false;

        if (updateUi)
            ShowDashboardCameraPlaceholder(Lang.Live_Waiting);
    }

    private void ShowDashboardCameraPlaceholder(string message)
    {
        DashboardCameraPreview.Visibility = Visibility.Collapsed;
        DashboardCameraPlaceholder.Visibility = Visibility.Visible;
        DashboardCameraPlaceholderText.Text = message;
        DashboardCameraInfoText.Text = "-";
        App.Status.CameraConnected = false;
    }

    private void DashboardMediaPlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            StopDashboardCameraPreview(updateUi: false);
            ShowDashboardCameraPlaceholder(Lang.Format(nameof(LocalizationManager.Live_CameraError), args.ErrorMessage));
        });
    }

    private void ChatSend_Click(object sender, RoutedEventArgs e) => _ = SendChatAsync();

    private void ChatInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter &&
            !InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            _ = SendChatAsync();
            e.Handled = true;
        }
    }

    private void QuickSuggestion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;

        string? prompt = button.Tag as string ?? button.Content?.ToString();
        if (string.IsNullOrWhiteSpace(prompt)) return;

        ChatInput.Text = prompt.Trim();
        _ = SendChatAsync();
    }

    private async Task SendChatAsync()
    {
        string text = ChatInput.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        // Re-point the shared AI service at the active provider/model (same as AIPage).
        AiConfigService.ApplyActive(_ai);

        if (string.IsNullOrEmpty(_ai.ApiKey))
        {
            AddChatBubble("ai", Lang.Ai_ErrorNoKey);
            return;
        }

        ChatInput.Text        = "";
        ChatSendBtn.IsEnabled = false;

        AddChatBubble("user", text);
        var (aiBubbleBorder, aiBubble) = AddChatBubble("ai", Lang.Ai_Thinking);

        _chatCts = new CancellationTokenSource();
        bool hasContent = false;
        string? errorMsg = null;

        try
        {
            await Task.Run(async () =>
            {
                await _ai.StreamChatAsync(text, token =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!hasContent) { aiBubble.Text = ""; hasContent = true; }
                        aiBubble.Text += token;
                        ScrollChat();
                    });
                }, _chatCts.Token);
            }, _chatCts.Token);
        }
        catch (OperationCanceledException) { errorMsg = "[Dihentikan]"; }
        catch (Exception ex)               { errorMsg = $"⚠ {ex.Message}"; }

        if (errorMsg != null)
            aiBubble.Text = errorMsg;
        else if (!hasContent)
            aiBubble.Text = "⚠ Tidak ada konten — periksa model & API key.";
        else
            aiBubbleBorder.Child = MarkdownRenderer.Render(
                aiBubble.Text, 12, ActualTheme == ElementTheme.Dark);

        _chatCts?.Dispose();
        _chatCts = null;
        ChatSendBtn.IsEnabled = true;
        ScrollChat();

        // Keep rendered count in sync with history
        _renderedCount = App.Ai.History.Count;
    }

    // Returns the bubble Border and the streaming TextBlock so callers can replace content after streaming.
    private (Border border, TextBlock tb) AddChatBubble(string role, string text)
    {
        bool isUser = role == "user";
        bool isDark = ActualTheme == ElementTheme.Dark;

        var label = new TextBlock
        {
            Text             = isUser ? Lang.Get("Ai_UserLabel") : Lang.Get("Ai_AiLabel"),
            FontSize         = 10,
            FontWeight       = Microsoft.UI.Text.FontWeights.SemiBold,
            CharacterSpacing = 60,
            Opacity          = 0.55,
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin           = new Thickness(2, 0, 2, 2)
        };

        var bg = isUser
            ? new SolidColorBrush(isDark
                ? Color.FromArgb(0xFF, 0x00, 0x4E, 0x9B)
                : Color.FromArgb(0xFF, 0xCC, 0xE4, 0xFF))
            : new SolidColorBrush(isDark
                ? Color.FromArgb(0xFF, 0x2C, 0x2C, 0x2C)
                : Color.FromArgb(0xFF, 0xF0, 0xF0, 0xF0));

        var tb = new TextBlock
        {
            Text                   = text,
            FontSize               = 12,
            TextWrapping           = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        };

        var bubble = new Border
        {
            Background          = bg,
            CornerRadius        = isUser ? new CornerRadius(10,10,2,10) : new CornerRadius(10,10,10,2),
            Padding             = new Thickness(10, 7, 10, 7),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth            = 240,
            Child               = tb
        };

        var container = new StackPanel
        {
            Spacing             = 3,
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left
        };
        container.Children.Add(label);
        container.Children.Add(bubble);
        ChatPanel.Children.Add(container);
        ScrollChat();
        return (bubble, tb);
    }

    private void ScrollChat() =>
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low,
            () => ChatScroll.ChangeView(null, double.MaxValue, null, true));

    // ── Fullscreen buttons ───────────────────────────────────────────────
    private void LeftFullscreen_Click(object sender, RoutedEventArgs e)
        => App.CurrentWindow?.NavigateToPage("Cascade");

    private void CenterFullscreen_Click(object sender, RoutedEventArgs e)
        => App.CurrentWindow?.NavigateToPage("LiveView");

    private void LearningAnalyticFullscreen_Click(object sender, RoutedEventArgs e)
        => App.CurrentWindow?.NavigateToPage("LearningAnalytic");

    private void RightFullscreen_Click(object sender, RoutedEventArgs e)
        => App.CurrentWindow?.NavigateToPage("AI");

    // ── Cursor helper (ProtectedCursor is non-public in WinUI 3) ────────
    private static void SetCursor(UIElement element, InputCursor cursor)
        => typeof(UIElement)
            .GetProperty("ProtectedCursor",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(element, cursor);
}
