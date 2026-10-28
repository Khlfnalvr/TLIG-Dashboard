using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TLIGDashboard.Models.ControlEngineering;

namespace TLIGDashboard.Services.ControlEngineering;

/// <summary>A performance target to search gains against — any subset of these can be set.</summary>
public sealed class TuningTarget
{
    public float? MaxOvershoot           { get; set; }   // %
    public float? MaxSettlingTime        { get; set; }   // s
    public float? MaxSteadyStateErrorPct { get; set; }   // %
    public bool Any => MaxOvershoot.HasValue || MaxSettlingTime.HasValue || MaxSteadyStateErrorPct.HasValue;
}

/// <summary>Outcome of a target-constrained search.</summary>
public sealed class TargetTuningResult
{
    /// <summary>Verified gains — meeting the target when <see cref="MetTarget"/> is true, otherwise the closest.</summary>
    public CascadeRecommendation? Gains   { get; set; }
    /// <summary>The recommended gains' measured temperature response.</summary>
    public CascadeMetrics?         Metrics { get; set; }
    /// <summary>True if a tuning meeting every requested constraint was found; false if only the closest could be returned.</summary>
    public bool MetTarget { get; set; }
    public bool HasGains  => Gains is not null && Metrics is not null;
}

/// <summary>
/// Recommends cascade gains by <b>searching the RK4 simulator</b> — not by asking an LLM to guess.
/// It sweeps a grid of OUTER temperature-PID gains (the loop the student tunes), pairs each with the
/// SIMC inner flow PI, simulates the full cascade with the same <see cref="CascadeSimulator"/> the
/// metric cards use, and returns verified gains. Every candidate is measured, so a recommendation can
/// never contradict what the plant actually does — the failure mode of letting the LLM invent numbers.
/// Mirrors <see cref="PidRecommender"/>.
///
/// <para>Two entry points share one cached grid evaluation per setpoint:
/// <see cref="Recommend"/> returns the single canonical "ideal" pick (used by the RUN advisor);
/// <see cref="RecommendForTarget"/> returns the best tuning meeting a caller-supplied target
/// (e.g. overshoot ≤ 5 %), used to answer target requests from the AI chat.</para>
///
/// <para>Only the outer loop is searched: cascade is tuned inner-first and the inner flow PI is
/// already SIMC-optimal, so the recommendation resets the inner loop to its known-good values.</para>
/// </summary>
public static class CascadeRecommender
{
    // Outer temperature-PID grid, bracketing the SIMC default (Kp=0.216, Ki=0.00084, Kd=0)
    // for Gp1 at K=10. The identified-K grid (0.8…3.0 / 0.003…0.02 / 0…10) was divided by
    // the same 1.2→10 gain ratio, so every candidate commands the same valve trajectory as
    // before and remains stable. Tidy values read well on the recommendation card.
    private static readonly double[] KpGrid = { 0.1, 0.12, 0.15, 0.18, 0.216, 0.24, 0.3, 0.36 };
    private static readonly double[] KiGrid = { 0.0003, 0.0005, 0.0007, 0.00084, 0.0012, 0.002 };
    private static readonly double[] KdGrid = { 0, 0.2, 0.5, 1.0 };

    // The known-good inner flow PI (SIMC for Gp2 at τc = 1.5θ, K2 = 0.0465 for the
    // 0–4.65 L/min operating range) the recommendation pairs with every outer.
    private const float InnerKpFixed = 0.85f, InnerKiFixed = 0.63f;

    // Fixed, bounded search run: long enough that any tuning that would count as ideal (settling
    // < 400 s) is fully settled and measured exactly; slower candidates are rejected on their
    // metrics regardless. Coarse dt keeps the 192-point sweep quick.
    private const double SearchDuration = 1500.0;
    private const double SearchDt = 0.1;

    // CascadeSimulator holds no mutable state, so one shared instance is safe to reuse.
    private static readonly CascadeSimulator _sim = new();

    // The whole evaluated grid (every candidate + its simulated metrics) cached per temperature
    // RISE (setpoint − inlet) and valve cap, so the canonical pick and any number of target
    // queries all filter the same data without re-simulating. The rise is the only setpoint
    // quantity the dynamics and the valve limits depend on; changing the inlet moves the
    // absolute window, not the grid. The cap ("Bukaan Valve") changes how fast flow can rise,
    // so it is part of the key. Bounded so the cache can't grow without limit.
    private const int MaxCachedGrids = 8;
    private static readonly ConcurrentDictionary<(int rise, int cap), List<(CascadeRecommendation gains, CascadeMetrics metrics, bool violates)>> _gridCache = new();
    private static readonly ConcurrentQueue<(int rise, int cap)> _gridOrder = new();

    /// <summary>
    /// Gains to recommend for a temperature response with the given metrics, or null when the current
    /// tuning already meets every criterion AND respects the operating constraints (nothing to fix)
    /// or the search found nothing feasible inside valve 0–100% / flow 0–4.65 L/min.
    /// A metric-ideal tuning that VIOLATES the constraints still gets a compliant
    /// recommendation — slamming the actuator is not a working tuning.
    /// </summary>
    public static (CascadeRecommendation gains, CascadeMetrics metrics)? Recommend(
        CascadeMetrics currentMetrics, bool currentStable, float setpoint, float inletTemperature,
        bool currentViolates = false, float valveLimit = 100f)
    {
        // Setpoint di luar jangkauan plant tidak mungkin dicapai gain apa pun; jangan
        // buang waktu mensimulasikan seluruh grid (tiap kandidat akan berjalan sampai
        // batas durasi) dan jangan tawarkan tuning yang tetap gagal.
        if (!SetpointReachability.IsCascadeReachable(setpoint, inletTemperature, valveLimit))
            return null;

        if (!currentViolates
            && PidDiagnosisCalculator.Evaluate(currentMetrics.PrimaryStepMetrics(), currentStable) == PidDiagnosisCode.Ideal)
            return null;

        (CascadeRecommendation gains, CascadeMetrics metrics)? best = null;
        double bestCost = double.PositiveInfinity;
        foreach (var c in EvaluateGrid(setpoint, inletTemperature, valveLimit))
        {
            // Constraint operasi dulu: yang meminta valve/flow di luar 0–100% /
            // 0–4,65 L/min langsung ditolak, walau saturasi membuatnya tampak lolos.
            if (c.violates) continue;
            // Keep only tunings the diagnosis itself would call ideal — same thresholds, so the
            // recommendation and the diagnosis can never disagree.
            if (PidDiagnosisCalculator.Evaluate(c.metrics.PrimaryStepMetrics(), c.metrics.Stable) != PidDiagnosisCode.Ideal)
                continue;
            double cost = Cost(c.metrics);
            if (cost < bestCost) { bestCost = cost; best = (c.gains, c.metrics); }
        }
        return best;
    }

    /// <summary>
    /// Best verified tuning meeting <paramref name="target"/> (all specified constraints, stable,
    /// AND inside valve 0–100% / flow 0–4.65 L/min).
    /// If none of the grid meets it, returns the closest stable tuning with <c>MetTarget = false</c> so
    /// the caller can be honest ("no tuning meets that; the closest is…"). Never invents numbers.
    /// Tunings violating the operating constraints are never returned — not even as "closest".
    /// </summary>
    public static TargetTuningResult RecommendForTarget(
        float setpoint, float inletTemperature, TuningTarget target, float valveLimit = 100f)
    {
        // Di luar jangkauan plant tidak ada tuning yang bisa memenuhi target apa pun;
        // jangan jalankan pencarian grid yang mahal.
        if (!SetpointReachability.IsCascadeReachable(setpoint, inletTemperature, valveLimit))
            return new TargetTuningResult { MetTarget = false };

        var grid = EvaluateGrid(setpoint, inletTemperature, valveLimit).Where(c => !c.violates).ToList();

        // Meets every requested constraint (unset constraints don't filter) and is stable.
        var passing = grid.Where(c => c.metrics.Stable && Meets(c.metrics, target)).ToList();
        if (passing.Count > 0)
        {
            // Crispest of the tunings that qualify — fast settling with low overshoot.
            var best = passing.OrderBy(c => Cost(c.metrics)).First();
            return new TargetTuningResult { Gains = best.gains, Metrics = best.metrics, MetTarget = true };
        }

        // Nothing meets it — offer the closest stable tuning (least total normalized overshoot).
        var stable = grid.Where(c => c.metrics.Stable).ToList();
        if (stable.Count == 0) return new TargetTuningResult { MetTarget = false };
        var closest = stable.OrderBy(c => Violation(c.metrics, target)).First();
        return new TargetTuningResult { Gains = closest.gains, Metrics = closest.metrics, MetTarget = false };
    }

    /// <summary>
    /// Simulates one OUTER-gain set (inner PI fixed at SIMC) and returns its temperature
    /// step metrics — the ground truth for verifying gains a student or the AI proposes.
    /// Metrics are read on the deviation (T−T0 vs SP−T0); T0 is the inlet temperature, so the
    /// simulation is run on the rise (setpoint − inlet) with a zero inlet — same dynamics.
    /// </summary>
    public static CascadeMetrics SimulateOuter(
        float setpoint, float inletTemperature, float kp, float ki, float kd, float valveLimit = 100f)
    {
        float sp = Math.Max(1f, setpoint - inletTemperature);
        var input = new CascadeInput
        {
            OuterKp = kp, OuterKi = ki, OuterKd = kd,
            InnerKp = InnerKpFixed, InnerKi = InnerKiFixed,
            Setpoint = sp, InletTemperature = 0f, Disturbance = 0f, ValveLimit = valveLimit,
        };
        var sim = _sim.Simulate(input, duration: SearchDuration, dt: SearchDt, withComparison: false);
        double y0 = sim.Temperature.Length > 0 ? sim.Temperature[0] : 0.0;
        double spDev = sp - y0;
        double[] yDev = new double[sim.Temperature.Length];
        for (int i = 0; i < yDev.Length; i++) yDev[i] = sim.Temperature[i] - y0;
        var (rise, os, settle, ssePct) = PidSimulator.ComputeStepMetrics(sim.Time, yDev, spDev);
        // Fixed-duration search run never sets the adaptive Settled flag — use the
        // bounded-amplitude check on the deviation (as before), plus the constraint.
        bool stable = PidSimulator.IsResponseStable(yDev, spDev)
            && !CascadeSimulator.ViolatesOperatingConstraints(sim);
        return new CascadeMetrics
        {
            Overshoot = (float)os, RiseTime = (float)rise, SettlingTime = (float)settle,
            SteadyStateError = (float)(ssePct / 100.0), Stable = stable,
        };
    }

    /// <summary>The inner flow PI (SIMC) that the recommender pairs with every outer tuning.</summary>
    public static (float kp, float ki) InnerPi => (InnerKpFixed, InnerKiFixed);

    // Crispness cost shared by both picks: fast settling with low overshoot (weighting overshoot
    // keeps the pick from drifting toward an aggressive tuning that merely squeaks under a limit).
    private static double Cost(CascadeMetrics m) => m.SettlingTime + 20.0 * m.Overshoot;

    private static bool Meets(CascadeMetrics m, TuningTarget t) =>
        (!t.MaxOvershoot.HasValue           || m.Overshoot    <= t.MaxOvershoot.Value) &&
        (!t.MaxSettlingTime.HasValue        || m.SettlingTime <= t.MaxSettlingTime.Value) &&
        (!t.MaxSteadyStateErrorPct.HasValue || Math.Abs(m.SteadyStateError) * 100.0 <= t.MaxSteadyStateErrorPct.Value);

    // Sum of per-constraint overshoots, each normalized by the target, so "closest" is comparable
    // across mixed units (%, s).
    private static double Violation(CascadeMetrics m, TuningTarget t)
    {
        double v = 0;
        if (t.MaxOvershoot.HasValue)
            v += Math.Max(0, m.Overshoot - t.MaxOvershoot.Value) / Math.Max(1f, t.MaxOvershoot.Value);
        if (t.MaxSettlingTime.HasValue)
            v += Math.Max(0, m.SettlingTime - t.MaxSettlingTime.Value) / Math.Max(1f, t.MaxSettlingTime.Value);
        if (t.MaxSteadyStateErrorPct.HasValue)
            v += Math.Max(0, Math.Abs(m.SteadyStateError) * 100.0 - t.MaxSteadyStateErrorPct.Value) / Math.Max(0.1f, t.MaxSteadyStateErrorPct.Value);
        return v;
    }

    // Simulates every grid point once and caches the result per setpoint. The sweep runs in
    // parallel (CascadeSimulator keeps no mutable state, so concurrent Simulate calls are safe)
    // and outside any lock, so a first-time search never blocks other readers.
    private static List<(CascadeRecommendation gains, CascadeMetrics metrics, bool violates)> EvaluateGrid(
        float setpoint, float inletTemperature, float valveLimit)
    {
        // Only the rise and the valve cap matter to the dynamics and the limits, so they are the key.
        int rise = (int)Math.Round(Math.Max(1f, setpoint - inletTemperature));
        int cap  = (int)Math.Round(CascadeSimulator.NormalizeValveLimit(valveLimit));
        var key  = (rise, cap);
        if (_gridCache.TryGetValue(key, out var cached)) return cached;

        var combos = new List<(double kp, double ki, double kd)>(KpGrid.Length * KiGrid.Length * KdGrid.Length);
        foreach (double kp in KpGrid)
        foreach (double ki in KiGrid)
        foreach (double kd in KdGrid)
            combos.Add((kp, ki, kd));

        var results = new (CascadeRecommendation gains, CascadeMetrics metrics, bool violates)[combos.Count];
        Parallel.For(0, combos.Count, i =>
        {
            var (kp, ki, kd) = combos[i];
            results[i] = EvaluateCandidate(rise, cap, kp, ki, kd);
        });

        var list = results.ToList();
        _gridCache[key] = list;
        _gridOrder.Enqueue(key);
        while (_gridOrder.Count > MaxCachedGrids && _gridOrder.TryDequeue(out var oldest))
            _gridCache.TryRemove(oldest, out _);
        return list;
    }

    private static (CascadeRecommendation gains, CascadeMetrics metrics, bool violates) EvaluateCandidate(
        int rise, int cap, double kp, double ki, double kd)
    {
        var input = new CascadeInput
        {
            OuterKp = (float)kp, OuterKi = (float)ki, OuterKd = (float)kd,
            InnerKp = InnerKpFixed, InnerKi = InnerKiFixed,
            Setpoint = rise, InletTemperature = 0f, Disturbance = 0f, ValveLimit = cap,
        };
        var sim = _sim.Simulate(input, duration: SearchDuration, dt: SearchDt, withComparison: false);
        bool violates = CascadeSimulator.ViolatesOperatingConstraints(sim);
        // Metrics on deviation (see ComputeMetrics): inlet is 0 here, so T0 ≈ 0.
        double y0 = sim.Temperature.Length > 0 ? sim.Temperature[0] : 0.0;
        double spDev = rise - y0;
        double[] yDev = new double[sim.Temperature.Length];
        for (int i = 0; i < yDev.Length; i++) yDev[i] = sim.Temperature[i] - y0;
        var (riseTime, os, settle, ssePct) = PidSimulator.ComputeStepMetrics(sim.Time, yDev, spDev);
        // Same fixed-duration stability rule as SimulateOuter (see above).
        bool stable = PidSimulator.IsResponseStable(yDev, spDev)
            && !CascadeSimulator.ViolatesOperatingConstraints(sim);

        return (
            new CascadeRecommendation
            {
                OuterKp = (float)kp, OuterKi = (float)ki, OuterKd = (float)kd,
                InnerKp = InnerKpFixed, InnerKi = InnerKiFixed,
            },
            new CascadeMetrics
            {
                Overshoot = (float)os, RiseTime = (float)riseTime, SettlingTime = (float)settle,
                SteadyStateError = (float)(ssePct / 100.0), Stable = stable,
            },
            violates);
    }
}
