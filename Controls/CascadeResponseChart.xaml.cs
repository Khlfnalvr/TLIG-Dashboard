using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using TLIGDashboard.Services;

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
/// Chart.js plot for one loop of the Cascade Control designer, comparing the RK4
/// <b>simulation</b> with the <b>actual plant</b> readings from the PLC. Pages place two of
/// them, one per <see cref="Loop"/>:
///
/// <list type="bullet">
/// <item><b>Outer</b> — simulated temperature, the single-loop baseline, the temperature
///   setpoint, and the measured shell-outlet temperature ("Temp. Shell out").</item>
/// <item><b>Inner</b> — simulated flow, the flow setpoint the outer PID hands the inner PI,
///   and the measured tube flow ("Flow Tube"). Tube, not shell: CascadeSimulator identifies
///   Gp2 as "secondary: flow (tube)", the same pairing ProcessErrorService uses.</item>
/// </list>
///
/// Both show the disturbance marker. Built on the same WebView2 + CDN pattern as
/// <see cref="PidResponseChart"/> (scroll to zoom, drag to pan, double-click / Reset to fit).
///
/// <para>Two independent layers share the axes: the simulation (pushed by <see cref="Update"/>)
/// and the PLC result (pulled by the control itself from <see cref="PlcTraceFeed"/> while it is
/// on screen). Either layer can be drawn without the other, and redrawing one never discards
/// the other.</para>
/// </summary>
public sealed partial class CascadeResponseChart : UserControl
{
    private const string ChartHtml = """
        <!DOCTYPE html>
        <html>
        <head>
            <script src="https://cdn.jsdelivr.net/npm/chart.js"></script>
            <script src="https://cdn.jsdelivr.net/npm/hammerjs@2.0.8"></script>
            <script src="https://cdn.jsdelivr.net/npm/chartjs-plugin-zoom"></script>
            <style>
                html, body { margin: 0; height: 100%; }
                body {
                    padding: 4px; box-sizing: border-box; position: relative;
                    background: transparent; overflow: hidden; font-family: sans-serif;
                }
                canvas { width: 100% !important; height: 100% !important; }
                #reset {
                    position: absolute; top: 6px; right: 8px; z-index: 10; display: none;
                    font: 11px sans-serif; padding: 3px 9px; border-radius: 4px;
                    border: 1px solid rgba(128,128,128,0.45);
                    background: rgba(150,150,150,0.16); color: #888; cursor: pointer;
                }
                #reset:hover { background: rgba(150,150,150,0.34); color: #555; }
                #msg {
                    position: absolute; inset: 0; display: none;
                    align-items: center; justify-content: center;
                    font: 11px sans-serif; color: #888; text-align: center; padding: 0 12px;
                }
            </style>
        </head>
        <body>
            <button id="reset" onclick="resetZoom()" title="Reset zoom">&#8635; Reset</button>
            <div id="msg"></div>
            <canvas id="cascadeChart"></canvas>
            <script>
                const LOOP = '__LOOP__';   // 'outer' (temperature) or 'inner' (flow)
                let chart = null;
                let chartSig = '';
                let sim = null;   // simulation layer: {time,temp,single,flow,flowSp,sp,distTime,tempUnit,flowUnit}
                let plc = null;   // PLC layer: {time,pv,fs,ft,sp}

                if (window.Chart && window.ChartZoom) {
                    try { Chart.register(window.ChartZoom); } catch (e) {}
                }
                function resetZoom() { if (chart && chart.resetZoom) chart.resetZoom(); }
                function showMessage(text) {
                    var m = document.getElementById('msg');
                    m.textContent = text; m.style.display = 'flex';
                    document.getElementById('reset').style.display = 'none';
                }

                // Simulation layer. time, temp, single, flow, flowSp are number arrays of equal
                // length; sp is the temperature setpoint, distTime is where the disturbance hits.
                function updateChart(time, temp, single, flow, flowSp, sp, distTime, tempUnit, flowUnit) {
                    sim = { time: time, temp: temp, single: single, flow: flow, flowSp: flowSp,
                            sp: sp, distTime: distTime, tempUnit: tempUnit, flowUnit: flowUnit };
                    render();
                }

                // PLC layer. Arrays are equal length; null = channel not measured at that point.
                function updatePlc(time, pv, fs, ft, sp) {
                    plc = { time: time, pv: pv, fs: fs, ft: ft, sp: sp };
                    render();
                }
                function clearPlc() { plc = null; render(); }

                // Points with a missing value are left out rather than drawn as zero.
                function xy(time, arr) {
                    const out = [];
                    for (let i = 0; i < time.length; i++) {
                        const v = arr ? arr[i] : null;
                        if (v !== null && v !== undefined && isFinite(v)) out.push({ x: time[i], y: v });
                    }
                    return out;
                }

                function lastTime() {
                    let tMax = 0;
                    if (sim && sim.time.length) tMax = Math.max(tMax, sim.time[sim.time.length - 1]);
                    if (plc && plc.time.length) tMax = Math.max(tMax, plc.time[plc.time.length - 1]);
                    return tMax;
                }

                function maxOf(arrays, start) {
                    let m = start;
                    for (const a of arrays)
                        for (const v of a || []) if (v !== null && isFinite(v) && v > m) m = v;
                    return m;
                }

                function distMarker(yMax) {
                    return {
                        label: 'Gangguan flow',
                        data: [{ x: sim.distTime, y: 0 }, { x: sim.distTime, y: yMax * 1.05 }],
                        borderColor: 'rgba(120,120,120,0.55)',
                        borderDash: [2, 3], borderWidth: 1, pointRadius: 0
                    };
                }

                function hasDist() { return sim && typeof sim.distTime === 'number' && sim.distTime >= 0; }

                // ── Outer loop: temperature ───────────────────────────────────
                function outerDatasets(unit) {
                    const sets = [];
                    if (sim) {
                        sets.push({
                            label: 'Simulasi: Temperatur (' + unit + ')',
                            data: xy(sim.time, sim.temp), borderColor: 'rgb(75, 192, 192)',
                            borderWidth: 2, tension: 0.1, pointRadius: 0
                        });
                        if (sim.single && sim.single.length === sim.time.length) {
                            sets.push({
                                label: 'Simulasi: single-loop',
                                data: xy(sim.time, sim.single), borderColor: 'rgba(150,150,150,0.9)',
                                borderDash: [5, 4], borderWidth: 1.5, tension: 0.1, pointRadius: 0
                            });
                        }
                    }
                    const sp = sim ? sim.sp : (plc ? plc.sp : NaN);
                    const tMax = lastTime();
                    if (typeof sp === 'number' && isFinite(sp) && tMax > 0) {
                        sets.push({
                            label: 'Setpoint temperatur',
                            data: [{ x: 0, y: sp }, { x: tMax, y: sp }],
                            borderColor: 'rgba(255, 99, 132, 0.85)',
                            borderDash: [6, 4], borderWidth: 1.5, pointRadius: 0
                        });
                    }
                    if (plc) {
                        sets.push({
                            label: 'Plant (PLC): Temp. shell out (' + unit + ')',
                            data: xy(plc.time, plc.pv), borderColor: 'rgb(220, 53, 69)',
                            borderWidth: 2, tension: 0.1, pointRadius: 2, pointHoverRadius: 4
                        });
                    }
                    if (hasDist()) {
                        let yMax = maxOf([sim.temp, sim.single, plc ? plc.pv : null], sim.sp);
                        sets.push(distMarker(yMax));
                    }
                    return sets;
                }

                // ── Inner loop: flow ──────────────────────────────────────────
                function innerDatasets(unit) {
                    const sets = [];
                    if (sim) {
                        sets.push({
                            label: 'Simulasi: Flow (' + unit + ')',
                            data: xy(sim.time, sim.flow), borderColor: 'rgb(255, 159, 64)',
                            borderWidth: 2, tension: 0.1, pointRadius: 0
                        });
                        if (sim.flowSp && sim.flowSp.length === sim.time.length) {
                            sets.push({
                                label: 'Setpoint flow (output PID luar)',
                                data: xy(sim.time, sim.flowSp), borderColor: 'rgba(255, 159, 64, 0.5)',
                                borderDash: [4, 4], borderWidth: 1.5, pointRadius: 0
                            });
                        }
                    }
                    if (plc) {
                        sets.push({
                            label: 'Plant (PLC): Flow tube (' + unit + ')',
                            data: xy(plc.time, plc.ft), borderColor: 'rgb(54, 120, 235)',
                            borderWidth: 2, tension: 0.1, pointRadius: 2, pointHoverRadius: 4
                        });
                    }
                    if (hasDist()) {
                        let yMax = maxOf([sim.flow, sim.flowSp, plc ? plc.ft : null], 0);
                        sets.push(distMarker(yMax));
                    }
                    return sets;
                }

                function render() {
                    if (!window.Chart) {
                        showMessage('Grafik tidak dapat dimuat — periksa koneksi internet (chart.js CDN).');
                        return;
                    }

                    const inner = LOOP === 'inner';
                    const unit  = inner ? (sim ? sim.flowUnit : 'L/min') : (sim ? sim.tempUnit : '°C');
                    const sets  = inner ? innerDatasets(unit) : outerDatasets(unit);

                    if (sets.length === 0) {
                        if (chart) { chart.destroy(); chart = null; chartSig = ''; }
                        document.getElementById('reset').style.display = 'none';
                        return;
                    }
                    document.getElementById('msg').style.display = 'none';
                    document.getElementById('reset').style.display = 'block';

                    // Same layers and labels as the chart on screen: swap the data in place so a live
                    // PLC update (about once a second) does not throw away the user's zoom and pan.
                    const sig = sets.map(s => s.label).join(';');
                    if (chart && sig === chartSig) {
                        chart.data.datasets.forEach((d, i) => { d.data = sets[i].data; });
                        chart.update('none');
                        return;
                    }

                    if (chart) chart.destroy();
                    chartSig = sig;
                    const ctx = document.getElementById('cascadeChart').getContext('2d');

                    const zoomCfg = window.ChartZoom ? {
                        pan:  { enabled: true, mode: 'xy' },
                        zoom: { wheel: { enabled: true }, pinch: { enabled: true }, mode: 'xy' }
                    } : undefined;

                    chart = new Chart(ctx, {
                        type: 'line',
                        data: { datasets: sets },
                        options: {
                            responsive: true, maintainAspectRatio: false,
                            animation: false, parsing: false, normalized: true,
                            interaction: { mode: 'nearest', axis: 'x', intersect: false },
                            plugins: {
                                legend: { display: true, position: 'bottom', labels: { boxWidth: 18, font: { size: 10 } } },
                                zoom: zoomCfg
                            },
                            scales: {
                                x: {
                                    type: 'linear', ticks: { maxTicksLimit: 10 },
                                    title: { display: true, text: 'Waktu (s)', font: { size: 9 } }
                                },
                                y: {
                                    type: 'linear',
                                    title: {
                                        display: true, font: { size: 9 },
                                        text: (inner ? 'Flow (' : 'Temperatur (') + unit + ')'
                                    }
                                }
                            }
                        }
                    });
                }
                document.getElementById('cascadeChart').addEventListener('dblclick', resetZoom);
            </script>
        </body>
        </html>
        """;

    // Thinning ceiling for the PLC layer, same idea as the simulation's stride.
    private const int MaxPlcPoints = 1500;

    /// <summary>
    /// The loop this chart plots. Set it in XAML (<c>Loop="Inner"</c>); it is read once, when
    /// <see cref="InitializeAsync"/> loads the page, so it must not change afterwards.
    /// </summary>
    public CascadeChartLoop Loop { get; set; } = CascadeChartLoop.Outer;

    private bool _ready;

    // Latest script per layer, held until the page has loaded. Only the newest of each kind
    // matters, so a burst of updates before the WebView is ready does not pile up.
    private string? _pendingSim;
    private string? _pendingPlc;

    private bool _feedAttached;
    private bool _feedPushPending;

    public CascadeResponseChart()
    {
        InitializeComponent();
        Loaded   += OnControlLoaded;
        Unloaded += OnControlUnloaded;
    }

    public async Task InitializeAsync()
    {
        await Web.EnsureCoreWebView2Async();
        Web.NavigationCompleted += OnNavigationCompleted;
        Web.NavigateToString(ChartHtml.Replace("__LOOP__", Loop == CascadeChartLoop.Inner ? "inner" : "outer"));
    }

    private void OnNavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        _ready = true;
        if (_pendingSim is { } sim) { _pendingSim = null; _ = Web.ExecuteScriptAsync(sim); }
        if (_pendingPlc is { } plc) { _pendingPlc = null; _ = Web.ExecuteScriptAsync(plc); }
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
        PushPlc(feed.Current);   // catch up on anything that happened while off screen
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
            PushPlc(PlcTraceFeed.Instance.Current);
        });
    }

    private void PushPlc(PlcTrace? trace)
    {
        string script;
        if (trace is null || trace.Count == 0)
        {
            script = "clearPlc()";
        }
        else
        {
            int stride = System.Math.Max(1, trace.Count / MaxPlcPoints);
            script =
                $"updatePlc({ArrN(Thin(trace.Time, stride))}, {ArrN(Thin(trace.PvShellOut, stride))}, " +
                $"{ArrN(Thin(trace.FlowShell, stride))}, {ArrN(Thin(trace.FlowTube, stride))}, " +
                $"{Num(trace.Setpoint)})";
        }

        if (_ready) _ = Web.ExecuteScriptAsync(script);
        else _pendingPlc = script;
    }

    // Every stride-th point, always keeping the last so the curve reaches the newest reading.
    private static double[] Thin(double[] a, int stride)
    {
        if (a.Length == 0 || stride <= 1) return a;
        var list = new List<double>(a.Length / stride + 2);
        for (int i = 0; i < a.Length; i += stride) list.Add(a[i]);
        if ((a.Length - 1) % stride != 0) list.Add(a[^1]);
        return list.ToArray();
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
        string script =
            $"updateChart({Arr(time)}, {Arr(temperature)}, {Arr(singleLoop)}, {Arr(flow)}, " +
            $"{Arr(flowSetpoint)}, {Num(setpoint)}, {Num(disturbanceTime)}, " +
            $"'{tempUnit}', '{flowUnit}')";

        if (_ready) _ = Web.ExecuteScriptAsync(script);
        else _pendingSim = script;
    }

    private static string Arr(IEnumerable<double> xs) =>
        "[" + string.Join(',', xs.Select(x => x.ToString("0.####", CultureInfo.InvariantCulture))) + "]";

    // NaN has no JSON/JS literal worth drawing: send null and let the chart skip the point.
    private static string ArrN(IEnumerable<double> xs) =>
        "[" + string.Join(',', xs.Select(x => double.IsFinite(x)
            ? x.ToString("0.####", CultureInfo.InvariantCulture) : "null")) + "]";

    private static string Num(double x) => double.IsFinite(x)
        ? x.ToString("0.####", CultureInfo.InvariantCulture) : "null";
}
