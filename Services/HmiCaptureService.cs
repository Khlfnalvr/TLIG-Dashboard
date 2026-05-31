using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using WinRT.Interop;

namespace TLIGDashboard.Services;

public enum CaptureSourceKind
{
    Display,
    Window
}

public readonly record struct CaptureBounds(int Left, int Top, int Width, int Height);

/// <summary>
/// A monitor or a top-level window that can be captured for HMI sharing.
/// <see cref="Matches"/> compares identity (window handle / monitor bounds) so a
/// selection survives a source-list refresh even when window bounds have changed.
/// </summary>
public sealed record CaptureSource(
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

/// <summary>
/// Single, app-wide HMI capture engine shared by every <see cref="Controls.HmiShareView"/>
/// instance (the dashboard panel and the Live View page). Owning the source list and the
/// currently selected source in ONE place keeps every view in sync — selecting a source in
/// the dashboard immediately reflects on Live View and vice-versa — and means there is a
/// single capture loop pushing frames to <see cref="ShareServer"/> instead of one per view.
///
/// Views drive it through <see cref="RefreshSources"/> / <see cref="Select"/> / <see cref="Stop"/>
/// and render the PNG handed to them by <see cref="FrameAvailable"/>. Server-only: the client
/// build renders the remote stream from <see cref="ShareClient"/> and never touches this.
/// </summary>
public sealed class HmiCaptureService
{
    public static HmiCaptureService Instance { get; } = new();
    private HmiCaptureService() { }

    private const int CaptureIntervalMs = 120;

    private readonly object _gate = new();
    private readonly List<CaptureSource> _sources = [];
    private CaptureSource? _activeSource;
    private System.Threading.Timer? _timer;
    private int _capturing; // 0 = idle, 1 = busy (guards against overlapping ticks)

    /// <summary>Raised (on the UI thread) after <see cref="RefreshSources"/> rebuilds the list.</summary>
    public event Action? SourcesChanged;
    /// <summary>Raised (on the UI thread) whenever the selected source changes, from any view.</summary>
    public event Action? ActiveSourceChanged;
    /// <summary>Raised on a background thread with the latest preview frame (PNG bytes).</summary>
    public event Action<byte[]>? FrameAvailable;

    /// <summary>Snapshot of the currently enumerated sources.</summary>
    public IReadOnlyList<CaptureSource> Sources
    {
        get { lock (_gate) return _sources.ToArray(); }
    }

    /// <summary>The source every view is currently capturing/showing (null = stopped).</summary>
    public CaptureSource? ActiveSource
    {
        get { lock (_gate) return _activeSource; }
    }

    // ── Source enumeration ────────────────────────────────────────────────────

    /// <summary>Re-enumerates displays + windows and notifies all views via <see cref="SourcesChanged"/>.</summary>
    public void RefreshSources()
    {
        var list = new List<CaptureSource>();
        list.AddRange(EnumerateDisplaySources());
        list.AddRange(EnumerateWindowSources());

        lock (_gate)
        {
            _sources.Clear();
            _sources.AddRange(list);
        }
        SourcesChanged?.Invoke();
    }

    // ── Selection / capture lifecycle ─────────────────────────────────────────

    /// <summary>Selects the source to capture (or null to stop) and (re)starts the shared loop.</summary>
    public void Select(CaptureSource? source)
    {
        lock (_gate)
        {
            _activeSource = source;
            if (source is null)
            {
                _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            }
            else
            {
                _timer ??= new System.Threading.Timer(_ => OnTick(), null, Timeout.Infinite, Timeout.Infinite);
                _timer.Change(0, CaptureIntervalMs); // fire one frame immediately, then every interval
            }
        }
        ActiveSourceChanged?.Invoke();
    }

    public void Stop() => Select(null);

    private void OnTick()
    {
        // Drop ticks that arrive while the previous capture is still running.
        if (Interlocked.CompareExchange(ref _capturing, 1, 0) != 0)
            return;
        try
        {
            var source = ActiveSource;
            if (source is null)
                return;

            bool share = ShareServer.Instance.IsRunning && ShareServer.Instance.ShareHmi;

            // Nothing consumes a frame when we are neither broadcasting nor showing a
            // live preview (no view subscribed) — skip the capture/encode work entirely.
            if (!share && FrameAvailable is null)
                return;

            var frame = CaptureSourceFrame(source, share);
            if (frame.Png is null)
                return;

            if (share && frame.Jpeg is not null)
                ShareServer.Instance.PushHmiFrame(frame.Jpeg);

            FrameAvailable?.Invoke(frame.Png);
        }
        catch
        {
            // Drop a bad frame (e.g. the window closed mid-capture) and keep the loop
            // alive; the user can pick another source without sharing tearing down.
        }
        finally
        {
            Interlocked.Exchange(ref _capturing, 0);
        }
    }

    // ── Frame capture (PrintWindow for windows, CopyFromScreen for monitors) ───

    private readonly record struct CapturedFrame(byte[]? Png, byte[]? Jpeg);

    private static CapturedFrame CaptureSourceFrame(CaptureSource source, bool alsoJpeg)
    {
        var bounds = source.Kind == CaptureSourceKind.Window
            ? GetWindowBounds(source.WindowHandle)
            : source.Bounds;

        if (bounds.Width <= 0 || bounds.Height <= 0)
            return default;

        // 24bpp RGB (no alpha): screen/window content is opaque, and rendering into a
        // 32bpp ARGB surface via PrintWindow leaves the alpha channel at 0, which makes
        // the PNG preview come out fully transparent. 24bpp sidesteps that entirely.
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);

        // A window source must capture the window's OWN rendered content, not the
        // screen pixels at its location — otherwise a window sitting on top (e.g. the
        // dashboard over a LabVIEW HMI) is what gets captured. PrintWindow asks the
        // target window to paint itself into our DC and works even when it is fully
        // occluded or in the background. CopyFromScreen is correct only for whole
        // monitors (Display sources) and as a fallback if PrintWindow fails.
        bool captured = source.Kind == CaptureSourceKind.Window
                        && TryPrintWindow(source.WindowHandle, bitmap);

        if (!captured)
        {
            using var graphics = Graphics.FromImage(bitmap);
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

    /// <summary>
    /// Renders a window's actual content into <paramref name="bitmap"/> via the
    /// Win32 PrintWindow API, so the real window is captured even while it is hidden
    /// behind other windows. PW_RENDERFULLCONTENT (Windows 8.1+) makes this work for
    /// DirectComposition / hardware-accelerated windows too. Returns false if the API
    /// fails so the caller can fall back to a screen-region copy.
    /// </summary>
    private static bool TryPrintWindow(IntPtr hwnd, Bitmap bitmap)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        using var graphics = Graphics.FromImage(bitmap);
        IntPtr hdc = graphics.GetHdc();
        try
        {
            return PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
        }
        finally
        {
            graphics.ReleaseHdc(hdc);
        }
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

    private readonly record struct MonitorInfo(CaptureBounds Bounds, bool IsPrimary);

    // ── Win32 interop ─────────────────────────────────────────────────────────

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    private const int MONITORINFOF_PRIMARY = 0x00000001;

    // Capture the full, composited window surface (incl. DWM / DirectComposition
    // content), not just the GDI paint — required so occluded modern windows are
    // captured correctly instead of coming back black.
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

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
