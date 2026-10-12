using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;

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
    }
}
