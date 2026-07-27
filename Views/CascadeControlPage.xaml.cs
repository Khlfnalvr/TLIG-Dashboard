using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TLIGDashboard.Helpers;
using TLIGDashboard.Services;
using TLIGDashboard.Services.ControlEngineering;

namespace TLIGDashboard.Views;

/// <summary>
/// Cascade Control designer: an outer temperature PID whose output is the setpoint of an
/// inner flow PI. Runs the RK4 <see cref="CascadeSimulator"/> for the current gains,
/// shows the temperature + flow response with a single-loop baseline, and surfaces the
/// AI diagnosis / advisor. State is shared through <see cref="CascadeSessionService"/>
/// (App.CascadeSession) so a run survives navigation (the page is cached).
/// </summary>
public sealed partial class CascadeControlPage : Page
{
    private LocalizationManager Lang => App.Lang;

    public CascadeControlPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        WireInputs();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _ = RespChart.InitializeAsync();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        SubscribeSession();
        RunBtn.IsEnabled = !App.CascadeSession.IsRunning;
        PullInputs();
        if (App.CascadeSession.LastResult is { } last) RenderResult(last);
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        UnsubscribeSession();
    }

    // ── Input wiring ─────────────────────────────────────────────────────────

    private bool _syncing;

    private void WireInputs()
    {
        // Gains like Ki=0.015 need more than the NumberBox default 2 fraction digits;
        // a significant-digits formatter shows them exactly without trailing-zero noise.
        var fmt = new Windows.Globalization.NumberFormatting.DecimalFormatter
        {
            IntegerDigits = 1,
            FractionDigits = 0,
            NumberRounder = new Windows.Globalization.NumberFormatting.SignificantDigitsNumberRounder
            {
                SignificantDigits = 5,
            },
        };
        foreach (var box in new[] { OuterKpBox, OuterKiBox, OuterKdBox, InnerKpBox, InnerKiBox, SetpointBox, DisturbanceBox })
            box.NumberFormatter = fmt;

        OuterKpBox.ValueChanged     += Input_ValueChanged;
        OuterKiBox.ValueChanged     += Input_ValueChanged;
        OuterKdBox.ValueChanged     += Input_ValueChanged;
        InnerKpBox.ValueChanged     += Input_ValueChanged;
        InnerKiBox.ValueChanged     += Input_ValueChanged;
        SetpointBox.ValueChanged    += Input_ValueChanged;
        DisturbanceBox.ValueChanged += Input_ValueChanged;
    }

    private void Input_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncing) return;
        PushInputs();
    }

    private void PushInputs()
    {
        var s = App.CascadeSession;
        s.OuterKp     = OuterKpBox.Value;
        s.OuterKi     = OuterKiBox.Value;
        s.OuterKd     = OuterKdBox.Value;
        s.InnerKp     = InnerKpBox.Value;
        s.InnerKi     = InnerKiBox.Value;
        s.Setpoint    = SetpointBox.Value;
        s.Disturbance = DisturbanceBox.Value;
    }

    private void PullInputs()
    {
        var s = App.CascadeSession;
        _syncing = true;
        OuterKpBox.Value     = s.OuterKp;
        OuterKiBox.Value     = s.OuterKi;
        OuterKdBox.Value     = s.OuterKd;
        InnerKpBox.Value     = s.InnerKp;
        InnerKiBox.Value     = s.InnerKi;
        SetpointBox.Value    = s.Setpoint;
        DisturbanceBox.Value = s.Disturbance;
        _syncing = false;
    }

    // ── Session events ───────────────────────────────────────────────────────

    private void SubscribeSession()
    {
        var s = App.CascadeSession;
        s.ResultChanged         -= OnResultChanged;
        s.ResultChanged         += OnResultChanged;
        s.RunningChanged        -= OnRunningChanged;
        s.RunningChanged        += OnRunningChanged;
        s.RunFailed             -= OnRunFailed;
        s.RunFailed             += OnRunFailed;
        s.RecommendationCleared -= OnRecommendationCleared;
        s.RecommendationCleared += OnRecommendationCleared;
    }

    private void UnsubscribeSession()
    {
        var s = App.CascadeSession;
        s.ResultChanged         -= OnResultChanged;
        s.RunningChanged        -= OnRunningChanged;
        s.RunFailed             -= OnRunFailed;
        s.RecommendationCleared -= OnRecommendationCleared;
    }

    private void OnResultChanged(object? sender, CascadeDesignResult result)
        => DispatcherQueue.TryEnqueue(() =>
        {
            CascadeErrorInfoBar.IsOpen = false;
            RenderResult(result);
        });

    private void OnRunningChanged(object? sender, bool running)
        => DispatcherQueue.TryEnqueue(() => RunBtn.IsEnabled = !running);

    private void OnRunFailed(object? sender, System.EventArgs e)
        => DispatcherQueue.TryEnqueue(() =>
        {
            CascadeErrorInfoBar.Message = Lang.Cas_ErrorUnavailable;
            CascadeErrorInfoBar.IsOpen  = true;
        });

    private void OnRecommendationCleared(object? sender, System.EventArgs e)
        => DispatcherQueue.TryEnqueue(() => AdvisorActions.Visibility = Visibility.Collapsed);

    // ── Run ──────────────────────────────────────────────────────────────────

    private async void RunBtn_Click(object sender, RoutedEventArgs e)
    {
        PushInputs();
        await App.CascadeSession.RunAsync();
        PullInputs();  // pick up any normalized values
    }

    private void RenderResult(CascadeDesignResult result)
    {
        var sim = result.Simulation;
        // The FOPDT run is ~12000 samples (1200 s @ dt=0.1); thin it for the chart so the
        // WebView isn't handed tens of thousands of points. Metrics use the full arrays.
        int stride = System.Math.Max(1, sim.Time.Length / 1500);
        RespChart.Update(
            Sample(sim.Time, stride), Sample(sim.Temperature, stride), Sample(sim.SingleLoopTemperature, stride),
            Sample(sim.Flow, stride), Sample(sim.FlowSetpoint, stride),
            result.Input.Setpoint, sim.DisturbanceTime);

        var m = result.Metrics;
        RiseTimeValue.Text  = m.RiseTime.ToString("0.00");
        OvershootValue.Text = m.Overshoot.ToString("0.0");
        SettlingValue.Text  = m.SettlingTime.ToString("0.0");
        SteadyErrValue.Text = m.SteadyStateError.ToString("0.000");

        CascadeDevValue.Text  = m.CascadeMaxDeviation.ToString("0.00");
        SingleDevValue.Text   = m.SingleLoopMaxDeviation.ToString("0.00");
        ImprovementValue.Text = m.DisturbanceImprovement >= 1 ? m.DisturbanceImprovement.ToString("0.0") : "--";

        DiagnosisValue.Text = string.IsNullOrEmpty(result.Diagnosis) ? "--" : result.Diagnosis;

        bool hasExplanation = !string.IsNullOrWhiteSpace(result.AdvisorExplanation);
        AdvisorExplanationHost.Child = hasExplanation
            ? MarkdownRenderer.Render(result.AdvisorExplanation, 12, ActualTheme == ElementTheme.Dark)
            : null;

        var pending = App.CascadeSession.PendingRecommendation;
        if (pending is { } rec)
        {
            AdvisorRecommendationText.Text =
                $"OUTER  Kp={rec.OuterKp:F3}  Ki={rec.OuterKi:F3}  Kd={rec.OuterKd:F3}\n" +
                $"INNER  Kp={rec.InnerKp:F3}  Ki={rec.InnerKi:F3}";
            AdvisorActions.Visibility = Visibility.Visible;
        }
        else
        {
            AdvisorActions.Visibility = Visibility.Collapsed;
        }

        AdvisorPanel.Visibility = hasExplanation || pending is not null
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Advisor accept / decline ─────────────────────────────────────────────

    private async void AdvisorAccept_Click(object sender, RoutedEventArgs e)
    {
        await App.CascadeSession.AcceptRecommendationAsync();
        PullInputs();
    }

    private void AdvisorDecline_Click(object sender, RoutedEventArgs e)
        => App.CascadeSession.ClearRecommendation();

    // Every stride-th sample, always keeping the last point so the curve reaches the end.
    private static double[] Sample(double[] a, int stride)
    {
        if (a.Length == 0 || stride <= 1) return a;
        var list = new System.Collections.Generic.List<double>(a.Length / stride + 2);
        for (int i = 0; i < a.Length; i += stride) list.Add(a[i]);
        if ((a.Length - 1) % stride != 0) list.Add(a[a.Length - 1]);
        return list.ToArray();
    }
}
