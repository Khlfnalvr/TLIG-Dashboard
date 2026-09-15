using System.Globalization;

namespace TLIGDashboard.Models;

/// <summary>
/// Kombinasi parameter yang dikirim ke plant HE lewat LabVIEW. Kelima nilai ini
/// yang menentukan "percobaan yang sama": kalau kelimanya sama, hasilnya boleh
/// diambil dari cache alih-alih menjalankan plant lagi.
/// </summary>
public sealed class HeParameterInput
{
    /// <summary>Set point suhu (°C).</summary>
    public double Sp   { get; init; }
    /// <summary>Gain proporsional (Kc / Kp).</summary>
    public double Kc   { get; init; }
    /// <summary>Waktu integral (Ti).</summary>
    public double Ti   { get; init; }
    /// <summary>Waktu derivatif (Td).</summary>
    public double Td   { get; init; }
    /// <summary>Bukaan valve/pompa (%).</summary>
    public double Pump { get; init; }

    /// <summary>
    /// Jumlah desimal yang dipakai saat mencocokkan dua kombinasi parameter.
    /// Tiga desimal cukup longgar untuk menyerap selisih floating point
    /// (0.1 + 0.2 ≠ 0.3) tapi masih cukup ketat untuk membedakan setelan yang
    /// memang berbeda. Naikkan/turunkan di sini kalau toleransinya perlu diubah —
    /// ingat, mengubahnya membuat run lama tidak lagi cocok dengan kunci baru.
    /// </summary>
    public const int MatchDecimals = 3;

    /// <summary>
    /// Kunci pencocokan cache: kelima nilai dibulatkan lalu digabung, mis.
    /// <c>"60.000|2.500|10.000|0.500|75.000"</c>. Dibandingkan sebagai teks
    /// supaya bisa memakai index biasa dan tidak terjebak perbandingan REAL.
    /// </summary>
    public string ParamKey => string.Join('|',
        Format(Sp), Format(Kc), Format(Ti), Format(Td), Format(Pump));

    private static string Format(double value) =>
        Math.Round(value, MatchDecimals).ToString("F" + MatchDecimals, CultureInfo.InvariantCulture);

    public override string ToString() =>
        $"SP={Sp:0.###} Kc={Kc:0.###} Ti={Ti:0.###} Td={Td:0.###} Pump={Pump:0.###}%";
}

/// <summary>Bagaimana sebuah run berakhir. Hanya run <see cref="Completed"/> yang boleh dipakai ulang.</summary>
public enum HeParameterRunStatus
{
    /// <summary>Selesai wajar sampai akhir — boleh dipakai sebagai hasil cache.</summary>
    Completed,
    /// <summary>Gagal (koneksi LabVIEW putus, error alat) — disimpan sebagai catatan saja.</summary>
    Failed,
    /// <summary>Dihentikan di tengah jalan oleh pengguna.</summary>
    Aborted,
}

/// <summary>Asal data sebuah run.</summary>
public enum HeParameterRunSource
{
    /// <summary>Dijalankan ke plant HE fisik lewat LabVIEW.</summary>
    Plant,
    /// <summary>Hasil simulator di dalam aplikasi.</summary>
    Simulation,
}

/// <summary>
/// Satu titik pada kurva respons. Ketujuh kanal ini mengikuti urutan
/// <c>CHART_FIELDS</c> di <c>PIDtest.py</c> — variabel yang dikirim balik LabVIEW.
/// </summary>
public sealed class HeParameterRunSample
{
    /// <summary>Detik sejak run dimulai (t = 0).</summary>
    public double  TSeconds      { get; init; }
    public double? FlowTube      { get; init; }   // L/min
    public double? FlowShell     { get; init; }   // L/min
    public double? SignalMa      { get; init; }   // mA
    public double? SignalPercent { get; init; }   // %
    public double? PvShellIn     { get; init; }   // °C
    public double? SetPoint      { get; init; }   // °C
    public double? PvShellOut    { get; init; }   // °C
}

/// <summary>Ringkasan kualitas respons satu run — dihitung sekali, lalu ikut tersimpan.</summary>
public sealed class HeParameterRunMetrics
{
    public double? FinalPvShellOut    { get; set; }
    public double? FinalPvShellIn     { get; set; }
    public double? FinalFlowTube      { get; set; }
    public double? FinalFlowShell     { get; set; }
    public double? FinalSignalPercent { get; set; }

    public double? SteadyStateError    { get; set; }
    public double? RiseTimeSeconds     { get; set; }
    public double? SettlingTimeSeconds { get; set; }
    public double? OvershootPercent    { get; set; }
    public double? PeakValue           { get; set; }

    /// <summary>Integral Square Error.</summary>
    public double? Ise  { get; set; }
    /// <summary>Integral Absolute Error.</summary>
    public double? Iae  { get; set; }
    /// <summary>Integral Time-weighted Absolute Error.</summary>
    public double? Itae { get; set; }
}

/// <summary>Satu percobaan lengkap: parameter masukan + metrik + kurva responsnya.</summary>
public sealed class HeParameterRun
{
    public long                 RunId    { get; init; }
    public HeParameterInput     Input    { get; init; } = new();
    public HeParameterRunSource Source   { get; init; } = HeParameterRunSource.Plant;
    public HeParameterRunStatus Status   { get; init; } = HeParameterRunStatus.Completed;

    public string?  RequestedByUserId { get; init; }
    public string?  RequestedByName   { get; init; }
    public DateTime StartedAtUtc      { get; init; } = DateTime.UtcNow;
    public DateTime? FinishedAtUtc    { get; init; }
    public double?  DurationSeconds   { get; init; }
    public string?  Note              { get; init; }

    /// <summary>Berapa kali hasil ini dipakai ulang tanpa menjalankan plant.</summary>
    public int       ReuseCount       { get; init; }
    public DateTime? LastReusedAtUtc  { get; init; }

    public HeParameterRunMetrics?     Metrics { get; set; }
    public List<HeParameterRunSample> Samples { get; init; } = [];

    /// <summary>True kalau run ini datang dari cache, bukan baru saja dijalankan.</summary>
    public bool IsFromCache => ReuseCount > 0;
}

/// <summary>Ringkasan isi cache — untuk panel status/pengaturan di Server.</summary>
public sealed record HeParameterCacheStats(
    int       TotalRuns,
    int       CompletedRuns,
    int       TotalReuses,
    DateTime? LastRunUtc);
