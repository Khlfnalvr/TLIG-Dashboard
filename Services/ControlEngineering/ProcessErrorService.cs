using System.Globalization;
using System.Text;

namespace TLIGDashboard.Services.ControlEngineering;

/// <summary>
/// One process variable seen from both sides — the live LabVIEW reading and the value the last
/// System Model run ended at — with the errors between them already computed.
/// </summary>
public sealed class ProcessErrorPair
{
    /// <summary>Which cascade loop this variable belongs to, e.g. "OUTER / suhu (Gp1)".</summary>
    public required string Loop      { get; init; }

    /// <summary>Key exactly as LabVIEW sent it (see <see cref="HmiDataService"/>'s DATA field names).</summary>
    public required string LiveKey   { get; init; }

    public required string Unit      { get; init; }
    public required double Live      { get; init; }

    /// <summary>Value the simulated curve ended the run at.</summary>
    public required double Simulated { get; init; }

    /// <summary>
    /// Setpoint of this loop, or null when it has no fixed one — the inner loop's setpoint is
    /// the outer PID's output, so it moves throughout the run.
    /// </summary>
    public double? Setpoint { get; init; }

    /// <summary>Model mismatch: simulated − live. This is the "error simulasi vs LabVIEW".</summary>
    public double  ModelError        => Simulated - Live;

    /// <summary>|ModelError| as a percentage of the simulated value; null when that value is ~0.</summary>
    public double? ModelErrorPercent => Percent(ModelError, Simulated);

    /// <summary>Tracking error of the REAL plant: setpoint − live. Null when the loop has no fixed setpoint.</summary>
    public double? ControlError        => Setpoint is { } sp ? sp - Live : null;

    /// <summary>|ControlError| as a percentage of the setpoint; null when there is none or it is ~0.</summary>
    public double? ControlErrorPercent => Percent(ControlError, Setpoint);

    /// <summary>
    /// True when the two sides differ by more than an order of magnitude. The percentage is still
    /// correct arithmetic, but a gap that size usually means the VI reports this variable in
    /// another unit or scale rather than the plant being 90%+ off the model — worth saying so
    /// instead of letting the number be read as a deviation.
    /// </summary>
    public bool ScaleSuspect
    {
        get
        {
            double a = Math.Abs(Live), b = Math.Abs(Simulated);
            return a > 1e-9 && b > 1e-9 && (a / b > 10.0 || b / a > 10.0);
        }
    }

    private static double? Percent(double? value, double? reference) =>
        value is { } v && reference is { } r && Math.Abs(r) > 1e-9
            ? Math.Abs(v) / Math.Abs(r) * 100.0
            : null;
}

/// <summary>Both sides of the comparison plus every pair that could be matched between them.</summary>
public sealed class ProcessComparison
{
    /// <summary>Raw live snapshot from <see cref="HmiDataService"/>, in first-seen key order.</summary>
    public IReadOnlyList<HmiDatum> Live { get; init; } = [];

    /// <summary>The last System Model run, or null when nothing has been run yet.</summary>
    public CascadeDesignResult? Simulation { get; init; }

    public IReadOnlyList<ProcessErrorPair> Pairs { get; init; } = [];

    public bool HasLive       => Live.Count > 0;
    public bool HasSimulation => Simulation is not null;
    public bool HasPairs      => Pairs.Count > 0;
}

/// <summary>
/// Puts BOTH sides of the process in front of the AI chat and does the arithmetic between them
/// in C# rather than leaving it to the LLM.
///
/// The chat used to receive only the live LabVIEW snapshot, so a question like "berapa error
/// antara simulasi dan data LabVIEW?" had nothing to compare against and the model guessed the
/// simulated half. This service:
/// <list type="bullet">
/// <item>pairs each live key with the cascade variable it actually corresponds to — the model
///   itself names them: Gp1's output is the shell-outlet temperature, Gp2's is the tube flow
///   (see <see cref="CascadeSimulator"/>);</item>
/// <item>computes the model mismatch (simulasi − live) and the real plant's tracking error
///   (setpoint − live) directly, so the numbers are exact;</item>
/// <item>renders them as a one-shot context block appended to the system prompt, and answers a
///   direct "berapa error…" question from the computed values without involving the LLM.</item>
/// </list>
/// Same principle as <see cref="TuningChat"/>: only ever report numbers that were measured or
/// calculated, never numbers a language model produced token by token.
/// </summary>
public static class ProcessErrorService
{
    // Live keys carrying each loop's process variable, best candidate first. These are the names
    // HmiDataService assigns to the positional "DATA,<v1>,<v2>,..." line the VI writes.
    //
    // The pairing is not a guess: CascadeSimulator documents Gp1 as "primary: temperature (shell
    // out)" and Gp2 as "secondary: flow (tube)", which is exactly what those keys carry. "PV" is
    // kept as a fallback for the primary because a VI may label the controlled variable that way.
    private static readonly string[] PrimaryKeys   = ["Temp. Shell out", "PV"];
    private static readonly string[] SecondaryKeys = ["Flow Tube", "Flow Shell"];

    private const string PrimaryUnit   = "°C";
    private const string SecondaryUnit = "L/min";

    // ── Comparison ────────────────────────────────────────────────────────────

    /// <summary>
    /// Snapshots the live data and the last simulation run and pairs them up. Never throws and
    /// never blocks — both sources are in-memory singletons.
    /// </summary>
    public static ProcessComparison Build()
    {
        var live = HmiDataService.Instance.Snapshot();
        var sim  = CascadeSessionService.Instance.LastResult;

        var pairs = new List<ProcessErrorPair>();
        if (sim is not null && live.Count > 0)
        {
            if (FindLive(live, PrimaryKeys) is { } p && Last(sim.Simulation.Temperature) is { } tSim)
                pairs.Add(new ProcessErrorPair
                {
                    Loop      = "OUTER / suhu (Gp1)",
                    LiveKey   = p.Key,
                    Unit      = PrimaryUnit,
                    Live      = p.Value,
                    Simulated = tSim,
                    Setpoint  = sim.Input.Setpoint,
                });

            if (FindLive(live, SecondaryKeys) is { } s && Last(sim.Simulation.Flow) is { } fSim)
                pairs.Add(new ProcessErrorPair
                {
                    Loop      = "INNER / flow (Gp2)",
                    LiveKey   = s.Key,
                    Unit      = SecondaryUnit,
                    Live      = s.Value,
                    Simulated = fSim,
                    // No Setpoint: the inner loop's is the outer PID's output, a simulated
                    // quantity that moves all run. Calling the gap to it a "control error"
                    // would just restate the model error under a wrong name.
                });
        }

        return new ProcessComparison { Live = live, Simulation = sim, Pairs = pairs };
    }

    // ── System-prompt context ─────────────────────────────────────────────────

    /// <summary>
    /// Renders the live data, the last simulation run and the errors between them as one block to
    /// append to the AI system prompt. Returns "" when there is nothing to say (no live data and
    /// no run yet), so the caller can skip the append entirely.
    ///
    /// Call it on every send: <see cref="AiConfigClient.ApplyActive"/> resets the system prompt
    /// each time, so the block is one-shot — it never accumulates and never enters chat history.
    /// </summary>
    public static string BuildChatContext()
    {
        var c = Build();
        if (!c.HasLive && !c.HasSimulation) return "";

        var sb = new StringBuilder();
        sb.AppendLine("=== KONTEKS PROSES (disusun otomatis oleh dashboard; angka sudah dihitung, pakai apa adanya) ===");

        if (c.HasLive)
        {
            sb.AppendLine();
            sb.AppendLine("A. Data live LabVIEW/HMI (TCP 6001):");
            foreach (var d in c.Live)
                sb.AppendLine($"   - {d.Key} = {d.Value}");
        }

        if (c.Simulation is { } sim)
        {
            var m = sim.Metrics;
            sb.AppendLine();
            sb.AppendLine("B. Simulasi System Model (cascade RK4, run terakhir):");
            sb.AppendLine($"   - Plant: Gp1 (suhu) = {N(CascadeSimulator.K1)} / ({N(CascadeSimulator.Tau1)}s + 1) · e^(-{N(CascadeSimulator.Theta1)}s); " +
                          $"Gp2 (flow) = {N(CascadeSimulator.K2)} / ({N(CascadeSimulator.Tau2)}s + 1) · e^(-{N(CascadeSimulator.Theta2)}s)");
            sb.AppendLine($"   - Gain OUTER (PID suhu): Kp={N(sim.Input.OuterKp)}, Ki={N(sim.Input.OuterKi)}, Kd={N(sim.Input.OuterKd)}");
            sb.AppendLine($"   - Gain INNER (PI flow): Kp={N(sim.Input.InnerKp)}, Ki={N(sim.Input.InnerKi)}");
            sb.AppendLine($"   - Setpoint outer = {N(sim.Input.Setpoint)} {PrimaryUnit}");
            sb.AppendLine($"   - Rise time {N(m.RiseTime)} s | Overshoot {N(m.Overshoot)} % | Settling {N(m.SettlingTime)} s | Steady-state error {m.SteadyStateError.ToString("0.000", CultureInfo.InvariantCulture)}");
            sb.AppendLine($"   - IAE {N(m.IAE)} | ISE {N(m.ISE)} | ITAE {N(m.ITAE)}");
            sb.AppendLine($"   - Stabil: {(m.Stable ? "ya" : "tidak")}");

            if (Last(sim.Simulation.Temperature) is { } tEnd)
                sb.AppendLine($"   - Nilai akhir run: suhu = {N(tEnd)} {PrimaryUnit}" +
                              (Last(sim.Simulation.Flow) is { } fEnd ? $", flow = {N(fEnd)} {SecondaryUnit}" : ""));
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("B. Simulasi System Model: belum ada run. Jangan mengarang angka simulasi — minta pengguna menekan RUN dulu.");
        }

        if (c.HasPairs)
        {
            sb.AppendLine();
            sb.AppendLine("C. Error simulasi vs data LabVIEW (sudah dihitung dashboard):");
            foreach (var p in c.Pairs)
                sb.AppendLine("   " + DescribePair(p).Replace("\n", "\n   "));
            sb.AppendLine();
            sb.AppendLine("Pakai angka di blok C apa adanya kalau pengguna menanyakan error/selisih antara simulasi dan data LabVIEW. " +
                          "Jangan menghitung ulang dan jangan memakai angka lain. Tugasmu menjelaskan artinya, bukan menghitungnya.");
        }
        else if (c.HasLive && c.HasSimulation)
        {
            sb.AppendLine();
            sb.AppendLine("C. Error simulasi vs LabVIEW: tidak bisa dihitung — tidak ada key LabVIEW yang cocok dengan variabel simulasi " +
                          $"(dicari: {string.Join(" / ", PrimaryKeys)} untuk suhu, {string.Join(" / ", SecondaryKeys)} untuk flow).");
        }

        return sb.ToString().TrimEnd();
    }

    // ── Direct answer (bypasses the LLM) ──────────────────────────────────────

    /// <summary>
    /// If <paramref name="message"/> asks for the error/difference between the simulation and the
    /// live LabVIEW data, answers it from the computed comparison. Returns null otherwise so the
    /// normal chat handles the message.
    /// </summary>
    public static string? TryAnswerErrorRequest(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        string m = message.ToLowerInvariant();
        if (!LooksLikeErrorRequest(m)) return null;

        var c = Build();

        if (!c.HasLive)
            return "Belum ada data live dari LabVIEW (TCP 6001) yang masuk, jadi errornya belum bisa dihitung. " +
                   "Jalankan VI-nya dulu supaya dashboard menerima baris `DATA,...`.";

        if (!c.HasSimulation)
            return "Belum ada hasil simulasi System Model untuk dibandingkan. Tekan **RUN** dulu, " +
                   "lalu tanyakan lagi — errornya akan dihitung dari kurva RK4 run tersebut.";

        if (!c.HasPairs)
            return "Data live dan hasil simulasi sama-sama ada, tapi tidak ada key LabVIEW yang cocok dengan variabel simulasi " +
                   $"(dicari **{string.Join("** / **", PrimaryKeys)}** untuk suhu dan **{string.Join("** / **", SecondaryKeys)}** untuk flow). " +
                   $"Key yang diterima saat ini: {string.Join(", ", c.Live.Select(d => d.Key))}.";

        var sb = new StringBuilder();
        sb.AppendLine("**Error simulasi System Model vs data LabVIEW** (dihitung langsung dari kurva RK4 run terakhir dan snapshot TCP 6001):");
        sb.AppendLine();
        foreach (var p in c.Pairs)
        {
            sb.AppendLine($"- {DescribePair(p)}");
        }
        sb.AppendLine();
        sb.Append("Angka di atas dihitung dashboard, bukan hasil perkiraan model bahasa. " +
                  "*Error model* = simulasi − live (seberapa jauh plant asli menyimpang dari model); " +
                  "*error kontrol* = setpoint − live (error yang sedang dilihat controller di plant asli).");
        return sb.ToString();
    }

    // A message qualifies only when it asks about a discrepancy AND points at the live data, so a
    // tuning request like "gain supaya steady-state error < 2%" is left to TuningChat / the LLM.
    private static bool LooksLikeErrorRequest(string m)
    {
        bool discrepancy =
            m.Contains("error") || m.Contains("galat") || m.Contains("selisih") || m.Contains("deviasi") ||
            m.Contains("simpangan") || m.Contains("penyimpangan") || m.Contains("perbedaan") ||
            m.Contains("beda") || m.Contains("akurasi") || m.Contains("mismatch");

        bool compare =
            m.Contains("banding") || m.Contains("versus") || m.Contains(" vs ") || m.Contains("cocok") ||
            m.Contains("sesuai") || m.Contains("selaras");

        bool live =
            m.Contains("labview") || m.Contains("live") || m.Contains("aktual") || m.Contains("real") ||
            m.Contains("nyata") || m.Contains("lapangan") || m.Contains("sensor") || m.Contains("hmi") ||
            m.Contains("plc") || m.Contains("pembacaan") || m.Contains("6001");

        bool sim = m.Contains("simulasi") || m.Contains("system model") || m.Contains("model");

        return live && (discrepancy || (compare && sim));
    }

    // ── Formatting helpers ────────────────────────────────────────────────────

    private static string DescribePair(ProcessErrorPair p)
    {
        var sb = new StringBuilder();
        sb.Append($"**{p.Loop}** — live \"{p.LiveKey}\" = {N(p.Live)} {p.Unit}, simulasi = {N(p.Simulated)} {p.Unit}");
        sb.Append($"\n  → error model (simulasi − live) = {Signed(p.ModelError)} {p.Unit}{PercentSuffix(p.ModelErrorPercent)}");

        if (p.ControlError is { } ce)
            sb.Append($"\n  → error kontrol (setpoint {N(p.Setpoint!.Value)} {p.Unit} − live) = {Signed(ce)} {p.Unit}{PercentSuffix(p.ControlErrorPercent)}");

        if (p.ScaleSuspect)
            sb.Append($"\n  → CATATAN: bedanya lebih dari 10x, jadi jangan sebut ini sebagai penyimpangan plant. " +
                      $"Dua sebab yang lebih mungkin: (a) satuan/skala \"{p.LiveKey}\" di VI tidak sama dengan {p.Unit} " +
                      "yang dipakai simulasi, atau (b) konstanta plant di CascadeSimulator masih hasil identifikasi lama " +
                      "sehingga kurva simulasinya berada di rentang yang berbeda. Sarankan pengguna mengecek keduanya.");

        return sb.ToString();
    }

    private static string PercentSuffix(double? percent) =>
        percent is { } v ? $" ({N(v)} %)" : "";

    private static string Signed(double v) => (v >= 0 ? "+" : "") + N(v);

    /// <summary>Compact invariant-culture number — the AI must not have to guess a decimal separator.</summary>
    private static string N(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return "--";
        double a = Math.Abs(v);
        string fmt = a >= 1000 ? "0.##" : a >= 1 ? "0.###" : "0.######";
        return v.ToString(fmt, CultureInfo.InvariantCulture);
    }

    private static double? Last(double[] values) => values.Length > 0 ? values[^1] : null;

    /// <summary>First candidate key present in the snapshot whose value parses as a number.</summary>
    private static (string Key, double Value)? FindLive(IReadOnlyList<HmiDatum> live, string[] candidates)
    {
        foreach (var candidate in candidates)
            foreach (var d in live)
                if (string.Equals(d.Key, candidate, StringComparison.OrdinalIgnoreCase) &&
                    TryParseValue(d.Value, out double v))
                    return (d.Key, v);
        return null;
    }

    // The VI may run under a locale that writes "44,591052"; accept either separator.
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
}
