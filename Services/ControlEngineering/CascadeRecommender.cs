using System;
using System.Collections.Generic;
using System.Linq;
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
    // Outer temperature-PID grid, bracketing the SIMC default (Kp=1.53, Ki=0.015, Kd=8) for the
    // identified Gp1. Tidy values read well on the recommendation card.
    private static readonly double[] KpGrid = { 0.5, 0.8, 1.0, 1.5, 2.0, 3.0, 4.0 };
    private static readonly double[] KiGrid = { 0.005, 0.01, 0.015, 0.02, 0.03, 0.05 };
    private static readonly double[] KdGrid = { 0, 2, 4, 8, 12, 20 };

    // The known-good inner flow PI (SIMC for Gp2) the recommendation pairs with every outer.
    private const float InnerKpFixed = 0.036f, InnerKiFixed = 0.10f;

    // Fixed, bounded search run: long enough that any tuning that would count as ideal (settling
    // < 400 s) is fully settled and measured exactly; slower candidates are rejected on their
    // metrics regardless. Coarse dt keeps the 252-point sweep quick.
    private const double SearchDuration = 1500.0;
    private const double SearchDt = 0.1;

    // CascadeSimulator holds no mutable state, so one shared instance is safe to reuse.
    private static readonly CascadeSimulator _sim = new();

    // The whole evaluated grid (every candidate + its simulated metrics) cached per setpoint, so the
    // canonical pick and any number of target queries all filter the same data without re-simulating.
    // The grid is mildly setpoint-dependent (valve/flow saturation makes the cascade non-linear).
    private static readonly object _lock = new();
    private static readonly Dictionary<int, List<(CascadeRecommendation gains, CascadeMetrics metrics)>> _gridCache = new();

    /// <summary>
    /// Gains to recommend for a temperature response with the given metrics, or null when the current
    /// tuning already meets every criterion (nothing to fix) or the search found nothing feasible.
    /// </summary>
    public static (CascadeRecommendation gains, CascadeMetrics metrics)? Recommend(
        CascadeMetrics currentMetrics, bool currentStable, float setpoint)
    {
        if (PidDiagnosisCalculator.Evaluate(currentMetrics.PrimaryStepMetrics(), currentStable) == PidDiagnosisCode.Ideal)
            return null;

        (CascadeRecommendation gains, CascadeMetrics metrics)? best = null;
        double bestCost = double.PositiveInfinity;
        foreach (var c in EvaluateGrid(setpoint))
        {
            // Keep only tunings the diagnosis itself would call ideal — same thresholds, so the
            // recommendation and the diagnosis can never disagree.
            if (PidDiagnosisCalculator.Evaluate(c.metrics.PrimaryStepMetrics(), c.metrics.Stable) != PidDiagnosisCode.Ideal)
                continue;
            double cost = Cost(c.metrics);
            if (cost < bestCost) { bestCost = cost; best = c; }
        }
        return best;
    }

    /// <summary>
    /// Best verified tuning meeting <paramref name="target"/> (all specified constraints, and stable).
    /// If none of the grid meets it, returns the closest stable tuning with <c>MetTarget = false</c> so
    /// the caller can be honest ("no tuning meets that; the closest is…"). Never invents numbers.
    /// </summary>
    public static TargetTuningResult RecommendForTarget(float setpoint, TuningTarget target)
    {
        var grid = EvaluateGrid(setpoint);

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
    /// Simulates one OUTER-gain set (inner PI fixed at SIMC) and returns its temperature step
    /// metrics — the ground truth for verifying gains a student or the AI proposes.
    /// </summary>
    public static CascadeMetrics SimulateOuter(float setpoint, float kp, float ki, float kd)
    {
        float sp = Math.Max(1f, setpoint);
        var input = new CascadeInput
        {
            OuterKp = kp, OuterKi = ki, OuterKd = kd,
            InnerKp = InnerKpFixed, InnerKi = InnerKiFixed,
            Setpoint = sp, Disturbance = 0f,
        };
        var sim = _sim.Simulate(input, duration: SearchDuration, dt: SearchDt, withComparison: false);
        var (rise, os, settle, ssePct) = PidSimulator.ComputeStepMetrics(sim.Time, sim.Temperature, sp);
        bool stable = PidSimulator.IsResponseStable(sim.Temperature, sp);
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

    // Simulates every grid point once and caches the result per setpoint.
    private static List<(CascadeRecommendation gains, CascadeMetrics metrics)> EvaluateGrid(float setpoint)
    {
        int key = (int)Math.Round(Math.Max(1f, setpoint));
        lock (_lock)
        {
            if (_gridCache.TryGetValue(key, out var cached)) return cached;

            var list = new List<(CascadeRecommendation, CascadeMetrics)>(KpGrid.Length * KiGrid.Length * KdGrid.Length);
            foreach (double kp in KpGrid)
            foreach (double ki in KiGrid)
            foreach (double kd in KdGrid)
            {
                var input = new CascadeInput
                {
                    OuterKp = (float)kp, OuterKi = (float)ki, OuterKd = (float)kd,
                    InnerKp = InnerKpFixed, InnerKi = InnerKiFixed,
                    Setpoint = key, Disturbance = 0f,
                };
                var sim = _sim.Simulate(input, duration: SearchDuration, dt: SearchDt, withComparison: false);
                var (rise, os, settle, ssePct) = PidSimulator.ComputeStepMetrics(sim.Time, sim.Temperature, key);
                bool stable = PidSimulator.IsResponseStable(sim.Temperature, key);

                list.Add((
                    new CascadeRecommendation
                    {
                        OuterKp = (float)kp, OuterKi = (float)ki, OuterKd = (float)kd,
                        InnerKp = InnerKpFixed, InnerKi = InnerKiFixed,
                    },
                    new CascadeMetrics
                    {
                        Overshoot = (float)os, RiseTime = (float)rise, SettlingTime = (float)settle,
                        SteadyStateError = (float)(ssePct / 100.0), Stable = stable,
                    }));
            }
            _gridCache[key] = list;
            return list;
        }
    }
}
