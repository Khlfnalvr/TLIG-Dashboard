using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Uno.UI.Hosting;

namespace TLIGDashboard;

/// <summary>
/// Head pratinjau (Skia) untuk PC pengembang: menjalankan UI yang sama dengan APK
/// tanpa Android SDK. Bukan pengganti TLIGDashboard.csproj — rilis Windows tetap
/// memakai build WinUI.
/// </summary>
internal class DesktopProgram
{
    [STAThread]
    public static void Main(string[] args)
    {
        var host = UnoPlatformHostBuilder.Create()
            .App(() => new App())
            .UseX11()
            .UseLinuxFrameBuffer()
            .UseMacOS()
            .UseWin32()
            .Build();

        StartPreviewAutomation();
        host.Run();
    }

    /// <summary>
    /// Alat bantu memotret halaman tanpa mengklik: set TLIG_PREVIEW_PAGE ke Tag item
    /// navigasi (Dashboard, Parameter, Cascade, LiveView, LearningAnalytic, AI) dan
    /// pratinjau akan menutup form login lalu membuka halaman itu. TLIG_PREVIEW_SIZE
    /// (mis. 1280x800) mengatur ukuran jendela agar menyerupai tablet. Tanpa variabel
    /// ini pratinjau berjalan seperti aplikasi biasa.
    /// </summary>
    private static void StartPreviewAutomation()
    {
        var page = Environment.GetEnvironmentVariable("TLIG_PREVIEW_PAGE");
        var size = Environment.GetEnvironmentVariable("TLIG_PREVIEW_SIZE");
        if (string.IsNullOrWhiteSpace(page) && string.IsNullOrWhiteSpace(size)) return;

        _ = Task.Run(async () =>
        {
            while (App.CurrentWindow is null) await Task.Delay(100);
            await Task.Delay(1500); // biarkan NavView selesai dimuat

            var window = App.CurrentWindow;
            window.DispatcherQueue.TryEnqueue(() =>
            {
                if (!string.IsNullOrWhiteSpace(size))
                {
                    var wh = size.Split('x');
                    if (wh.Length == 2 && int.TryParse(wh[0], out int w) && int.TryParse(wh[1], out int h))
                    {
                        if (window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p)
                            p.Restore();
                        window.AppWindow.Resize(new Windows.Graphics.SizeInt32 { Width = w, Height = h });
                    }
                }

                if (string.IsNullOrWhiteSpace(page) || window.Content is not FrameworkElement root) return;

                if (root.FindName("LoginOverlay") is UIElement login)
                    login.Visibility = Visibility.Collapsed;

                // "Markdown" bukan halaman: menampilkan contoh jawaban AI lewat
                // MarkdownRenderer untuk mengecek jalur teks kaya di Uno.
                if (string.Equals(page, "Markdown", StringComparison.OrdinalIgnoreCase))
                {
                    const string sample =
                        "## Tuning PID\n\nGain **Kp** menaikkan respons, *Ki* menghapus galat tunak, dan `Kd` meredam.\n\n" +
                        "- Naikkan Kp sampai osilasi\n- Turunkan 50%\n\n" +
                        "| Parameter | Nilai |\n|---|---|\n| Kp | 1.8 |\n| Ki | 0.007 |\n\n" +
                        "Fungsi alih: $G(s) = \\frac{K}{\\tau s + 1}$\n\n```\nu = Kp*e + Ki*sum(e)\n```";
                    window.GetContentFrame()!.Content = new ScrollViewer
                    {
                        Padding = new Thickness(32),
                        Content = Helpers.MarkdownRenderer.Render(
                            sample, 14, MainWindow.CurrentAppTheme == ElementTheme.Dark)
                    };
                    return;
                }

                if (root.FindName("NavView") is NavigationView nav)
                    foreach (var item in nav.MenuItems.OfType<NavigationViewItem>())
                        if (string.Equals(item.Tag as string, page, StringComparison.OrdinalIgnoreCase))
                        {
                            nav.SelectedItem = item;
                            break;
                        }
            });
        });
    }
}
