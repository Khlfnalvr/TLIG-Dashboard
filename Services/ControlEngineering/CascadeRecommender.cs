using System;
using System.Collections.Generic;
using TLIGDashboard.Models.ControlEngineering;

namespace TLIGDashboard.Services.ControlEngineering;

/// <summary>
/// Recommends replacement cascade gains by <b>searching the RK4 simulator</b> — not by asking an
/// LLM to guess. It sweeps a grid of OUTER temperature-PID gains (the loop the student actually
/// tunes), pairs each with the SIMC inner flow PI, simulates the full cascade with the same
/// <see cref="CascadeSimulator"/> the metric cards use, keeps only tunings that pass the exact
/// acceptance criteria <see cref="PidDiagnosisCalculator"/> uses, and returns one canonical pick.
/// Every candidate is verified against the same metrics the diagnosis reads, so the recommendation
/// can never contradict the diagnosis — the failure mode of the old LLM advisor, whose five
/// numbers were never simulated before being offered. Mirrors <see cref="PidRecommender"/>.
///
/// <para>Only the outer loop is searched: cascade is tuned inner-first, the inner flow PI is
/// already SIMC-optimal for the fast delay-dominated flow plant, and the diagnosis judges the
/// temperature (outer) response — so the recommendation resets the inner loop to its known-good
/// values and finds the outer gains that make the temperature response ideal around it.</para>
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

    // Fixed, bounded search run: long enough that any tuning the diagnosis would call ideal
    // (settling < 400 s) is fully settled and measured exactly; slower candidates are rejected on
    // their metrics regardless. Coarse dt keeps the 252-point sweep quick.
    private const double SearchDuration = 1500.0;
    private const double SearchDt = 0.1;

    // CascadeSimulator holds no mutable state, so one shared instance is safe to reuse.
    private static readonly CascadeSimulator _sim = new();

    // The canonical pick is mildly setpoint-dependent (the valve/flow-setpoint saturation makes
    // the cascade non-linear, unlike the linear single-loop plant), so cache per setpoint.
    private static readonly object _lock = new();
    private static readonly Dictionary<int, (CascadeRecommendation gains, CascadeMetrics metrics)?> _cache = new();

    /// <summary>
    /// Gains to recommend for a temperature response with the given metrics, or null when the
    /// current tuning already meets every criterion (nothing to fix — don't nudge a student off a
    /// working tuning) or, defensively, when the search found nothing feasible.
    /// </summary>
    public static (CascadeRecommendation gains, CascadeMetrics metrics)? Recommend(
        CascadeMetrics currentMetrics, bool currentStable, float setpoint)
    {
        if (PidDiagnosisCalculator.Evaluate(currentMetrics.PrimaryStepMetrics(), currentStable) == PidDiagnosisCode.Ideal)
            return null;

        int key = (int)Math.Round(Math.Max(1f, setpoint));
        lock (_lock)
        {
            if (!_cache.TryGetValue(key, out var canonical))
            {
                canonical = ComputeCanonical(key);
                _cache[key] = canonical;
            }
            return canonical;
        }
    }

    private static (CascadeRecommendation, CascadeMetrics)? ComputeCanonical(float setpoint)
    {
        (CascadeRecommendation gains, CascadeMetrics metrics)? best = null;
        double bestCost = double.PositiveInfinity;

        foreach (double kp in KpGrid)
        foreach (double ki in KiGrid)
        foreach (double kd in KdGrid)
        {
            var input = new CascadeInput
            {
                OuterKp = (float)kp, OuterKi = (float)ki, OuterKd = (float)kd,
                InnerKp = InnerKpFixed, InnerKi = InnerKiFixed,
                Setpoint = setpoint, Disturbance = 0f,
            };
            var sim = _sim.Simulate(input, duration: SearchDuration, dt: SearchDt, withComparison: false);
            var (rise, os, settle, ssePct) = PidSimulator.ComputeStepMetrics(sim.Time, sim.Temperature, setpoint);
            bool stable = PidSimulator.IsResponseStable(sim.Temperature, setpoint);

            var metrics = new CascadeMetrics
            {
                Overshoot = (float)os, RiseTime = (float)rise, SettlingTime = (float)settle,
                SteadyStateError = (float)(ssePct / 100.0), Stable = stable,
            };

            // Keep only tunings the diagnosis itself would call ideal — same thresholds, so the
            // recommendation and the diagnosis can never disagree.
            if (PidDiagnosisCalculator.Evaluate(metrics.PrimaryStepMetrics(), stable) != PidDiagnosisCode.Ideal)
                continue;

            // Canonical rule (mirrors PidRecommender): prefer a crisp, well-damped response —
            // fast settling with low overshoot. Weighting overshoot keeps the pick from drifting
            // toward an aggressive tuning that merely squeaks under the 20% overshoot limit.
            double cost = settle + 20.0 * os;
            if (cost < bestCost)
            {
                bestCost = cost;
                best = (new CascadeRecommendation
                {
                    OuterKp = (float)kp, OuterKi = (float)ki, OuterKd = (float)kd,
                    InnerKp = InnerKpFixed, InnerKi = InnerKiFixed,
                }, metrics);
            }
        }

        return best;
    }
}
