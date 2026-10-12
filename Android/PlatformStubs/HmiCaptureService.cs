namespace TLIGDashboard.Services;

// Pengganti Services/HmiCaptureService.cs untuk head Android.
//
// Yang asli menangkap layar/jendela HMI lewat GDI (System.Drawing + user32), jadi
// hanya bisa hidup di Windows dan hanya dipakai Server. Client tidak pernah menangkap
// layar — ia menampilkan siaran dari ShareClient — sehingga di sini cukup bentuk API
// yang sama tanpa isi, agar Controls/HmiShareView tetap terkompilasi apa adanya.

public enum CaptureSourceKind
{
    Display,
    Window
}

public readonly record struct CaptureBounds(int Left, int Top, int Width, int Height);

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

public sealed class HmiCaptureService
{
    public static HmiCaptureService Instance { get; } = new();
    private HmiCaptureService() { }

#pragma warning disable CS0067 // event tidak pernah dipicu: memang tidak ada tangkapan layar di Android
    public event Action? SourcesChanged;
    public event Action? ActiveSourceChanged;
    public event Action<byte[]>? FrameAvailable;
#pragma warning restore CS0067

    public IReadOnlyList<CaptureSource> Sources => [];
    public CaptureSource? ActiveSource => null;

    public void RefreshSources() { }
    public void Select(CaptureSource? source) { }
    public void Stop() { }
}
