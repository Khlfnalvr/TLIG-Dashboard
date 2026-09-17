using System.Globalization;
using TLIGDashboard.Models;
using TLIGDashboard.Services.ControlEngineering;

namespace TLIGDashboard.Services;

/// <summary>
/// Perekam percobaan plant HE di sisi <b>Server</b>: selama satu run berjalan ia
/// mengumpulkan kurva respons yang dikirim LabVIEW, lalu menyimpannya ke cache
/// parameter (<see cref="HeParameterCacheRepository"/>) begitu run selesai —
/// sehingga kombinasi SP/Kc/Ti/Td/Pump yang sama tidak perlu dijalankan lagi.
///
/// Hanya Server yang merekam, dan itu memang satu-satunya yang bisa: data
/// LabVIEW masuk ke <see cref="HmiDataService"/> miliknya (TCP 6001), dan
/// databasenya juga ada di sana. Run yang dimulai dari Client tetap terekam,
/// karena perintahnya lewat <c>/sim/pid/run</c> yang dijalankan Server.
///
/// <para><b>Kanal.</b> Model cache menyediakan tujuh kanal; VI yang dipakai
/// sekarang mengirim empat nama saja ("Flow Tube", "PV", "Flow Shell",
/// "Temp. Shell out" — lihat <c>HmiDataService.DataLineFields</c>). Nama-nama
/// lain tetap dikenali kalau suatu saat VI mulai mengirimnya; yang tidak ada
/// disimpan sebagai NULL, bukan 0, supaya "tidak diukur" tidak tertukar dengan
/// "terukur nol".</para>
/// </summary>
public sealed class HeRunRecorder
{
    public static HeRunRecorder Instance { get; } = new();
    private HeRunRecorder() { }

    /// <summary>
    /// Batas jumlah sampel satu run. Pada ~1 paket/detik ini setara belasan jam,
    /// jadi praktikum normal tidak akan menyentuhnya; gunanya menahan run yang
    /// tertinggal hidup agar tidak menghabiskan memori.
    /// </summary>
    private const int MaxSamples = 20_000;

    /// <summary>
    /// Sedikitnya sekian sampel sebelum sebuah run boleh dianggap
    /// <see cref="HeParameterRunStatus.Completed"/>. Run yang lebih pendek dari
    /// ini (LabVIEW tidak mengirim apa-apa, atau ditekan STOP seketika) tetap
    /// disimpan sebagai catatan tapi berstatus Failed, jadi tidak pernah
    /// disodorkan sebagai hasil cache — kurva kosong akan lebih berbahaya
    /// daripada tidak punya cache sama sekali.
    /// </summary>
    private const int MinSamplesForCompleted = 5;

    private readonly object _gate = new();

    private List<HeParameterRunSample>? _samples;
    private HeParameterInput?           _input;
    private string?                     _userId;
    private string?                     _userName;
    private DateTime                    _startedUtc;
    private double                      _lastT = -1;
    private bool                        _subscribed;

    /// <summary>Sedang merekam satu run.</summary>
    public bool IsRecording { get { lock (_gate) return _samples is not null; } }

    /// <summary>Parameter run yang sedang direkam, atau <c>null</c> kalau tidak sedang merekam.</summary>
    public HeParameterInput? Input { get { lock (_gate) return _input; } }

    /// <summary>
    /// Pemilik run yang sedang direkam (username), atau <c>null</c> kalau tidak
    /// sedang merekam. Dipakai kolom kehadiran untuk membedakan "Simulasi
    /// berjalan" dari sekadar "memegang giliran".
    /// </summary>
    public string? RecordingUserId { get { lock (_gate) return _samples is null ? null : _userId; } }

    /// <summary>
    /// Mulai merekam. Kalau masih ada run sebelumnya yang belum ditutup, run itu
    /// ditutup dulu sebagai <see cref="HeParameterRunStatus.Aborted"/> — tetap
    /// tersimpan sebagai catatan, tapi tidak akan dipakai ulang.
    /// </summary>
    public void Start(HeParameterInput input, string? userId, string? displayName)
    {
        if (!BuildInfo.IsServer) return;

        _ = FinishAsync(HeParameterRunStatus.Aborted, "Ditimpa run berikutnya");

        lock (_gate)
        {
            _samples    = new List<HeParameterRunSample>(512);
            _input      = input;
            _userId     = userId;
            _userName   = displayName;
            _startedUtc = DateTime.UtcNow;
            _lastT      = -1;

            if (!_subscribed)
            {
                HmiDataService.Instance.DataReceived += OnData;
                _subscribed = true;
            }
        }
    }

    /// <summary>
    /// Menutup run yang sedang berjalan dan menyimpannya. Aman dipanggil walau
    /// tidak sedang merekam (tidak terjadi apa-apa), dan aman dipanggil dua kali:
    /// yang kedua sudah tidak menemukan run terbuka.
    /// </summary>
    /// <param name="expectedUserId">
    /// Kalau diisi, run hanya ditutup bila memang milik pengguna itu. Dipakai jalur
    /// pelepasan giliran (<see cref="HeRigRelease"/>): antara membaca siapa pemegang
    /// kendali dan benar-benar melepasnya, giliran bisa berpindah tangan — tanpa
    /// penjagaan ini, percobaan milik orang berikutnya yang justru ikut ditutup.
    /// </param>
    public async Task FinishAsync(HeParameterRunStatus status, string? note = null,
                                  string? expectedUserId = null)
    {
        List<HeParameterRunSample> samples;
        HeParameterInput input;
        string? userId, userName;
        DateTime startedUtc;

        lock (_gate)
        {
            if (_samples is null || _input is null) return;
            if (expectedUserId is not null &&
                !string.Equals(_userId, expectedUserId, StringComparison.Ordinal)) return;

            samples    = _samples;
            input      = _input;
            userId     = _userId;
            userName   = _userName;
            startedUtc = _startedUtc;

            _samples = null;
            _input   = null;

            if (_subscribed)
            {
                HmiDataService.Instance.DataReceived -= OnData;
                _subscribed = false;
            }
        }

        var finishedUtc = DateTime.UtcNow;

        // Sebuah run tanpa kurva tidak boleh masuk cache sebagai hasil yang sah:
        // berikutnya orang akan disodori "hasil" yang isinya kosong.
        if (status == HeParameterRunStatus.Completed && samples.Count < MinSamplesForCompleted)
        {
            status = HeParameterRunStatus.Failed;
            note   = note is null
                ? $"Hanya {samples.Count} sampel diterima dari LabVIEW"
                : $"{note} — hanya {samples.Count} sampel diterima dari LabVIEW";
        }

        var run = new HeParameterRun
        {
            Input             = input,
            Source            = HeParameterRunSource.Plant,
            Status            = status,
            RequestedByUserId = userId,
            RequestedByName   = userName,
            StartedAtUtc      = startedUtc,
            FinishedAtUtc     = finishedUtc,
            DurationSeconds   = (finishedUtc - startedUtc).TotalSeconds,
            Note              = note,
            Metrics           = ComputeMetrics(samples, input.Sp),
        };
        run.Samples.AddRange(samples);

        try { await App.HeParamCache.SaveRunAsync(run); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"HE run save failed: {ex}"); }
    }

    // ── Pengumpulan sampel ──────────────────────────────────────────────────

    private void OnData(IReadOnlyList<HmiDatum> data)
    {
        lock (_gate)
        {
            if (_samples is null || _input is null) return;
            if (_samples.Count >= MaxSamples) return;

            // Dibulatkan ke milidetik: kolom t_seconds ikut jadi primary key, jadi
            // dua paket yang datang dalam milidetik yang sama harus dibuang salah
            // satunya di sini — kalau tidak, penyimpanannya yang akan gagal.
            double t = Math.Round((DateTime.UtcNow - _startedUtc).TotalSeconds, 3);
            if (t <= _lastT) return;
            _lastT = t;

            _samples.Add(new HeParameterRunSample
            {
                TSeconds      = t,
                FlowTube      = Find(data, "Flow Tube"),
                FlowShell     = Find(data, "Flow Shell"),
                SignalMa      = Find(data, "Sinyal mA", "Signal mA"),
                SignalPercent = Find(data, "Sinyal %", "Signal %"),
                PvShellIn     = Find(data, "PV Shell in", "Temp. Shell in"),
                // Set point tidak dikirim balik VI; yang dipakai adalah nilai yang
                // memang sedang diperintahkan, supaya kurvanya bisa dibaca sendiri.
                SetPoint      = _input.Sp,
                PvShellOut    = Find(data, "Temp. Shell out", "PV Shell out", "PV"),
            });
        }
    }

    private static double? Find(IReadOnlyList<HmiDatum> data, params string[] keys)
    {
        foreach (var key in keys)
            foreach (var d in data)
                if (string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase) &&
                    TryParseValue(d.Value, out double value))
                    return value;
        return null;
    }

    /// <summary>
    /// Aturan baca angka yang sama dengan <c>ProcessErrorService</c>: titik sebagai
    /// desimal, dan koma diterima hanya kalau jelas dipakai sebagai desimal.
    /// </summary>
    private static bool TryParseValue(string? raw, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        string s = raw.Trim();
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return true;

        return s.IndexOf(',') >= 0 && s.IndexOf('.') < 0 &&
               double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    // ── Metrik ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Membaca metrik dari kurva yang benar-benar terukur. Rumusnya persis yang
    /// dipakai simulator (<see cref="PidSimulator.ComputeStepMetrics"/> dan
    /// <see cref="PidSimulator.ComputePerformanceIndices"/>), jadi angka plant dan
    /// angka simulasi memang bisa disandingkan.
    ///
    /// Kanal yang dipakai sebagai process variable adalah suhu keluaran shell —
    /// variabel yang dikendalikan loop luar cascade (Gp1).
    /// </summary>
    private static HeParameterRunMetrics? ComputeMetrics(List<HeParameterRunSample> samples, double setpoint)
    {
        if (samples.Count == 0) return null;

        var last = samples[^1];
        var metrics = new HeParameterRunMetrics
        {
            FinalPvShellOut    = last.PvShellOut,
            FinalPvShellIn     = last.PvShellIn,
            FinalFlowTube      = last.FlowTube,
            FinalFlowShell     = last.FlowShell,
            FinalSignalPercent = last.SignalPercent,
        };

        // Hanya titik yang PV-nya benar-benar terukur yang boleh masuk hitungan.
        var time = new List<double>(samples.Count);
        var pv   = new List<double>(samples.Count);
        foreach (var s in samples)
            if (s.PvShellOut is { } value) { time.Add(s.TSeconds); pv.Add(value); }

        if (pv.Count < 2 || Math.Abs(setpoint) < 1e-9) return metrics;

        var t = time.ToArray();
        var y = pv.ToArray();

        var (rise, overshootPct, settling, _) = PidSimulator.ComputeStepMetrics(t, y, setpoint);
        var (iae, ise, itae)                  = PidSimulator.ComputePerformanceIndices(t, y, setpoint);

        metrics.RiseTimeSeconds     = rise;
        metrics.SettlingTimeSeconds = settling;
        metrics.OvershootPercent    = overshootPct;
        metrics.PeakValue           = y.Max();
        // Selisih akhir dalam satuan aslinya (°C) — yang dibaca mahasiswa di
        // laporan, bukan persentasenya.
        metrics.SteadyStateError    = Math.Abs(setpoint - y[^1]);
        metrics.Iae                 = iae;
        metrics.Ise                 = ise;
        metrics.Itae                = itae;

        return metrics;
    }
}
