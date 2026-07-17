using System.Globalization;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace TLIGDashboard.Controls;

/// <summary>
/// Chart.js step-response plot for the Smart PID Designer. Shared by the Dashboard's
/// System Model panel and the Parameter page (its extended screen) so both draw the
/// RK4 curve from a single copy of the chart definition.
///
/// Scroll to zoom, drag to pan, double-click (or the Reset button) to fit again.
/// </summary>
public sealed partial class PidResponseChart : UserControl
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
                /* Hidden until there is a curve to reset — updateChart() reveals it. */
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
            <canvas id="pidChart"></canvas>
            <script>
                let chart;

                // The UMD build normally self-registers; register defensively in case a
                // future version stops doing so. Registering twice is harmless.
                if (window.Chart && window.ChartZoom) {
                    try { Chart.register(window.ChartZoom); } catch (e) {}
                }

                function resetZoom() { if (chart && chart.resetZoom) chart.resetZoom(); }

                function showMessage(text) {
                    var m = document.getElementById('msg');
                    m.textContent = text;
                    m.style.display = 'flex';
                    document.getElementById('reset').style.display = 'none';
                }

                function updateChart(time, amp, sp) {
                    // chart.js is loaded from a CDN — without a network this stays
                    // undefined, and a silently blank card looks like a broken sim.
                    if (!window.Chart) {
                        showMessage('Grafik tidak dapat dimuat — periksa koneksi internet (chart.js CDN).');
                        return;
                    }
                    document.getElementById('msg').style.display = 'none';
                    document.getElementById('reset').style.display = 'block';

                    const ctx = document.getElementById('pidChart').getContext('2d');
                    if (chart) chart.destroy();
                    // {x,y} points on a linear axis, not labels+values on a category
                    // axis: the zoom plugin can only narrow a numeric range, and a
                    // category axis also spaces ticks by sample index rather than by
                    // elapsed time (which is what produced x labels like 6.7, 13.4).
                    const datasets = [{
                        label: 'Step Response',
                        data: time.map((t, i) => ({ x: t, y: amp[i] })),
                        borderColor: 'rgb(75, 192, 192)',
                        tension: 0.1,
                        pointRadius: 0
                    }];
                    if (typeof sp === 'number' && isFinite(sp) && time.length) {
                        datasets.push({
                            label: 'Setpoint',
                            data: [{ x: time[0], y: sp }, { x: time[time.length - 1], y: sp }],
                            borderColor: 'rgba(255, 99, 132, 0.8)',
                            borderDash: [6, 4],
                            borderWidth: 1.5,
                            pointRadius: 0
                        });
                    }

                    const zoomCfg = window.ChartZoom ? {
                        pan:  { enabled: true, mode: 'xy' },
                        zoom: {
                            wheel: { enabled: true },
                            pinch: { enabled: true },
                            mode: 'xy'
                        }
                    } : undefined;

                    chart = new Chart(ctx, {
                        type: 'line',
                        data: { datasets: datasets },
                        options: {
                            responsive: true,
                            maintainAspectRatio: false,
                            animation: false,
                            parsing: false,
                            normalized: true,
                            plugins: {
                                legend: { display: false },
                                zoom: zoomCfg
                            },
                            scales: {
                                x: {
                                    type: 'linear',
                                    ticks: { maxTicksLimit: 8 },
                                    title: { display: true, text: 'Time (s)', font: { size: 9 } }
                                },
                                y: { title: { display: true, text: 'Amplitude', font: { size: 9 } } }
                            }
                        }
                    });
                }

                // Double-click anywhere on the plot restores the default fit.
                document.getElementById('pidChart').addEventListener('dblclick', resetZoom);
            </script>
        </body>
        </html>
        """;

    private bool _ready;
    // A run that lands before the document finishes loading would be dropped, so hold
    // the last curve and draw it once the page is up.
    private string? _pendingScript;

    public PidResponseChart()
    {
        InitializeComponent();
    }

    public async Task InitializeAsync()
    {
        await Web.EnsureCoreWebView2Async();
        Web.NavigationCompleted += OnNavigationCompleted;
        Web.NavigateToString(ChartHtml);
    }

    private void OnNavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        _ready = true;
        if (_pendingScript is { } script)
        {
            _pendingScript = null;
            _ = Web.ExecuteScriptAsync(script);
        }
    }

    /// <summary>Plots an RK4 step response with a dashed line at <paramref name="setpoint"/>.</summary>
    public void Update(IEnumerable<double> time, IEnumerable<double> amplitude, double setpoint)
    {
        string timeJson = "[" + string.Join(',', time.Select(t => t.ToString("0.###", CultureInfo.InvariantCulture))) + "]";
        string ampJson  = "[" + string.Join(',', amplitude.Select(a => a.ToString("0.####", CultureInfo.InvariantCulture))) + "]";
        string spJson   = setpoint.ToString("0.####", CultureInfo.InvariantCulture);
        string script   = $"updateChart({timeJson}, {ampJson}, {spJson})";

        if (_ready) _ = Web.ExecuteScriptAsync(script);
        else _pendingScript = script;
    }
}
