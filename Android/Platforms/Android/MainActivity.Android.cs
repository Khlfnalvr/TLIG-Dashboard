using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using AndroidX.Core.View;

namespace TLIGDashboard.Droid;

// Dashboard dirancang untuk layar lebar (tiga kolom, halaman Parameter satu layar),
// jadi di tablet aplikasi dikunci mendatar; sensor tetap boleh membaliknya 180°.
[Activity(
    MainLauncher = true,
    ScreenOrientation = ScreenOrientation.SensorLandscape,
    ConfigurationChanges = global::Uno.UI.ActivityHelper.AllConfigChanges,
    WindowSoftInputMode = SoftInput.AdjustNothing | SoftInput.StateHidden,
    Exported = true
)]
public class MainActivity : Microsoft.UI.Xaml.ApplicationActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        global::AndroidX.Core.SplashScreen.SplashScreen.InstallSplashScreen(this);

        base.OnCreate(savedInstanceState);

        KeepContentClearOfSystemBars();
    }

    /// <summary>
    /// Android menggambar aplikasi dari tepi ke tepi (wajib sejak Android 15), sehingga
    /// tanpa ini baris navigasi atas tertimpa status bar — tidak bisa diketuk — dan
    /// bagian bawah tertimpa tombol navigasi. Isi aplikasi diberi jarak sebesar area
    /// sistem itu (termasuk lekukan kamera); jalurnya sendiri dibiarkan hitam dengan
    /// ikon terang, cocok untuk tema gelap maupun terang.
    /// </summary>
    private void KeepContentClearOfSystemBars()
    {
        if (Window is null) return;

        Window.DecorView.SetBackgroundColor(Android.Graphics.Color.Black);

        var controller = WindowCompat.GetInsetsController(Window, Window.DecorView);
        if (controller is not null)
        {
            controller.AppearanceLightStatusBars = false;
            controller.AppearanceLightNavigationBars = false;
        }

        var content = FindViewById(Android.Resource.Id.Content);
        if (content is null) return;

        ViewCompat.SetOnApplyWindowInsetsListener(content, new SystemBarInsetsListener());
        ViewCompat.RequestApplyInsets(content);
    }

    private sealed class SystemBarInsetsListener : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat OnApplyWindowInsets(View v, WindowInsetsCompat insets)
        {
            var bars = insets.GetInsets(
                WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout());
            v.SetPadding(bars.Left, bars.Top, bars.Right, bars.Bottom);
            return insets;
        }
    }
}
