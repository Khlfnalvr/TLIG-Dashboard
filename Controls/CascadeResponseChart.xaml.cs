using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace TLIGDashboard.Controls;

/// <summary>
/// Chart.js plot for the Cascade Control designer. Shows the primary (temperature) and
/// secondary (flow) process variables on two y-axes, plus the single-loop temperature
/// baseline and a marker where the flow disturbance is injected — so the cascade's
/// disturbance-rejection advantage is visible at a glance. Built on the same WebView2 +
/// CDN pattern as <see cref="PidResponseChart"/> (scroll to zoom, drag to pan,
/// double-click / Reset to fit).
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
                let chart;
                if (window.Chart && window.ChartZoom) {
                    try { Chart.register(window.ChartZoom); } catch (e) {}
                }
                function resetZoom() { if (chart && chart.resetZoom) chart.resetZoom(); }
                function showMessage(text) {
                    var m = document.getElementById('msg');
                    m.textContent = text; m.style.display = 'flex';
                    document.getElementById('reset').style.display = 'none';
                }

                // time, temp, single, flow, flowSp are number arrays of equal length;
                // sp is the temperature setpoint, distTime is where the disturbance hits.
                function updateChart(time, temp, single, flow, flowSp, sp, distTime, tempUnit, flowUnit) {
                    if (!window.Chart) {
                        showMessage('Grafik tidak dapat dimuat — periksa koneksi internet (chart.js CDN).');
                        return;
                    }
                    document.getElementById('msg').style.display = 'none';
                    document.getElementById('reset').style.display = 'block';

                    const ctx = document.getElementById('cascadeChart').getContext('2d');
                    if (chart) chart.destroy();

                    const xy = (arr) => time.map((t, i) => ({ x: t, y: arr[i] }));
                    const datasets = [];

                    datasets.push({
                        label: 'Temperatur (' + tempUnit + ')', yAxisID: 'yTemp',
                        data: xy(temp), borderColor: 'rgb(75, 192, 192)',
                        borderWidth: 2, tension: 0.1, pointRadius: 0
                    });
                    if (single && single.length === time.length) {
                        datasets.push({
                            label: 'Temperatur single-loop', yAxisID: 'yTemp',
                            data: xy(single), borderColor: 'rgba(150,150,150,0.9)',
                            borderDash: [5, 4], borderWidth: 1.5, tension: 0.1, pointRadius: 0
                        });
                    }
                    if (typeof sp === 'number' && isFinite(sp) && time.length) {
                        datasets.push({
                            label: 'Setpoint temperatur', yAxisID: 'yTemp',
                            data: [{ x: time[0], y: sp }, { x: time[time.length - 1], y: sp }],
                            borderColor: 'rgba(255, 99, 132, 0.85)',
                            borderDash: [6, 4], borderWidth: 1.5, pointRadius: 0
                        });
                    }
                    datasets.push({
                        label: 'Flow (' + flowUnit + ')', yAxisID: 'yFlow',
                        data: xy(flow), borderColor: 'rgb(255, 159, 64)',
                        borderWidth: 2, tension: 0.1, pointRadius: 0
                    });
                    if (flowSp && flowSp.length === time.length) {
                        datasets.push({
                            label: 'Setpoint flow (dari PID luar)', yAxisID: 'yFlow',
                            data: xy(flowSp), borderColor: 'rgba(255, 159, 64, 0.45)',
                            borderDash: [4, 4], borderWidth: 1.5, pointRadius: 0
                        });
                    }
                    if (typeof distTime === 'number' && distTime >= 0) {
                        let yMax = sp;
                        for (const v of temp) if (isFinite(v) && v > yMax) yMax = v;
                        for (const v of single || []) if (isFinite(v) && v > yMax) yMax = v;
                        datasets.push({
                            label: 'Gangguan flow', yAxisID: 'yTemp',
                            data: [{ x: distTime, y: 0 }, { x: distTime, y: yMax * 1.05 }],
                            borderColor: 'rgba(120,120,120,0.55)',
                            borderDash: [2, 3], borderWidth: 1, pointRadius: 0
                        });
                    }

                    const zoomCfg = window.ChartZoom ? {
                        pan:  { enabled: true, mode: 'xy' },
                        zoom: { wheel: { enabled: true }, pinch: { enabled: true }, mode: 'xy' }
                    } : undefined;

                    chart = new Chart(ctx, {
                        type: 'line',
                        data: { datasets: datasets },
                        options: {
                            responsive: true, maintainAspectRatio: false,
                            animation: false, parsing: false, normalized: true,
                            interaction: { mode: 'nearest', axis: 'x', intersect: false },
                            plugins: {
                                legend: { display: true, labels: { boxWidth: 18, font: { size: 10 } } },
                                zoom: zoomCfg
                            },
                            scales: {
                                x: {
                                    type: 'linear', ticks: { maxTicksLimit: 10 },
                                    title: { display: true, text: 'Waktu (s)', font: { size: 9 } }
                                },
                                yTemp: {
                                    type: 'linear', position: 'left',
                                    title: { display: true, text: 'Temperatur (' + tempUnit + ')', font: { size: 9 } }
                                },
                                yFlow: {
                                    type: 'linear', position: 'right',
                                    grid: { drawOnChartArea: false },
                                    title: { display: true, text: 'Flow (' + flowUnit + ')', font: { size: 9 } }
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

    private bool _ready;
    private string? _pendingScript;

    public CascadeResponseChart()
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

    /// <summary>Draws a completed cascade run.</summary>
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
        else _pendingScript = script;
    }

    private static string Arr(IEnumerable<double> xs) =>
        "[" + string.Join(',', xs.Select(x => x.ToString("0.####", CultureInfo.InvariantCulture))) + "]";

    private static string Num(double x) => x.ToString("0.####", CultureInfo.InvariantCulture);
}
