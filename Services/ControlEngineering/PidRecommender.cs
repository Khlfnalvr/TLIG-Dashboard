using System;
using TLIGDashboard.Models.ControlEngineering;

namespace TLIGDashboard.Services.ControlEngineering;

/// <summary>
/// Recommends replacement gains by <b>searching the RK4 simulator</b> — not by asking an LLM
/// or an ML model to guess. It sweeps a grid of Kp/Ki/Kd, simulates each with the same
/// <see cref="PidSimulator"/> the metric cards use, keeps only the tunings that pass the exact
/// acceptance criteria <see cref="PidDiagnosisCalculator"/> uses, and returns one canonical
/// pick. Because every candidate is verified against the same metrics the diagnosis reads, the
/// recommendation can never contradict the diagnosis — the failure mode of the LLM advisor,
/// whose numbers were never simulated before being shown (a student who applied them saw
/// overshoot go <i>up</i>, from 72.7% to 76.6%).
///
/// <para>The plant is linear, so overshoot %, settling time and steady-state error % are all
/// independent of the setpoint (a larger step just scales the whole response). The feasible set
/// and the canonical pick are therefore identical for every setpoint, so the search runs once
/// and is cached.</para>
/// </summary>
public static class PidRecommender
{
    // A grid of tidy values — clean numbers read better on the recommendation card, and a
    // fixed grid keeps the pick reproducible. The ranges bracket the useful region for this
    // plant and stop well short of the gains that merely run to the grid edge in a simulator
    // with no actuator limit.
    private static readonly double[] KpGrid = { 0.2, 0.3, 0.5, 0.8, 1, 1.5, 2, 3, 4, 5, 7, 10 };
    private static readonly double[] KiGrid = { 0.01, 0.02, 0.05, 0.1, 0.2, 0.4, 0.8, 1.5 };
    private static readonly double[] KdGrid = { 0, 0.1, 0.25, 0.5, 1, 2 };

    // Coarser step than the display run (0.01): the winner's metrics are effectively
    // identical, and it keeps the full 576-point sweep fast even though the FOPDT plant
    // (tau ~101 s + 52 s dead time) runs for a few hundred simulated seconds per candidate.
    private const double SearchDt = 0.1;

    // PidSimulator holds no mutable state, so a single shared instance is safe to reuse.
    private static readonly PidSimulator _sim = new();

    /// <summary>The canonical feasible tuning and its measured response, computed once.</summary>
    private static readonly Lazy<(PidPrediction gains, PidMetricsPrediction metrics)?> _canonical
        = new(ComputeCanonical);

    /// <summary>
    /// Gains to recommend for a response with the given metrics, or null when the current
    /// tuning already meets every criterion (nothing to fix — don't nudge a student off a
    /// working tuning) or, defensively, when the search found nothing feasible.
    /// </summary>
    public static (PidPrediction gains, PidMetricsPrediction metrics)? Recommend(
        PidMetricsPrediction currentMetrics, bool currentStable)
    {
        if (PidDiagnosisCalculator.Evaluate(currentMetrics, currentStable) == PidDiagnosisCode.Ideal)
            return null;
        return _canonical.Value;
    }

    private static (PidPrediction, PidMetricsPrediction)? ComputeCanonical()
    {
        (PidPrediction gains, PidMetricsPrediction metrics)? best = null;
        double bestCost = double.PositiveInfinity;

        foreach (double kp in KpGrid)
        foreach (double ki in KiGrid)
        foreach (double kd in KdGrid)
        {
            var gains = new PidPrediction { Kp = (float)kp, Ki = (float)ki, Kd = (float)kd };
            var sim = _sim.SimulateStepResponse(gains, reference: 1.0, duration: 0, dt: SearchDt);
            var (rise, os, settle, ssePct) =
                PidSimulator.ComputeStepMetrics(sim.Time, sim.Amplitude, 1.0);
            bool stable = PidSimulator.IsResponseStable(sim.Amplitude, 1.0);

            var metrics = new PidMetricsPrediction
            {
                Overshoot        = (float)os,
                RiseTime         = (float)rise,
                SettlingTime     = (float)settle,
                SteadyStateError = (float)(ssePct / 100.0),
            };

            // Keep only tunings the diagnosis itself would call ideal — same thresholds,
            // so the recommendation and the diagnosis can never disagree.
            if (PidDiagnosisCalculator.Evaluate(metrics, stable) != PidDiagnosisCode.Ideal)
                continue;

            // Canonical rule — a deliberate policy choice, isolated here so it is easy to
            // swap. Balance response speed against total control effort: pure "fastest" runs
            // to the grid edge in an actuator-free sim, and pure "gentlest" recommends a
            // sluggish tuning that only just passes; their sum picks a crisp, well-damped,
            // moderate-gain response (Kp=0.8, Ki=0.01, Kd=0 for this FOPDT plant). Kd falls
            // out at 0 because a large dead time makes derivative action unhelpful here —
            // a correct, teachable result, not a bug.
            double cost = settle + (kp + ki + kd);
            if (cost < bestCost)
            {
                bestCost = cost;
                best = (gains, metrics);
            }
        }

        return best;
    }
}
