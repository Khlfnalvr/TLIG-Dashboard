using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace TLIGDashboard.Services;

/// <summary>
/// Bridges the HMI's PID controls to the external Python client (PIDtest.py), which
/// forwards SP/KC/KI/KD to LabVIEW over its own TCP socket:
///
///   TLIG Dashboard ──pid_bridge.json──▶ PIDtest.py ──TCP(struct)──▶ LabVIEW
///
/// Two directions of control, matching the two things the user does in the HMI:
///
///   • Parameter sync — every time Kp/Ki/Kd/Setpoint change in the dashboard the
///     values are written to a small JSON file that sits *next to* the Python
///     script. The script re-reads that file on every send, so a change made in
///     the HMI reaches LabVIEW on the next cycle without restarting anything.
///
///   • Run / Stop — the RUN button launches "python PIDtest.py" as a child
///     process (STOP flags it to exit and kills it), so pressing RUN in the HMI
///     is what actually starts the Python client.
///
/// The JSON file is the single contract with the script:
///     { "sp": 60, "kp": 1.5, "ki": 0.015, "kd": 8, "run": true, "host": "127.0.0.1", "port": 6000 }
/// "host"/"port" tell the script which LabVIEW to reach — taken from the dashboard's
/// "Host / IP address (LabVIEW HMI)" setting, so another PC's IP is all it takes to
/// drive a remote LabVIEW (no editing the Python script on each machine).
/// It lives beside the script (derived from <see cref="AppSettings.PythonScriptPath"/>)
/// so the script can locate it from its own __file__ with no extra configuration.
///
/// <para><b>Server vs Client.</b> Only the Server PC is wired to LabVIEW, so the local
/// process/JSON path above runs on the <b>Server</b> flavor. On the <b>Client</b> flavor
/// the same <see cref="Run"/> / <see cref="Stop"/> / <see cref="SyncParams"/> calls are
/// forwarded over HTTP to the signed-in server (<see cref="ShareProtocol.PidRunPath"/>),
/// which drives its own bridge — so the Client's RUN button reaches the lab LabVIEW just
/// like the Server's. This mirrors how <see cref="ControlEngineering.PidDesignService"/>
/// hides the server/client split, and keeps both callers (Dashboard + Parameter page)
/// flavor-agnostic.</para>
/// </summary>
public sealed class PythonBridgeService : IDisposable
{
    public static PythonBridgeService Instance { get; } = new();
    private PythonBridgeService() { }

    // ── Events ────────────────────────────────────────────────────────────────
    /// <summary>One line of the script's stdout/stderr.</summary>
    public event Action<string>? Output;
    /// <summary>Human-readable state / error text for the UI.</summary>
    public event Action<string>? StatusChanged;
    /// <summary>Raised when the child process starts (true) or exits (false).</summary>
    public event Action<bool>? RunningChanged;

    public bool IsRunning
    {
        get
        {
            // Client has no local process — RUN/STOP are forwarded to the server, so track
            // the last forwarded state instead of a child process handle.
            if (BuildInfo.IsClient) return _clientRunning;
            lock (_procLock) return _process is { HasExited: false };
        }
    }

    // Optimistic RUN/STOP state on the Client flavor (see ForwardToServer). Volatile: read
    // by IsRunning on the UI thread, written from the forward task.
    private volatile bool _clientRunning;

    // ── Internals ───────────────────────────────────────────────────────────────
    private Process?        _process;
    private readonly object _procLock = new();
    private readonly object _fileLock = new();

    // Last gains mirrored into the contract file, so a plain parameter change and a
    // RUN both write a complete, consistent file.
    private double _sp = 60, _kp = 1.5, _ki = 0.015, _kd = 8;
    private volatile bool _run;

    // Settings are read once, lazily — the interpreter and script path do not change
    // while the app runs (edit settings.json + restart to point at a different script).
    private AppSettings? _settings;
    private AppSettings  Cfg        => _settings ??= AppSettingsService.Load();
    private string       PythonExe  => string.IsNullOrWhiteSpace(Cfg.PythonExe) ? "python" : Cfg.PythonExe.Trim();

    // The configured script (default D:\PIDtest.py) when it exists on this machine;
    // otherwise the copy bundled next to the executable, so a fresh clone/install runs
    // the bridge with no per-machine setup. A user editing their own D:\PIDtest.py
    // still wins, because the configured path is preferred whenever it is present.
    private string ScriptPath
    {
        get
        {
            var configured = (Cfg.PythonScriptPath ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
                return configured;

            var bundled = Path.Combine(AppContext.BaseDirectory, "PIDtest.py");
            if (File.Exists(bundled))
                return bundled;

            return configured;   // neither exists → keep configured so the error names it
        }
    }

    // Where PIDtest.py should send the PID struct: the LabVIEW HMI host/port the user
    // configures in the dashboard ("Host / IP address (LabVIEW HMI)"). Shared with
    // PlcTcpService's setting so there is a single "where is LabVIEW" address. Written
    // into pid_bridge.json on every sync; PIDtest.py reads it each cycle, so pointing
    // the dashboard at another PC's IP is all it takes to reach a remote LabVIEW.
    private string LabViewHost => string.IsNullOrWhiteSpace(Cfg.PlcTcpHost) ? "127.0.0.1" : Cfg.PlcTcpHost.Trim();
    private int    LabViewPort => Cfg.PlcTcpPort is > 0 and <= 65535 ? Cfg.PlcTcpPort : 6000;

    /// <summary>The JSON contract file, next to the script so PIDtest.py finds it by __file__.</summary>
    private string ParamsFilePath
    {
        get
        {
            var dir = string.IsNullOrWhiteSpace(ScriptPath) ? null : Path.GetDirectoryName(ScriptPath);
            if (string.IsNullOrWhiteSpace(dir)) dir = AppSettingsService.FolderPath;
            return Path.Combine(dir!, "pid_bridge.json");
        }
    }

    // ── Parameter sync ──────────────────────────────────────────────────────────

    /// <summary>
    /// Mirrors the current gains/setpoint into the contract file. Called on every
    /// parameter change in the HMI; the running script picks them up on its next send.
    /// </summary>
    public void SyncParams(double kp, double ki, double kd, double sp)
    {
        _kp = kp; _ki = ki; _kd = kd; _sp = sp;
        if (BuildInfo.IsClient) { ForwardToServer("sync"); return; }
        WriteParamsFile();
    }

    // ── Run / Stop ────────────────────────────────────────────────────────────

    /// <summary>Writes the given gains with run=true, then launches the script (no-op if already up).</summary>
    public void Run(double kp, double ki, double kd, double sp)
    {
        // Re-read settings so a LabVIEW IP / script path the user just changed in the
        // dashboard takes effect on this RUN without needing an app restart.
        _settings = null;

        _kp = kp; _ki = ki; _kd = kd; _sp = sp;
        _run = true;
        if (BuildInfo.IsClient) { ForwardToServer("run"); return; }
        WriteParamsFile();   // run=true must be on disk before the script starts reading
        StartProcess();
    }

    /// <summary>Flags the script to exit (run=false) and terminates the child process.</summary>
    public void Stop()
    {
        _run = false;
        if (BuildInfo.IsClient) { ForwardToServer("stop"); return; }
        WriteParamsFile();   // a still-looping script sees run=false and exits cleanly…
        KillProcess();       // …and we make it immediate regardless.
    }

    // ── Client → Server forwarding ──────────────────────────────────────────────

    /// <summary>
    /// Client flavor only: sends the current gains + <paramref name="action"/> to the
    /// signed-in server, which runs it on its own bridge (the machine wired to LabVIEW).
    /// Fire-and-forget; the outcome is surfaced through <see cref="StatusChanged"/> and,
    /// for run/stop, <see cref="RunningChanged"/> so the RUN button state stays honest.
    /// </summary>
    private void ForwardToServer(string action)
    {
        var s     = AppSettingsService.Load();
        var host  = s.ServerHost;
        var token = s.ServerToken;

        // Snapshot the current contract so the async post is not racing later edits.
        double kp = _kp, ki = _ki, kd = _kd, sp = _sp;

        if (string.IsNullOrWhiteSpace(AuthClient.NormalizeHost(host)) || string.IsNullOrWhiteSpace(token))
        {
            // A background parameter sync stays silent; an explicit RUN/STOP reports why nothing happened.
            if (action is "run" or "stop")
                StatusChanged?.Invoke("Gagal mengirim perintah: belum tersambung ke server.");
            return;
        }

        // Optimistic RUN/STOP state so the UI reacts immediately; rolled back on failure.
        if (action == "run")  { _clientRunning = true;  RunningChanged?.Invoke(true); }
        if (action == "stop") { _clientRunning = false; RunningChanged?.Invoke(false); }

        _ = Task.Run(async () =>
        {
            bool ok = await PidRunClient.PostAsync(host, token, action, kp, ki, kd, sp);
            switch (action)
            {
                case "run":
                    if (!ok) { _clientRunning = false; RunningChanged?.Invoke(false); }
                    StatusChanged?.Invoke(ok
                        ? "Perintah RUN dikirim ke server (LabVIEW)."
                        : "Gagal mengirim perintah RUN ke server.");
                    break;
                case "stop":
                    StatusChanged?.Invoke(ok
                        ? "Perintah STOP dikirim ke server (LabVIEW)."
                        : "Gagal mengirim perintah STOP ke server.");
                    break;
                case "sync":
                    if (!ok) StatusChanged?.Invoke("Gagal menyinkronkan parameter ke server.");
                    break;
            }
        });
    }

    // ── File writer (atomic) ────────────────────────────────────────────────────

    private void WriteParamsFile()
    {
        // NumberBox yields NaN when cleared — coerce to 0 so the script always parses a number.
        static string Num(double v) =>
            (double.IsNaN(v) ? 0 : v).ToString("0.###############", CultureInfo.InvariantCulture);

        // Escape backslash/quote so an unusual host string can't break the JSON.
        static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        string json =
            "{\n" +
            $"  \"sp\": {Num(_sp)},\n" +
            $"  \"kp\": {Num(_kp)},\n" +
            $"  \"ki\": {Num(_ki)},\n" +
            $"  \"kd\": {Num(_kd)},\n" +
            $"  \"run\": {(_run ? "true" : "false")},\n" +
            $"  \"host\": \"{Esc(LabViewHost)}\",\n" +
            $"  \"port\": {LabViewPort}\n" +
            "}\n";

        try
        {
            var path = ParamsFilePath;
            var dir  = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            lock (_fileLock)
            {
                // Write to a sibling temp file then swap it in, so the script never reads a
                // half-written file (it could poll at any moment). The swap can briefly lose
                // to the script holding the file open for its (sub-millisecond) read, so retry.
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                for (int attempt = 0; ; attempt++)
                {
                    try { File.Move(tmp, path, overwrite: true); break; }
                    catch (IOException) when (attempt < 5) { System.Threading.Thread.Sleep(15); }
                }
            }
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Gagal menulis parameter ke {ParamsFilePath}: {ex.Message}");
        }
    }

    // ── Process management ──────────────────────────────────────────────────────

    private void StartProcess()
    {
        lock (_procLock)
        {
            if (_process is { HasExited: false }) return;   // already running

            var script = ScriptPath;
            if (string.IsNullOrWhiteSpace(script) || !File.Exists(script))
            {
                StatusChanged?.Invoke($"Script Python tidak ditemukan: \"{script}\". " +
                                      "Atur PythonScriptPath di settings.json.");
                return;
            }

            // Try the configured interpreter first, then the Windows "py" launcher — one of
            // them is almost always on PATH even when the other name isn't.
            foreach (var exe in Dedup(PythonExe, "py", "python"))
            {
                var proc = TryStart(exe, script);
                if (proc is null) continue;

                _process = proc;
                StatusChanged?.Invoke($"Menjalankan {Path.GetFileName(script)} …");
                RunningChanged?.Invoke(true);
                return;
            }

            StatusChanged?.Invoke($"Gagal menjalankan Python (\"{PythonExe}\" tidak ditemukan). " +
                                  "Pastikan Python terpasang dan ada di PATH.");
        }
    }

    /// <summary>Starts one interpreter; returns the live process, or null if that exe wasn't found.</summary>
    private Process? TryStart(string exe, string script)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = exe,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            WorkingDirectory       = Path.GetDirectoryName(script) ?? "",
        };
        psi.ArgumentList.Add(script);   // ArgumentList quotes paths with spaces correctly

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) Output?.Invoke(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) Output?.Invoke(e.Data); };
        proc.Exited += (_, _) =>
        {
            RunningChanged?.Invoke(false);
            StatusChanged?.Invoke("Proses Python berhenti.");
        };

        try
        {
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            return proc;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            proc.Dispose();
            return null;   // interpreter name not found — caller falls back to the next one
        }
        catch (Exception ex)
        {
            proc.Dispose();
            StatusChanged?.Invoke($"Gagal menjalankan Python: {ex.Message}");
            return null;
        }
    }

    private void KillProcess()
    {
        lock (_procLock)
        {
            if (_process is null) return;
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
            try { _process.Dispose(); } catch { }
            _process = null;
        }
    }

    private static IEnumerable<string> Dedup(params string[] names)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in names)
            if (!string.IsNullOrWhiteSpace(n) && seen.Add(n.Trim()))
                yield return n.Trim();
    }

    public void Dispose() => KillProcess();
}

/// <summary>
/// Client-side helper that forwards a PID process command (run / stop / sync) to a
/// <see cref="ShareServer"/>'s <see cref="ShareProtocol.PidRunPath"/> endpoint, presenting
/// the session token as the Bearer credential. Mirrors <see cref="TaskClient"/> /
/// <see cref="ControlEngineering.PidDesignClient"/>.
/// </summary>
public static class PidRunClient
{
    /// <summary>POSTs one command; returns true on a 2xx response, false on any error.</summary>
    public static async Task<bool> PostAsync(
        string host, string token, string action, double kp, double ki, double kd, double sp)
    {
        if (string.IsNullOrWhiteSpace(AuthClient.NormalizeHost(host)) || string.IsNullOrWhiteSpace(token))
            return false;

        try
        {
            var body = new JsonObject
            {
                ["action"] = action,
                ["kp"] = kp,
                ["ki"] = ki,
                ["kd"] = kd,
                ["sp"] = sp,
            };

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var req  = new HttpRequestMessage(HttpMethod.Post, $"{AuthClient.BaseUrl(host)}{ShareProtocol.PidRunPath}")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

            using var resp = await http.SendAsync(req);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }
}
