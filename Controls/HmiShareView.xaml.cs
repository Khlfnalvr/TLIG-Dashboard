using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using TLIGDashboard.Services;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace TLIGDashboard.Controls;

public sealed partial class HmiShareView : UserControl
{
    private readonly DispatcherTimer _captureTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(120)
    };

    private readonly List<CaptureSource> _sources = [];
    private bool _isRefreshingSources;
    private bool _isCapturingFrame;
    private CaptureSource? _activeSource;
    private readonly bool _clientMode = BuildInfo.IsClient;

    private LocalizationManager Lang => App.Lang;

    public HmiShareView()
    {
        InitializeComponent();
        RefreshLocalizedText();
        Lang.PropertyChanged += Lang_PropertyChanged;

        if (_clientMode)
        {
            // Client: this control displays the HMI stream received from the
            // server instead of capturing a local screen.
            SourceSelector.Visibility       = Visibility.Collapsed;
            RefreshSourcesButton.Visibility  = Visibility.Collapsed;
            ShowPlaceholder(Lang.Hmi_WaitingStream);
            // Subscribe on every load; unsubscribe on unload. (The hosting page is
            // cached, so hooking once in the ctor would be dropped after the first
            // navigation away.)
            Loaded   += (_, _) =>
            {
                ShareClient.Instance.FrameReceived -= OnRemoteFrame;
                ShareClient.Instance.FrameReceived += OnRemoteFrame;
            };
            Unloaded += (_, _) => ShareClient.Instance.FrameReceived -= OnRemoteFrame;
        }
        else
        {
            Loaded += HmiShareView_Loaded;
            Unloaded += HmiShareView_Unloaded;
            _captureTimer.Tick += CaptureTimer_Tick;
        }
    }

    // ── Client mode: render frames pushed from the server ─────────────────────
    private void OnRemoteFrame(byte channel, byte[] bytes)
    {
        if (channel != ShareProtocol.ChannelHmi) return;
        DispatcherQueue.TryEnqueue(async () => await RenderRemoteAsync(bytes));
    }

    private async Task RenderRemoteAsync(byte[] bytes)
    {
        try
        {
            var bitmap = new BitmapImage();
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
            CaptureImage.Source = bitmap;
            CaptureImage.Visibility = Visibility.Visible;
            PlaceholderPanel.Visibility = Visibility.Collapsed;
        }
        catch { /* drop a bad frame */ }
    }

    private void HmiShareView_Loaded(object sender, RoutedEventArgs e)
        => RefreshSources();

    private void HmiShareView_Unloaded(object sender, RoutedEventArgs e)
        => StopCapture(Lang.Hmi_SelectPlaceholder);

    private void Lang_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        RefreshLocalizedText();
        if (_clientMode)
        {
            if (CaptureImage.Source is null)
                ShowPlaceholder(Lang.Hmi_WaitingStream);
        }
        else if (_activeSource is null)
        {
            ShowPlaceholder(Lang.Hmi_SelectPlaceholder);
        }
    }

    private void RefreshLocalizedText()
    {
        HeaderText.Text = Lang.Hmi_Header;
        SourceSelector.PlaceholderText = Lang.Hmi_SelectSource;
        RefreshSourcesButton.Content = Lang.Hmi_RefreshSources;
        StopShareButton.Content = Lang.Hmi_StopShare;
    }

    private void RefreshSourcesButton_Click(object sender, RoutedEventArgs e)
        => RefreshSources();

    private void SourceSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingSources || SourceSelector.SelectedItem is not CaptureSource source)
            return;

        StartCapture(source);
    }

    private void StopShareButton_Click(object sender, RoutedEventArgs e)
        => StopCapture(Lang.Hmi_SelectPlaceholder);

    private void RefreshSources()
    {
        _isRefreshingSources = true;
        _sources.Clear();

        foreach (var source in EnumerateDisplaySources())
            _sources.Add(source);
        foreach (var source in EnumerateWindowSources())
            _sources.Add(source);

        SourceSelector.ItemsSource = null;
        SourceSelector.ItemsSource = _sources;
        SourceSelector.DisplayMemberPath = nameof(CaptureSource.Name);
        SourceSelector.SelectedItem = _activeSource is null
            ? null
            : _sources.FirstOrDefault(s => s.Matches(_activeSource));
        _isRefreshingSources = false;

        if (_sources.Count == 0)
            ShowPlaceholder(Lang.Hmi_NoSource);
        else if (_activeSource is null)
            ShowPlaceholder(Lang.Hmi_SelectPlaceholder);
    }

    private void StartCapture(CaptureSource source)
    {
        _activeSource = source;
        CaptureImage.Visibility = Visibility.Visible;
        PlaceholderPanel.Visibility = Visibility.Collapsed;
        StopShareButton.Visibility = Visibility.Visible;
        _captureTimer.Start();
        _ = CaptureAndRenderAsync();
    }

    private void StopCapture(string placeholderMessage)
    {
        _captureTimer.Stop();
        _activeSource = null;
        _isCapturingFrame = false;
        CaptureImage.Source = null;
        CaptureImage.Visibility = Visibility.Collapsed;
        StopShareButton.Visibility = Visibility.Collapsed;
        if (!_isRefreshingSources)
            SourceSelector.SelectedItem = null;
        ShowPlaceholder(placeholderMessage);
    }

    private void ShowPlaceholder(string message)
    {
        PlaceholderText.Text = message;
        PlaceholderPanel.Visibility = Visibility.Visible;
    }

    private async void CaptureTimer_Tick(object? sender, object e)
        => await CaptureAndRenderAsync();

    private async Task CaptureAndRenderAsync()
    {
        if (_isCapturingFrame || _activeSource is null)
            return;

        _isCapturingFrame = true;
        var source = _activeSource;

        bool share = ShareServer.Instance.IsRunning && ShareServer.Instance.ShareHmi;

        try
        {
            var frame = await Task.Run(() => CaptureSourceFrame(source, share));
            if (frame.Png is null || !ReferenceEquals(source, _activeSource))
                return;

            var bitmap = new BitmapImage();
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(frame.Png);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
            if (ReferenceEquals(source, _activeSource))
                CaptureImage.Source = bitmap;

            // Broadcast the JPEG copy to connected clients.
            if (share && frame.Jpeg is not null)
                ShareServer.Instance.PushHmiFrame(frame.Jpeg);
        }
        catch (Exception ex)
        {
            StopCapture(Lang.Format(nameof(LocalizationManager.Hmi_CaptureError), ex.Message));
        }
        finally
        {
            _isCapturingFrame = false;
        }
    }

    private readonly record struct CapturedFrame(byte[]? Png, byte[]? Jpeg);

    private static CapturedFrame CaptureSourceFrame(CaptureSource source, bool alsoJpeg)
    {
        var bounds = source.Kind == CaptureSourceKind.Window
            ? GetWindowBounds(source.WindowHandle)
            : source.Bounds;

        if (bounds.Width <= 0 || bounds.Height <= 0)
            return default;

        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(
                bounds.Left,
                bounds.Top,
                0,
                0,
                new System.Drawing.Size(bounds.Width, bounds.Height),
                CopyPixelOperation.SourceCopy);
        }

        using var pngMs = new MemoryStream();
        bitmap.Save(pngMs, ImageFormat.Png);

        byte[]? jpeg = null;
        if (alsoJpeg)
            jpeg = EncodeJpeg(bitmap, 70L);

        return new CapturedFrame(pngMs.ToArray(), jpeg);
    }

    private static ImageCodecInfo? _jpegCodec;

    private static byte[] EncodeJpeg(Bitmap bitmap, long quality)
    {
        _jpegCodec ??= ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);

        using var ms = new MemoryStream();
        if (_jpegCodec is not null)
        {
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
            bitmap.Save(ms, _jpegCodec, ep);
        }
        else
        {
            bitmap.Save(ms, ImageFormat.Jpeg);
        }
        return ms.ToArray();
    }

    private static IEnumerable<CaptureSource> EnumerateDisplaySources()
    {
        int index = 1;
        foreach (var monitor in GetMonitors())
        {
            string primary = monitor.IsPrimary ? " Primary" : "";
            yield return new CaptureSource(
                $"Display {index}{primary} ({monitor.Bounds.Width}x{monitor.Bounds.Height})",
                CaptureSourceKind.Display,
                IntPtr.Zero,
                monitor.Bounds);
            index++;
        }
    }

    private static IEnumerable<CaptureSource> EnumerateWindowSources()
    {
        IntPtr currentWindow = App.CurrentWindow is null
            ? IntPtr.Zero
            : WindowNative.GetWindowHandle(App.CurrentWindow);
        var windows = new List<CaptureSource>();

        EnumWindows((hwnd, _) =>
        {
            if (hwnd == currentWindow || !IsWindowVisible(hwnd))
                return true;

            int titleLength = GetWindowTextLength(hwnd);
            if (titleLength <= 0)
                return true;

            var title = new StringBuilder(titleLength + 1);
            _ = GetWindowText(hwnd, title, title.Capacity);
            string name = title.ToString().Trim();
            if (string.IsNullOrWhiteSpace(name))
                return true;

            var bounds = GetWindowBounds(hwnd);
            if (bounds.Width < 160 || bounds.Height < 120)
                return true;

            windows.Add(new CaptureSource(name, CaptureSourceKind.Window, hwnd, bounds));
            return true;
        }, IntPtr.Zero);

        return windows
            .OrderByDescending(w => w.Name.Contains("LabVIEW", StringComparison.OrdinalIgnoreCase))
            .ThenBy(w => w.Name, StringComparer.CurrentCultureIgnoreCase);
    }

    private static List<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MONITORINFOEX();
            info.cbSize = Marshal.SizeOf<MONITORINFOEX>();
            if (GetMonitorInfo(monitor, ref info))
            {
                monitors.Add(new MonitorInfo(
                    ToBounds(info.rcMonitor),
                    (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
            }
            return true;
        }, IntPtr.Zero);
        return monitors;
    }

    private static CaptureBounds GetWindowBounds(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect))
            return default;
        return ToBounds(rect);
    }

    private static CaptureBounds ToBounds(RECT rect) =>
        new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    private enum CaptureSourceKind
    {
        Display,
        Window
    }

    private sealed record CaptureSource(
        string Name,
        CaptureSourceKind Kind,
        IntPtr WindowHandle,
        CaptureBounds Bounds)
    {
        public bool Matches(CaptureSource other) =>
            Kind == other.Kind &&
            (Kind == CaptureSourceKind.Display
                ? Bounds.Equals(other.Bounds)
                : WindowHandle == other.WindowHandle);
    }

    private readonly record struct CaptureBounds(int Left, int Top, int Width, int Height);
    private readonly record struct MonitorInfo(CaptureBounds Bounds, bool IsPrimary);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    private const int MONITORINFOF_PRIMARY = 0x00000001;

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(
        IntPtr hdc,
        IntPtr lprcClip,
        MonitorEnumProc lpfnEnum,
        IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }
}
