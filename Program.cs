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
