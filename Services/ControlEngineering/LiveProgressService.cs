using System.Globalization;
using System.Text;

namespace TLIGDashboard.Services.ControlEngineering;

/// <summary>Status progres run live: sudah jalan berapa lama vs prediksi settling simulasi.</summary>
public sealed class LiveProgress
{
    public bool IsRunning { get; init; }
    public double ElapsedSeconds { get; init; }
    public int SampleCount { get; init; }
    public double Setpoint { get; init; }
    public double? LiveTemp { get; init; }
    public double? PredictedSettling { get; init; }
    public double? ControlError => LiveTemp is { } t ? Setpoint - t : null;
    public double ProgressFraction =>
        PredictedSettling is { } p && p > 1e-9 ? Math.Clamp(ElapsedSeconds / p, 0, 99) : 0;
}

/// <summary>
/// Tracker progres live opsi 1: tanpa CSV, langsung dari aliran TCP 6001.
///
/// <para>Sumber: <c>HeRunRecorder</c> (t0 + sampel, Server saja) untuk elapsed,
/// snapshot <c>HmiDataService</c> untuk suhu terkini, dan metrik run RK4 terakhir
/// (<c>CascadeSessionService.LastResult</c>) sebagai prediksi settling ideal.
/// Kalau VI di-RUN manual tanpa lewat dashboard, t0 tidak diketahui — dilaporkan
/// sebagai manual tanpa elapsed, bukan dikarang.</para>
/// </summary>
public static class LiveProgressService
{
    /// <summary>Potret progres saat ini, atau null kalau tidak ada live maupun simulasi.</summary>
    public static LiveProgress? Build()
    {
        if (!BuildInfo.IsServer) return null;

        var rec = HeRunRecorder.Instance;
        var sim = CascadeSessionService.Instance.LastResult;
        double? predicted = sim is not null && sim.Metrics.SettlingTime > 0
            ? sim.Metrics.SettlingTime : null;

        if (rec.IsRecording)
        {
            double sp = rec.Input?.Sp ?? CascadeSessionService.Instance.Setpoint;
            double? live = rec.LatestPvShellOut ?? ReadLiveTemp();
            return new LiveProgress
            {
                IsRunning = true,
                ElapsedSeconds = rec.ElapsedSeconds,
                SampleCount = rec.SampleCount,
                Setpoint = sp,
                LiveTemp = live,
                PredictedSettling = predicted,
            };
        }

        // Tidak merekam: kalau ada data live berarti VI jalan manual (t0 tak diketahui).
        var manual = ReadLiveTemp();
        if (manual is null && sim is null) return null;
        if (manual is null) return null; // hanya simulasi -> biar blok B yang bicara
        return new LiveProgress
        {
            IsRunning = false,
            ElapsedSeconds = 0,
            SampleCount = 0,
            Setpoint = CascadeSessionService.Instance.Setpoint,
            LiveTemp = manual,
            PredictedSettling = predicted,
        };
    }

    /// <summary>Blok konteks sekali-pakai (blok D) untuk ditempel ke system prompt.</summary>
    public static string BuildChatContext()
    {
        var p = Build();
        if (p is null) return "";

        var sb = new StringBuilder();
        sb.AppendLine("=== PROGRES LIVE (dihitung dashboard dari recorder + kurva RK4 terakhir) ===");
        if (p.IsRunning)
        {
            sb.AppendLine($"Run live BERJALAN: elapsed {Fmt(p.ElapsedSeconds)}s, {p.SampleCount} sampel, " +
                          $"suhu live {(p.LiveTemp is { } t ? Fmt(t) + " C" : "-")}, setpoint {Fmt(p.Setpoint)} C" +
                          (p.ControlError is { } ce ? $", error kontrol {Signed(ce)} C" : "") + ".");
            if (p.PredictedSettling is { } pred)
                sb.AppendLine($"Prediksi settling SIMULASI (ideal): {Fmt(pred)}s -> progres {p.ProgressFraction * 100:0}% ({Verdict(p)}).");
            else
                sb.AppendLine("Belum ada run simulasi -> tidak ada prediksi settling. Minta pengguna tekan RUN simulasi dulu.");
            sb.AppendLine("Aturan: elapsed < prediksi = wajar belum settle, minta tunggu; " +
                          "elapsed > 1.5x prediksi tapi error masih besar = baru sebut mismatch/saturasi.");
        }
        else
        {
            sb.AppendLine($"VI terdeteksi MANUAL (tidak lewat RUN dashboard, t0 tak diketahui): " +
                          $"suhu live {(p.LiveTemp is { } t ? Fmt(t) + " C" : "-")}, setpoint {Fmt(p.Setpoint)} C" +
                          (p.ControlError is { } ce ? $", error kontrol {Signed(ce)} C" : "") + ".");
            if (p.PredictedSettling is { } pred)
                sb.AppendLine($"Prediksi settling SIMULASI (ideal): {Fmt(pred)}s — tanpa t0, jangan hitung progres/sisa waktu.");
            sb.AppendLine("Jangan mengarang elapsed atau sisa waktu untuk run manual.");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Jawaban langsung untuk pertanyaan progres ("sudah berapa lama", "kapan settle").
    /// Null = bukan pertanyaan progres, teruskan ke router berikutnya / LLM.
    /// </summary>
    public static string? TryAnswerProgressRequest(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        if (!LooksLikeProgressRequest(message.ToLowerInvariant())) return null;

        if (!BuildInfo.IsServer)
            return "Progres live hanya bisa dibaca di sisi Server (data TCP 6001 masuk ke sana). " +
                   "Buka dashboard di PC Server lalu tanyakan lagi.";

        var p = Build();
        if (p is null)
            return "Belum ada run live maupun simulasi. Tekan **RUN** simulasi dulu untuk prediksi settling, " +
                   "lalu jalankan plant — progresnya akan terbaca di sini.";

        var sb = new StringBuilder();
        if (p.IsRunning)
        {
            sb.AppendLine("**Progres live plant** (dari recorder + kurva RK4 terakhir):");
            sb.AppendLine();
            sb.AppendLine($"- Berjalan {Fmt(p.ElapsedSeconds)}s ({p.SampleCount} sampel), " +
                          $"suhu {(p.LiveTemp is { } t ? Fmt(t) + " C" : "-")} / setpoint {Fmt(p.Setpoint)} C" +
                          (p.ControlError is { } ce ? $" (error {Signed(ce)} C)" : ""));
            if (p.PredictedSettling is { } pred)
            {
                sb.AppendLine($"- Prediksi settling simulasi (ideal): {Fmt(pred)}s -> progres {p.ProgressFraction * 100:0}%");
                sb.AppendLine($"- {Verdict(p)}");
                sb.AppendLine();
                sb.Append("Simulasi adalah model ideal satu titik operasi; plant asli boleh lebih lambat. " +
                          "Angka real final baru sah setelah STOP (run Completed).");
            }
            else
            {
                sb.AppendLine("- Belum ada run simulasi, jadi belum ada prediksi. Tekan **RUN** simulasi dulu.");
            }
        }
        else
        {
            sb.AppendLine("**VI jalan manual** (tidak lewat RUN dashboard, t0 tidak diketahui):");
            sb.AppendLine();
            sb.AppendLine($"- Suhu live {(p.LiveTemp is { } t ? Fmt(t) + " C" : "-")} / setpoint {Fmt(p.Setpoint)} C" +
                          (p.ControlError is { } ce ? $" (error {Signed(ce)} C)" : ""));
            if (p.PredictedSettling is { } pred)
                sb.AppendLine($"- Prediksi settling simulasi (ideal): {Fmt(pred)}s. Sisa waktu tidak bisa dihitung tanpa t0 — " +
                              "jalankan berikutnya lewat tombol RUN dashboard supaya progres terbaca.");
            else
                sb.AppendLine("- Belum ada run simulasi. Tekan **RUN** simulasi dulu.");
        }
        return sb.ToString();
    }

    private static string Verdict(LiveProgress p)
    {
        if (p.PredictedSettling is not { } pred || pred <= 0) return "Belum ada prediksi.";
        double err = Math.Abs(p.ControlError ?? double.MaxValue);
        if (p.ElapsedSeconds < pred * 0.95)
            return "Wajar BELUM settle — tunggu, jangan ubah gain dulu.";
        if (p.ElapsedSeconds <= pred * 1.5)
            return "Seharusnya SUDAH dekat setpoint — cek trennya masih turun atau stagnan.";
        return err > 2.0
            ? "Sudah LEWAT 1.5x prediksi tapi error masih besar — indikasi mismatch model / saturasi valve / beban."
            : "Sudah lewat prediksi dan error kecil — anggap settle, boleh STOP untuk menyimpan run Completed.";
    }

    private static bool LooksLikeProgressRequest(string m)
    {
        bool progress = m.Contains("sudah") || m.Contains("progress") || m.Contains("progres") ||
                        m.Contains("kapan") || m.Contains("berapa lagi") || m.Contains("tunggu") ||
                        m.Contains("berjalan") || m.Contains("jalan") || m.Contains("sisa") ||
                        m.Contains("belum settle") || m.Contains("belum sampai") || m.Contains("lama");
        bool target = m.Contains("settl") || m.Contains("tunak") || m.Contains("setpoint") ||
                      m.Contains("set point") || m.Contains("sampai") || m.Contains("mencapai") ||
                      m.Contains("menit") || m.Contains("detik") || m.Contains("live") ||
                      m.Contains("run") || m.Contains("plant");
        return progress && target;
    }

    private static double? ReadLiveTemp()
    {
        try
        {
            foreach (var d in HmiDataService.Instance.Snapshot())
                if ((d.Key.Contains("Shell out", StringComparison.OrdinalIgnoreCase) ||
                     d.Key.Equals("PV", StringComparison.OrdinalIgnoreCase)) &&
                    TryParse(d.Value, out double v))
                    return v;
        }
        catch { }
        return null;
    }

    private static bool TryParse(string? raw, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string s = raw.Trim();
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return true;
        return s.IndexOf(',') >= 0 && s.IndexOf('.') < 0 &&
               double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string Fmt(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
    private static string Signed(double v) => (v >= 0 ? "+" : "") + Fmt(v);
}
