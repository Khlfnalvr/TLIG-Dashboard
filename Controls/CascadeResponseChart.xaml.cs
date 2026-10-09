using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TLIGDashboard.Services;
using Windows.UI;

namespace TLIGDashboard.Controls;

/// <summary>Which loop of the cascade a <see cref="CascadeResponseChart"/> plots.</summary>
public enum CascadeChartLoop
{
    /// <summary>Outer (primary) loop: temperature, shell outlet — Gp1.</summary>
    Outer,
    /// <summary>Inner (secondary) loop: flow, tube side — Gp2.</summary>
    Inner,
}

/// <summary>
/// One loop of the Cascade Control designer, comparing the RK4 <b>simulation</b> with the
/// <b>actual plant</b> readings from the PLC. Pages place two of them, one per <see cref="Loop"/>:
///
/// <list type="bullet">
/// <item><b>Outer</b> — simulated temperature, the single-loop baseline, the temperature
///   setpoint, and the measured shell-outlet temperature ("Temp. Shell out").</item>
/// <item><b>Inner</b> — simulated flow, the flow setpoint the outer PID hands the inner PI,
///   and the measured tube flow ("Flow Tube"). Tube, not shell: CascadeSimulator identifies
///   Gp2 as "secondary: flow (tube)", the same pairing ProcessErrorService uses.</item>
/// </list>
///
/// Both show the disturbance marker. Drawn natively by <see cref="LineChartView"/> (no
/// WebView2 / Chart.js).
///
/// <para>Where the data comes from is unchanged: the simulation is pushed by the page through
/// <see cref="Update"/>, and the PLC layer is pulled by the control itself from
/// <see cref="PlcTraceFeed"/> while it is on screen (Server: its own LabVIEW link; Client:
/// <c>GET /hmi/trace</c>). Either layer can be drawn without the other, and redrawing one
/// never discards the other.</para>
/// </summary>
public sealed partial class CascadeResponseChart : UserControl
{
    // Same palette as the former Chart.js version, so the curves keep their meaning.
    private static readonly Color SimTemp    = Color.FromArgb(255, 75, 192, 192);
    private static readonly Color SingleLoop = Color.FromArgb(230, 150, 150, 150);
    private static readonly Color TempSp     = Color.FromArgb(217, 255, 99, 132);
    private static readonly Color PlcTemp    = Color.FromArgb(255, 220, 53, 69);
    private static readonly Color SimFlow    = Color.FromArgb(255, 255, 159, 64);
    private static readonly Color FlowSp     = Color.FromArgb(128, 255, 159, 64);
    private static readonly Color PlcFlow    = Color.FromArgb(255, 54, 120, 235);
    private static readonly Color Disturb    = Color.FromArgb(140, 120, 120, 120);

    // Thinning ceiling for the PLC layer, same idea as the simulation's stride.
    private const int MaxPlcPoints = 1500;

    /// <summary>The loop this chart plots. Set it in XAML (<c>Loop="Inner"</c>).</summary>
    public CascadeChartLoop Loop
    {
        get => _loop;
        set { _loop = value; Render(); }
    }
    private CascadeChartLoop _loop = CascadeChartLoop.Outer;

    // Simulation layer (from Update) and PLC layer (from the feed).
    private double[]? _time, _temp, _single, _flow, _flowSp;
    private double _sp = double.NaN, _distTime = double.NaN;
    private string _tempUnit = "°C", _flowUnit = "L/min";
    private PlcTrace? _plc;

    private bool _feedAttached;
    private bool _feedPushPending;

    public CascadeResponseChart()
    {
        InitializeComponent();
        Loaded   += OnControlLoaded;
        Unloaded += OnControlUnloaded;
    }

    // ── Simulation layer ────────────────────────────────────────────────────────

    /// <summary>
    /// Draws a completed cascade run. Both charts take the same arguments and each plots its
    /// own loop's part, so pages can hand the same run to both.
    /// </summary>
    public void Update(
        IEnumerable<double> time, IEnumerable<double> temperature, IEnumerable<double> singleLoop,
        IEnumerable<double> flow, IEnumerable<double> flowSetpoint,
        double setpoint, double disturbanceTime,
        string tempUnit = "°C", string flowUnit = "L/min")
    {
        _time     = time.ToArray();
        _temp     = temperature.ToArray();
        _single   = singleLoop.ToArray();
        _flow     = flow.ToArray();
        _flowSp   = flowSetpoint.ToArray();
        _sp       = setpoint;
        _distTime = disturbanceTime;
        _tempUnit = tempUnit;
        _flowUnit = flowUnit;
        Render();
    }

    // ── PLC layer: pulled from the feed while the chart is on screen ────────────

    private void OnControlLoaded(object sender, RoutedEventArgs e)
    {
        if (_feedAttached) return;
        _feedAttached = true;

        var feed = PlcTraceFeed.Instance;
        feed.Changed -= OnFeedChanged;
        feed.Changed += OnFeedChanged;
        feed.Attach();
        _plc = feed.Current;   // catch up on anything that happened while off screen
        Render();
    }

    private void OnControlUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_feedAttached) return;
        _feedAttached = false;

        var feed = PlcTraceFeed.Instance;
        feed.Changed -= OnFeedChanged;
        feed.Detach();
    }

    // May arrive on a background thread (Server). Coalesced: several points landing before the
    // UI thread gets to run cost one redraw, not one each.
    private void OnFeedChanged(PlcTrace? _)
    {
        if (_feedPushPending) return;
        _feedPushPending = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _feedPushPending = false;
            _plc = PlcTraceFeed.Instance.Current;
            Render();
        });
    }

    // ── Series per loop ─────────────────────────────────────────────────────────

    private void Render()
    {
        if (Chart is null) return;   // Loop set from XAML before InitializeComponent finished

        bool inner = _loop == CascadeChartLoop.Inner;
        Chart.YTitle = inner ? $"Flow ({_flowUnit})" : $"Temperatur ({_tempUnit})";

        var list = new List<ChartSeries>();
        bool hasSim = _time is { Length: > 0 };
        var plc = _plc is { Count: > 0 } p ? p : null;
        double[]? plcTime = plc is null ? null : Thin(plc.Time, plc.Count);

        if (inner)
        {
            if (hasSim)
            {
                list.Add(new ChartSeries { Label = $"Simulasi: Flow ({_flowUnit})", Color = SimFlow, X = _time!, Y = _flow! });
                if (_flowSp is { } fsp && fsp.Length == _time!.Length)
                    list.Add(new ChartSeries { Label = "Setpoint flow (output PID luar)", Color = FlowSp, X = _time, Y = fsp, Thickness = 1.5, Dash = [4, 4] });
            }
            if (plc is not null)
                list.Add(new ChartSeries { Label = $"Plant (PLC): Flow tube ({_flowUnit})", Color = PlcFlow, X = plcTime!, Y = Thin(plc.FlowTube, plc.Count), PointRadius = 2 });
        }
        else
        {
            if (hasSim)
            {
                list.Add(new ChartSeries { Label = $"Simulasi: Temperatur ({_tempUnit})", Color = SimTemp, X = _time!, Y = _temp! });
                if (_single is { } sl && sl.Length == _time!.Length)
                    list.Add(new ChartSeries { Label = "Simulasi: single-loop", Color = SingleLoop, X = _time, Y = sl, Thickness = 1.5, Dash = [5, 4] });
            }

            // One setpoint line spanning whichever layers are drawn.
            double sp = hasSim ? _sp : plc?.Setpoint ?? double.NaN;
            double tMax = Math.Max(hasSim ? _time![^1] : 0, plcTime is { Length: > 0 } pt ? pt[^1] : 0);
            if (double.IsFinite(sp) && tMax > 0)
                list.Add(new ChartSeries { Label = "Setpoint temperatur", Color = TempSp, X = [0, tMax], Y = [sp, sp], Thickness = 1.5, Dash = [6, 4] });

            if (plc is not null)
                list.Add(new ChartSeries { Label = $"Plant (PLC): Temp. shell out ({_tempUnit})", Color = PlcTemp, X = plcTime!, Y = Thin(plc.PvShellOut, plc.Count), PointRadius = 2 });
        }

        if (hasSim && double.IsFinite(_distTime) && _distTime >= 0)
            list.Add(new ChartSeries { Label = "Gangguan flow", Color = Disturb, Kind = ChartSeriesKind.VerticalMarker, X = [_distTime], Thickness = 1, Dash = [2, 3] });

        Chart.SetSeries(list);
    }

    // Every stride-th point, always keeping the last so the curve reaches the newest reading.
    private static double[] Thin(double[] a, int count)
    {
        int stride = Math.Max(1, count / MaxPlcPoints);
        if (a.Length == 0 || stride <= 1) return a;
        var list = new List<double>(a.Length / stride + 2);
        for (int i = 0; i < a.Length; i += stride) list.Add(a[i]);
        if ((a.Length - 1) % stride != 0) list.Add(a[^1]);
        return list.ToArray();
    }
}
