using System.Globalization;
using Microsoft.UI.Xaml;

namespace TLIGDashboard;

class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture   = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

#if DEBUG
        // Build Debug dijalankan lewat dotnet.exe (lihat TLIGDashboard.csproj), jadi
        // folder exe proses adalah C:\Program Files\dotnet. Tanpa ini MRT Core mencari
        // resources.pri di sana dan InitializeComponent gagal dengan XamlParseException
        // ("Cannot locate resource from 'ms-appx:///MainWindow.xaml'"). MRM.dll hanya
        // memakai BASE_DIRECTORY bila _PID sama dengan PID proses ini.
        Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", AppContext.BaseDirectory);
        Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY_PID", Environment.ProcessId.ToString());
#endif

        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start((p) =>
            {
                try
                {
                    var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                    SynchronizationContext.SetSynchronizationContext(context);
                    _ = new App();
                }
                catch (Exception ex)
                {
                    LogError(ex);
                    throw;
                }
            });
        }
        catch (Exception ex)
        {
            LogError(ex);
        }
    }

    static void LogError(Exception ex)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "TLIGDashboard_Error.txt");
            File.WriteAllText(path, $"[{DateTime.Now}]\n{ex}");
        }
        catch { }
    }
}
