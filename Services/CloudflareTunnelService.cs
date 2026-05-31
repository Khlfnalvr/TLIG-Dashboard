using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace TLIGDashboard.Services;

public enum TunnelState
{
    Stopped,
    Starting,
    Running,
    Downloading,
    Error,
}

/// <summary>
/// Manages a <c>cloudflared</c> tunnel that exposes the local share-server port to the
/// public internet — clients on any network connect to the Cloudflare edge and the
/// traffic is tunnelled back to this machine (no port forwarding / VPN required).
///
/// Two modes:
///   • <b>Quick tunnel</b> (default): a random <c>*.trycloudflare.com</c> URL, no
///     account needed. The URL changes every run.
///   • <b>Named tunnel</b> (custom domain): a fixed URL on the user's own domain.
///     Requires a one-time browser sign-in (<c>cloudflared tunnel login</c>) which
///     authorizes a Cloudflare zone; after that this service automatically creates a
///     named tunnel and a DNS route for the chosen hostname, then runs it. The public
///     URL stays the same across restarts.
///
/// Binary resolution order (first match wins):
///   1. <c>cloudflared.exe</c> in the app's base directory (bundled in the installer)
///   2. <c>%LOCALAPPDATA%\TLIGDashboard\cloudflared.exe</c>  (auto-downloaded)
/// </summary>
public sealed class CloudflareTunnelService
{
    public static CloudflareTunnelService Instance { get; } = new();
    private CloudflareTunnelService() { }

    private const string ExeName    = "cloudflared.exe";
    public  const string DefaultTunnelName = "tlig-dashboard";
    private const string DownloadUrl =
        "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";

    // Matches any *.trycloudflare.com URL in cloudflared's output (quick tunnels).
    private static readonly Regex _urlRx =
        new(@"https://[a-zA-Z0-9][a-zA-Z0-9\-]*\.trycloudflare\.com",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches the "connection registered" lines a named tunnel prints once it is live.
    private static readonly Regex _connRx =
        new(@"Registered tunnel connection|Connection [0-9a-fA-F-]+ registered",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches the Cloudflare sign-in URL printed by `cloudflared tunnel login`.
    private static readonly Regex _loginUrlRx =
        new(@"https://\S*argotunnel\S*",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── Public state ───────────────────────────────────────────────────────────
    public TunnelState State            { get; private set; } = TunnelState.Stopped;
    public string      TunnelUrl        { get; private set; } = "";
    public string      LastError        { get; private set; } = "";
    public double      DownloadProgress { get; private set; }          // 0..1

    // Named-tunnel sign-in state.
    public bool   IsLoggingIn { get; private set; }
    public string LoginUrl    { get; private set; } = "";              // shown if the browser doesn't open
    public bool   IsLoggedIn  => File.Exists(CertPath);

    public bool IsInstalled  => FindExe() is not null;
    public bool IsRunning    => State == TunnelState.Running;

    public event Action? StateChanged;

    // ── Process ────────────────────────────────────────────────────────────────
    private Process?                  _process;
    private CancellationTokenSource?  _timeoutCts;
    private bool                      _namedMode;
    private string                    _pendingHostname = "";

    // ── Paths ──────────────────────────────────────────────────────────────────
    private static string AppBaseDir =>
        AppContext.BaseDirectory.TrimEnd('\\', '/');

    private static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TLIGDashboard");

    // cloudflared stores its account cert + tunnel credentials here by default.
    private static string CloudflaredHomeDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cloudflared");
    private static string CertPath => Path.Combine(CloudflaredHomeDir, "cert.pem");

    public string? FindExe()
    {
        var bundled    = Path.Combine(AppBaseDir, ExeName);
        if (File.Exists(bundled))    return bundled;
        var downloaded = Path.Combine(DataDir, ExeName);
        if (File.Exists(downloaded)) return downloaded;
        return null;
    }

    // ── Tunnel lifecycle ───────────────────────────────────────────────────────

    /// <summary>
    /// Entry point from the UI. Reads the saved settings and starts either a named
    /// tunnel (custom domain) or a quick tunnel.
    /// </summary>
    public void Start(int port)
    {
        var s = AppSettingsService.Load();
        if (s.TunnelUseCustomDomain && !string.IsNullOrWhiteSpace(s.TunnelCustomDomain))
        {
            var name = string.IsNullOrWhiteSpace(s.TunnelName)
                ? DefaultTunnelName : s.TunnelName.Trim();
            _ = StartNamedAsync(port, s.TunnelCustomDomain.Trim(), name);
        }
        else
        {
            StartQuick(port);
        }
    }

    // ── Quick tunnel (*.trycloudflare.com) ──────────────────────────────────────
    private void StartQuick(int port)
    {
        if (State is TunnelState.Running or TunnelState.Starting) return;
        var exe = FindExe();
        if (exe is null) { Set(TunnelState.Error, "cloudflared.exe not found"); return; }

        Stop();
        TunnelUrl = "";
        _namedMode = false;
        Set(TunnelState.Starting);

        ArmTimeout("Timeout: tunnel URL not received within 90 s.");

        // --no-autoupdate: prevents cloudflared from spawning an updater child process.
        LaunchProcess(exe, $"tunnel --url http://localhost:{port} --no-autoupdate");
    }

    // ── Named tunnel (custom domain) ─────────────────────────────────────────────
    private async Task StartNamedAsync(int port, string hostname, string tunnelName)
    {
        if (State is TunnelState.Running or TunnelState.Starting) return;
        var exe = FindExe();
        if (exe is null) { Set(TunnelState.Error, "cloudflared.exe not found"); return; }
        if (!IsLoggedIn)
        {
            Set(TunnelState.Error, "Not signed in to Cloudflare. Click \"Connect domain\" first.");
            return;
        }

        Stop();
        TunnelUrl = "";
        _namedMode = true;
        _pendingHostname = hostname;
        Set(TunnelState.Starting);

        // 1. Ensure the named tunnel exists (idempotent — "already exists" is fine).
        var create = await RunAsync(exe, $"tunnel create {tunnelName}");
        if (!create.ok && !create.output.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            Set(TunnelState.Error, $"create tunnel: {FirstLine(create.output)}");
            return;
        }

        // 2. Route the hostname's DNS (CNAME) to this tunnel (idempotent).
        var route = await RunAsync(exe, $"tunnel route dns {tunnelName} {hostname}");
        if (!route.ok &&
            !route.output.Contains("already", StringComparison.OrdinalIgnoreCase) &&
            !route.output.Contains("exists",  StringComparison.OrdinalIgnoreCase))
        {
            Set(TunnelState.Error, $"route DNS: {FirstLine(route.output)}");
            return;
        }

        // Still starting? (Stop() could have been called meanwhile.)
        if (State != TunnelState.Starting) return;

        // 3. Run the tunnel — serves the configured hostname from the local port.
        ArmTimeout("Timeout: tunnel did not connect within 90 s.");
        LaunchProcess(exe, $"tunnel --no-autoupdate run --url http://localhost:{port} {tunnelName}");
    }

    private void LaunchProcess(string exe, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = exe,
            Arguments              = arguments,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += OnLine;
        _process.ErrorDataReceived  += OnLine;
        _process.Exited += (_, _) =>
        {
            if (State is TunnelState.Starting or TunnelState.Running)
            {
                TunnelUrl = "";
                Set(TunnelState.Stopped);
            }
        };

        try
        {
            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            Set(TunnelState.Error, ex.Message);
        }
    }

    private void ArmTimeout(string message)
    {
        _timeoutCts = new CancellationTokenSource();
        var ct = _timeoutCts.Token;
        _ = Task.Delay(90_000, ct).ContinueWith(_ =>
        {
            if (!ct.IsCancellationRequested && State == TunnelState.Starting)
                Set(TunnelState.Error, message);
        }, TaskScheduler.Default);
    }

    public void Stop()
    {
        _timeoutCts?.Cancel();
        _timeoutCts?.Dispose();
        _timeoutCts = null;

        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch { }
            try { _process.Dispose(); } catch { }
            _process = null;
        }

        TunnelUrl = "";
        if (State != TunnelState.Stopped)
            Set(TunnelState.Stopped);
    }

    private void OnLine(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null || State != TunnelState.Starting) return;

        if (_namedMode)
        {
            if (_connRx.IsMatch(e.Data))
            {
                TunnelUrl = $"https://{_pendingHostname}";
                _timeoutCts?.Cancel();   // connected, disarm timeout
                Set(TunnelState.Running);
            }
            return;
        }

        var m = _urlRx.Match(e.Data);
        if (m.Success)
        {
            TunnelUrl = m.Value;
            _timeoutCts?.Cancel();   // URL received, disarm timeout
            Set(TunnelState.Running);
        }
    }

    // ── Sign-in (named tunnel) ───────────────────────────────────────────────────

    /// <summary>
    /// Runs <c>cloudflared tunnel login</c>, which opens the browser for the user to
    /// authorize one of their Cloudflare zones. Completes when the account cert is
    /// written (or the operation times out / is cancelled).
    /// </summary>
    public async Task LoginAsync(CancellationToken ct = default)
    {
        if (IsLoggingIn) return;
        var exe = FindExe();
        if (exe is null) { Set(TunnelState.Error, "cloudflared.exe not found"); return; }

        IsLoggingIn = true;
        LoginUrl    = "";
        LastError   = "";
        RaiseStateChanged();

        Process? login = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = exe,
                Arguments              = "tunnel login",
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            login = new Process { StartInfo = psi, EnableRaisingEvents = true };
            login.OutputDataReceived += OnLoginLine;
            login.ErrorDataReceived  += OnLoginLine;
            login.Start();
            login.BeginOutputReadLine();
            login.BeginErrorReadLine();

            // Wait until the cert appears (success), the process exits, the caller
            // cancels, or we hit a generous timeout (browser sign-in is manual).
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            while (!IsLoggedIn && !login.HasExited)
            {
                if (timeout.IsCancellationRequested) break;
                await Task.Delay(500, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            Set(TunnelState.Error, ex.Message);
        }
        finally
        {
            try { if (login is { HasExited: false }) login.Kill(entireProcessTree: true); } catch { }
            try { login?.Dispose(); } catch { }
            IsLoggingIn = false;
            if (!IsLoggedIn && string.IsNullOrEmpty(LastError))
                LastError = "Sign-in was not completed.";
            RaiseStateChanged();
        }
    }

    private void OnLoginLine(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null) return;
        var m = _loginUrlRx.Match(e.Data);
        if (m.Success)
        {
            LoginUrl = m.Value;
            RaiseStateChanged();
        }
    }

    // ── Run a short cloudflared command and capture its combined output ───────────
    private static async Task<(bool ok, string output)> RunAsync(
        string exe, string arguments, int timeoutMs = 30_000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = exe,
                Arguments              = arguments,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            using var p = new Process { StartInfo = psi };
            var sb = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) sb.AppendLine(e.Data); };
            p.ErrorDataReceived  += (_, e) => { if (e.Data is not null) sb.AppendLine(e.Data); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            if (!await Task.Run(() => p.WaitForExit(timeoutMs)))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return (false, "timeout");
            }
            return (p.ExitCode == 0, sb.ToString().Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string FirstLine(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "unknown error";
        var line = s.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();
        return string.IsNullOrEmpty(line) ? "unknown error" : line;
    }

    // ── Download ───────────────────────────────────────────────────────────────

    public async Task<bool> DownloadAsync(CancellationToken ct = default)
    {
        Set(TunnelState.Downloading);
        DownloadProgress = 0;
        RaiseStateChanged();

        Directory.CreateDirectory(DataDir);
        var target = Path.Combine(DataDir, ExeName);
        var tmp    = target + ".tmp";

        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "TLIGDashboard");

            // GitHub releases redirect to the actual CDN asset.
            using var resp = await http.GetAsync(
                DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength ?? -1L;
            await using var net  = await resp.Content.ReadAsStreamAsync(ct);
            await using var file = File.Create(tmp);

            var  buf  = new byte[65536];
            long done = 0;
            int  n;
            while ((n = await net.ReadAsync(buf, ct)) > 0)
            {
                await file.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0)
                {
                    DownloadProgress = (double)done / total;
                    RaiseStateChanged();
                }
            }

            file.Close();
            File.Move(tmp, target, overwrite: true);
            Set(TunnelState.Stopped);
            return true;
        }
        catch (Exception ex)
        {
            try { File.Delete(tmp); } catch { }
            Set(TunnelState.Error, ex.Message);
            return false;
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────
    private void Set(TunnelState s, string err = "") { State = s; LastError = err; RaiseStateChanged(); }
    private void RaiseStateChanged() => StateChanged?.Invoke();
}
