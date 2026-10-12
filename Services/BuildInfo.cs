namespace TLIGDashboard.Services;

/// <summary>
/// Compile-time flavor identity. The project builds in two flavors from one
/// codebase (see <c>TLIGDashboard.csproj</c>, property <c>Flavor</c>):
///
///   • SERVER — hosts the share server: broadcasts its camera + HMI screen over
///     WebSocket and proxies AI chat using its own API key. Holds all settings.
///   • CLIENT — connects to a server, receives the camera + HMI stream, and
///     routes AI chat through the server proxy (no local API key needed).
///
/// Most code branches at runtime on <see cref="IsServer"/> so both paths stay
/// compiled in both flavors; <c>#if SERVER/CLIENT</c> is used only for branding.
/// </summary>
internal static class BuildInfo
{
#if SERVER
    public const string Flavor      = "Server";
    public const string ProductName = "TLIG Dashboard Server";
#else
    public const string Flavor      = "Client";
    public const string ProductName = "TLIG Dashboard Client";
#endif

    // static readonly (not const) so both flavor branches stay compiled without
    // CS0162 unreachable-code warnings; the JIT still folds these to constants.
    public static readonly bool IsServer = Flavor == "Server";
    public static readonly bool IsClient = !IsServer;

    /// <summary>
    /// True in the Windows App SDK build (the normal Server / Client .exe). False in the
    /// Uno Platform head (<c>Android\TLIGDashboard.Android.csproj</c>) that runs the Client
    /// on Android tablets from this same XAML and code: there is no Win32 window, caption
    /// buttons, Mica, Explorer, or installer-based update there, so code that touches
    /// those checks this first.
    /// </summary>
#if HAS_UNO
    public static readonly bool IsWinUI = false;
#else
    public static readonly bool IsWinUI = true;
#endif
}
