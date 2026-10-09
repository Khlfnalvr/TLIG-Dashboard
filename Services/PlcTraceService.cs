using System.Globalization;

namespace TLIGDashboard.Services;

/// <summary>
/// Potret kurva hasil PLC (telemetri LabVIEW) untuk satu run, siap digambar di grafik.
/// Larik <see cref="Time"/>, <see cref="PvShellOut"/>, <see cref="FlowShell"/> dan
/// <see cref="FlowTube"/> selalu sama panjang; <see cref="double.NaN"/> berarti kanal itu
/// tidak terukur pada titik tersebut (bukan nol).
/// </summary>
public sealed class PlcTrace
{
    /// <summary>Penanda run; berganti setiap <see cref="PlcTraceService.Begin"/>.</summary>
    public long   RunId    { get; init; }
    public bool   Running  { get; init; }

    /// <summary><c>"server"</c> atau <c>"client"</c>: dari mana RUN dimulai.</summary>
    public string Origin   { get; init; } = PlcTraceService.OriginServer;

    /// <summary>Username yang memulai run (dipakai Server untuk menjaga siapa boleh melihat).</summary>
    public string? Owner   { get; init; }
    public double Setpoint { get; init; }

    /// <summary>Indeks titik pertama larik ini di dalam run (0 untuk potret penuh).</summary>
    public int    Offset   { get; init; }

    /// <summary>Jumlah titik seluruh run, bukan hanya yang ada di potret ini.</summary>
    public int    Total    { get; init; }

    public double[] Time       { get; init; } = [];
    public double[] PvShellOut { get; init; } = [];
    public double[] FlowShell  { get; init; } = [];
    public double[] FlowTube   { get; init; } = [];

    public int Count => Time.Length;
}

/// <summary>
/// Perekam kurva hasil PLC di sisi <b>Server</b>, untuk digambar di grafik dashboard.
///
/// <para>Hanya Server yang menerimanya: VI mengirim ke listener <see cref="HmiDataService"/>
/// (TCP 6001) milik PC Server. Client tidak punya sumbernya sendiri, jadi ia membaca
/// salinan Server lewat <c>GET /hmi/trace</c> (lihat <see cref="PlcTraceFeed"/>).</para>
///
/// <para>Berbeda dari <see cref="HeRunRecorder"/>: perekam itu menyimpan run ke database lalu
/// membuang sampelnya, sedangkan layanan ini hanya menjaga kurva run terakhir tetap ada
/// di memori supaya grafik tidak kosong begitu STOP ditekan.</para>
///
/// <para><b>Asal run.</b> Yang menentukan siapa melihat grafik: run yang dimulai dari Server
/// hanya tampil di Server, run yang dimulai dari Client tampil di Server <i>dan</i> di Client
/// itu. Aturannya ditegakkan endpoint Server, bukan layar Client.</para>
/// </summary>
public sealed class PlcTraceService
{
    public static PlcTraceService Instance { get; } = new();
    private PlcTraceService() { }

    public const string OriginServer = "server";
    public const string OriginClient = "client";

    /// <summary>Menahan run yang tertinggal hidup agar tidak menghabiskan memori.</summary>
    private const int MaxPoints = 20_000;

    /// <summary>Paket yang datang lebih rapat dari ini dibuang satu; ~1 paket/detik sudah cukup untuk grafik.</summary>
    private const double MinSpacingSeconds = 0.2;

    private readonly object _gate = new();
    private readonly List<double> _t  = [];
    private readonly List<double> _pv = [];
    private readonly List<double> _fs = [];
    private readonly List<double> _ft = [];

    private long     _runId;
    private bool     _running;
    private string   _origin = OriginServer;
    private string?  _owner;
    private double   _setpoint;
    private DateTime _startUtc;
    private bool     _subscribed;

    /// <summary>Dipanggil dari thread latar setiap ada titik baru atau status run berubah.</summary>
    public event Action? Updated;

    /// <summary>Belum pernah ada run sejak aplikasi dibuka.</summary>
    public bool HasRun { get { lock (_gate) return _runId != 0; } }

    /// <summary>
    /// Mulai run baru: kurva lama dibuang. Dipanggil tepat sebelum perintah RUN berangkat
    /// ke LabVIEW, supaya paket pertamanya sudah ikut tercatat.
    /// </summary>
    public void Begin(string origin, string? owner, double setpoint)
    {
        if (!BuildInfo.IsServer) return;

        lock (_gate)
        {
            _runId++;
            _running  = true;
            _origin   = origin;
            _owner    = owner;
            _setpoint = setpoint;
            _startUtc = DateTime.UtcNow;
            _t.Clear(); _pv.Clear(); _fs.Clear(); _ft.Clear();

            if (!_subscribed)
            {
                HmiDataService.Instance.DataReceived += OnData;
                _subscribed = true;
            }
        }
        RaiseUpdated();
    }

    /// <summary>Menutup run. Kurvanya tetap disimpan sampai <see cref="Begin"/> berikutnya.</summary>
    public void End()
    {
        lock (_gate)
        {
            if (!_running) return;
            _running = false;
        }
        RaiseUpdated();
    }

    /// <summary>
    /// Potret run terakhir mulai dari titik ke-<paramref name="from"/>. Kalau
    /// <paramref name="runId"/> bukan run yang sedang tersimpan, potretnya dimulai dari 0.
    /// <c>null</c> kalau belum pernah ada run.
    /// </summary>
    public PlcTrace? Read(long runId, int from)
    {
        lock (_gate)
        {
            if (_runId == 0) return null;

            int offset = runId == _runId ? Math.Clamp(from, 0, _t.Count) : 0;
            return new PlcTrace
            {
                RunId      = _runId,
                Running    = _running,
                Origin     = _origin,
                Owner      = _owner,
                Setpoint   = _setpoint,
                Offset     = offset,
                Total      = _t.Count,
                Time       = _t.GetRange(offset, _t.Count - offset).ToArray(),
                PvShellOut = _pv.GetRange(offset, _pv.Count - offset).ToArray(),
                FlowShell  = _fs.GetRange(offset, _fs.Count - offset).ToArray(),
                FlowTube   = _ft.GetRange(offset, _ft.Count - offset).ToArray(),
            };
        }
    }

    /// <summary>Seluruh run terakhir (<see cref="Read"/> dari awal).</summary>
    public PlcTrace? Snapshot() => Read(-1, 0);

    // ── Pengumpulan titik ───────────────────────────────────────────────────

    private void OnData(IReadOnlyList<HmiDatum> data)
    {
        lock (_gate)
        {
            if (!_running) return;
            if (_t.Count >= MaxPoints) return;

            double pv = Find(data, "Temp. Shell out", "PV Shell out", "PV");
            double fs = Find(data, "Flow Shell");
            double ft = Find(data, "Flow Tube");
            if (double.IsNaN(pv) && double.IsNaN(fs) && double.IsNaN(ft)) return;

            double t = Math.Round((DateTime.UtcNow - _startUtc).TotalSeconds, 3);
            if (_t.Count > 0 && t - _t[^1] < MinSpacingSeconds) return;

            _t.Add(t); _pv.Add(pv); _fs.Add(fs); _ft.Add(ft);
        }
        RaiseUpdated();
    }

    private void RaiseUpdated()
    {
        try { Updated?.Invoke(); } catch { }
    }

    private static double Find(IReadOnlyList<HmiDatum> data, params string[] keys)
    {
        foreach (var key in keys)
            foreach (var d in data)
                if (string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase) &&
                    TryParseValue(d.Value, out double value))
                    return value;
        return double.NaN;
    }

    /// <summary>Aturan baca angka yang sama dengan <see cref="HeRunRecorder"/>: titik atau koma desimal.</summary>
    private static bool TryParseValue(string? raw, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        string s = raw.Trim();
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return double.IsFinite(value);

        return s.IndexOf(',') >= 0 && s.IndexOf('.') < 0 &&
               double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
               double.IsFinite(value);
    }
}
