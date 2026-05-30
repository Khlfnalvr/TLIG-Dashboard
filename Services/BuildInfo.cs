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
    public const bool   IsServer    = true;
    public const string Flavor      = "Server";
    public const string ProductName = "TLIG Dashboard Server";
#else
    public const bool   IsServer    = false;
    public const string Flavor      = "Client";
    public const string ProductName = "TLIG Dashboard Client";
#endif

    public const bool IsClient = !IsServer;
}
