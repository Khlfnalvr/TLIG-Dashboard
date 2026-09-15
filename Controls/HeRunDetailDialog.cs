// Controls/HeRunDetailDialog.cs
// Dialog detail satu percobaan HE: kurva responsnya, parameter yang dipakai,
// metrik hasilnya, dan tombol ekspor kurva ke CSV.
// Dipanggil dari HeQueueHistoryPage saat sebuah baris riwayat diklik.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TLIGDashboard.Helpers;
using TLIGDashboard.Models;
using TLIGDashboard.Services;

namespace TLIGDashboard.Controls;

internal static class HeRunDetailDialog
{
    private static LocalizationManager Lang => App.Lang;

    /// <summary>
    /// Membuka detail satu percobaan. Run-nya diambil ulang lengkap dengan kurva
    /// (daftar riwayat sengaja tidak membawanya), jadi dialog ini yang menanggung
    /// satu-satunya pengambilan data berat di halaman itu.
    /// </summary>
    public static async Task ShowAsync(XamlRoot root, long runId)
    {
        var run = await HeControlService.GetRunAsync(runId);
        if (run is null)
        {
            await ShowMessageAsync(root, Lang.HeH_DetailTitle, Lang.HeH_DetailNotFound);
            return;
        }

        var content = new StackPanel { Width = 660, Spacing = 12 };

        // ── Keterangan run ───────────────────────────────────────────────────
        string when = DateTime.SpecifyKind(run.StartedAtUtc, DateTimeKind.Utc)
            .ToLocalTime().ToString("dd MMM yyyy HH:mm:ss");
        string who = string.IsNullOrWhiteSpace(run.RequestedByName)
            ? (run.RequestedByUserId ?? "—") : run.RequestedByName!;

        content.Children.Add(new TextBlock
        {
            Text = $"{when} · {who} · {run.Status}",
            Opacity = 0.7,
            FontSize = 12,
        });

        content.Children.Add(new TextBlock
        {
            Text = run.Input.ToString(),   // "SP=60 Kc=2,5 Ti=10 Td=0,5 Pump=75%"
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });

        // ── Kurva ────────────────────────────────────────────────────────────
        if (run.Samples.Count > 0)
        {
            var chart = new CascadeResponseChart { Height = 300 };

            // WebView2 baru bisa disiapkan setelah kontrolnya masuk visual tree,
            // jadi pemasangannya menunggu Loaded. Update() sendiri aman dipanggil
            // lebih dulu — skripnya diantre sampai halaman chart selesai dimuat.
            chart.Loaded += async (_, _) =>
            {
                try
                {
                    await chart.InitializeAsync();
                    DrawCurve(chart, run);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"HE run chart failed: {ex}");
                }
            };

            content.Children.Add(chart);
        }
        else
        {
            content.Children.Add(new TextBlock
            {
                Text = Lang.HeH_DetailNoCurve,
                Opacity = 0.7,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        // ── Metrik ───────────────────────────────────────────────────────────
        if (run.Metrics is { } m)
        {
            var metrics = new StackPanel { Spacing = 2 };
            metrics.Children.Add(Line($"{Lang.HeH_ColMetrics}: " + string.Join(" · ", new[]
            {
                m.RiseTimeSeconds     is { } rise   ? $"tr {rise:0.##} s"        : null,
                m.SettlingTimeSeconds is { } settle ? $"ts {settle:0.##} s"      : null,
                m.OvershootPercent    is { } over   ? $"OS {over:0.##} %"        : null,
                m.PeakValue           is { } peak   ? $"peak {peak:0.##}"        : null,
                m.SteadyStateError    is { } err    ? $"e {err:0.###} °C"   : null,
            }.Where(part => part is not null))));

            metrics.Children.Add(Line(string.Join(" · ", new[]
            {
                m.Ise  is { } ise  ? $"ISE {ise:0.##}"   : null,
                m.Iae  is { } iae  ? $"IAE {iae:0.##}"   : null,
                m.Itae is { } itae ? $"ITAE {itae:0.##}" : null,
            }.Where(part => part is not null))));

            content.Children.Add(metrics);
        }

        // ── Ekspor kurva ─────────────────────────────────────────────────────
        var exportStatus = new TextBlock
        {
            FontSize = 11,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var exportButton = new Button { Content = Lang.HeH_ExportCurve, IsEnabled = run.Samples.Count > 0 };
        exportButton.Click += async (_, _) =>
        {
            exportButton.IsEnabled = false;
            try
            {
                var path = await CsvFileSaver.SaveAsync(
                    HeCsvExport.RunSamples(run), $"kurva-he-run{run.RunId}");
                if (path is not null)
                {
                    exportStatus.Text = $"{Lang.HeH_ExportSaved}: {path}";
                    exportStatus.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                exportStatus.Text = $"{Lang.HeH_ExportFailed}: {ex.Message}";
                exportStatus.Visibility = Visibility.Visible;
            }
            finally
            {
                exportButton.IsEnabled = run.Samples.Count > 0;
            }
        };

        content.Children.Add(exportButton);
        content.Children.Add(exportStatus);

        var dialog = new ContentDialog
        {
            Title           = Lang.HeH_DetailTitle,
            Content         = new ScrollViewer { Content = content, MaxHeight = 620 },
            CloseButtonText = Lang.Get("Ui_Close"),
            DefaultButton   = ContentDialogButton.Close,
            XamlRoot        = root,
        };

        await dialog.ShowAsync();
    }

    /// <summary>
    /// Menggambar kurva terukur: suhu keluaran shell sebagai process variable dan
    /// flow tube sebagai loop dalam — pasangan yang sama dengan yang dimodelkan
    /// Gp1/Gp2. Baseline single-loop dan penanda gangguan tidak ada di percobaan
    /// sungguhan, jadi keduanya dikirim kosong.
    /// </summary>
    private static void DrawCurve(CascadeResponseChart chart, HeParameterRun run)
    {
        int n = run.Samples.Count;
        var time = new double[n];
        var temperature = new double[n];
        var flow = new double[n];

        for (int i = 0; i < n; i++)
        {
            var s = run.Samples[i];
            time[i] = s.TSeconds;
            // Kanal yang tidak terukur jadi NaN — digambar putus, bukan ditarik ke nol.
            temperature[i] = s.PvShellOut ?? double.NaN;
            flow[i]        = s.FlowTube ?? s.FlowShell ?? double.NaN;
        }

        chart.Update(time, temperature, [], flow, [], run.Input.Sp, -1);
    }

    private static TextBlock Line(string text) => new()
    {
        Text = text,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
    };

    private static async Task ShowMessageAsync(XamlRoot root, string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title           = title,
            Content         = message,
            CloseButtonText = Lang.Get("Ui_Close"),
            XamlRoot        = root,
        };
        try { await dialog.ShowAsync(); } catch { }
    }
}
