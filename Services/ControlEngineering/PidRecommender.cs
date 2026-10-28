using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TLIGDashboard.Models.ControlEngineering;

namespace TLIGDashboard.Services.ControlEngineering;

/// <summary>
/// Recommends replacement gains by <b>searching the RK4 simulator</b> — not by asking an LLM
/// or an ML model to guess. It sweeps a grid of Kp/Ki/Kd, simulates each with the same
/// <see cref="PidSimulator"/> the metric cards use, keeps only the tunings that pass the exact
/// acceptance criteria <see cref="PidDiagnosisCalculator"/> uses AND respect the operating
/// constraints (valve 0–100% ↔ flow 0–4.65 L/min, see <see cref="OperatingConstraints"/>),
/// and returns one canonical pick. Because every candidate is verified against the same
/// metrics the diagnosis reads, the recommendation can never contradict the diagnosis — the
/// failure mode of the LLM advisor, whose numbers were never simulated before being shown
/// (a student who applied them saw overshoot go <i>up</i>, from 72.7% to 76.6%).
///
/// <para>The search runs <b>per setpoint</b> (cached): metrics are setpoint-independent only
/// for tunings that never saturate, but valve/flow saturation makes both the response and
/// the feasible set setpoint-dependent — a gain that is gentle at reference 1.0 can slam
/// the actuator at setpoint 60. Evaluating at the actual setpoint keeps the recommendation
/// compliant where it will really run.</para>
/// </summary>
public static class PidRecommender
{
    // A grid of tidy values — clean numbers read better on the recommendation card, and a
    // fixed grid keeps the pick reproducible. The ranges bracket the useful region for this
    // plant and stop well short of the gains that merely run to the grid edge; the
    // simulator saturates the valve at 0–100% with anti-windup (see PidSimulator), so
    // edge-riding tunings pay for their saturation in the metrics — and tunings that
    // DEMAND beyond valve 0–100% / flow 0–4.65 L/min are rejected outright.
    private static readonly double[] KpGrid = { 0.2, 0.3, 0.5, 0.8, 1, 1.5, 2, 3, 4, 5, 7, 10 };
    private static readonly double[] KiGrid = { 0.01, 0.02, 0.05, 0.1, 0.2, 0.4, 0.8, 1.5 };
    private static readonly double[] KdGrid = { 0, 0.1, 0.25, 0.5, 1, 2 };

    // Coarser step than the display run (0.01): the winner's metrics are effectively
    // identical, and it keeps the full 576-point sweep fast even though the FOPDT plant
    // (tau ~101 s + 52 s dead time) runs for a few hundred simulated seconds per candidate.
    private const double SearchDt = 0.1;

    // PidSimulator holds no mutable state, so a single shared instance is safe to reuse,
    // including from the parallel grid sweep below.
    private static readonly PidSimulator _sim = new();

    // The whole evaluated grid cached per setpoint (rounded), so repeat RUNs at one
    // operating point never re-simulate. Bounded so the cache can't grow without limit.
    // Mirrors CascadeRecommender's per-setpoint cache.
    private const int MaxCachedGrids = 8;
    private static readonly ConcurrentDictionary<int, List<(PidPrediction gains, PidMetricsPrediction metrics)>> _gridCache = new();
    private static readonly ConcurrentQueue<int> _gridOrder = new();

    /// <summary>
    /// Gains to recommend for a response with the given metrics at the given setpoint,
    /// or null when the current tuning already meets every criterion AND respects the
    /// operating constraints (nothing to fix — don't nudge a student off a working
    /// tuning) or, defensively, when the search found nothing feasible.
    /// A metric-ideal tuning that VIOLATES valve/flow constraints still gets a
    /// compliant recommendation — it is not a working tuning.
    /// </summary>
    public static (PidPrediction gains, PidMetricsPrediction metrics)? Recommend(
        PidMetricsPrediction currentMetrics, bool currentStable, float setpoint, bool currentViolates = false)
    {
        // Setpoint di luar jangkauan plant tidak mungkin dicapai gain apa pun; lewati
        // pencarian grid yang mahal dan jangan tawarkan tuning yang tetap gagal.
        if (!SetpointReachability.IsSingleLoopReachable(setpoint))
            return null;

        if (!currentViolates
            && PidDiagnosisCalculator.Evaluate(currentMetrics, currentStable) == PidDiagnosisCode.Ideal)
            return null;

        (PidPrediction gains, PidMetricsPrediction metrics)? best = null;
        double bestCost = double.PositiveInfinity;
        foreach (var c in EvaluateGrid(setpoint))
        {
            // Keep only tunings the diagnosis itself would call ideal — same thresholds,
            // so the recommendation and the diagnosis can never disagree. The grid only
            // holds constraint-compliant candidates (see EvaluateCandidate).
            if (PidDiagnosisCalculator.Evaluate(c.metrics, true) != PidDiagnosisCode.Ideal)
                continue;

            double cost = c.metrics.SettlingTime + (c.gains.Kp + c.gains.Ki + c.gains.Kd);
            if (cost < bestCost)
            {
                bestCost = cost;
                best = c;
            }
        }

        return best;
    }

    // Simulates every grid point once at the given setpoint and caches the result.
    // Only constraint-compliant, stable candidates are kept — the grid therefore IS the
    // feasible set. The sweep runs in parallel (PidSimulator keeps no mutable state).
    private static List<(PidPrediction gains, PidMetricsPrediction metrics)> EvaluateGrid(float setpoint)
    {
        float sp = float.IsNaN(setpoint) || setpoint <= 0 ? 1f : setpoint;
        int key = (int)Math.Round(sp);
        if (_gridCache.TryGetValue(key, out var cached)) return cached;

        var combos = new List<(double kp, double ki, double kd)>(KpGrid.Length * KiGrid.Length * KdGrid.Length);
        foreach (double kp in KpGrid)
        foreach (double ki in KiGrid)
        foreach (double kd in KdGrid)
            combos.Add((kp, ki, kd));

        var kept = new ConcurrentQueue<(PidPrediction gains, PidMetricsPrediction metrics)>();
        Parallel.For(0, combos.Count, i =>
        {
            var (kp, ki, kd) = combos[i];
            var gains = new PidPrediction { Kp = (float)kp, Ki = (float)ki, Kd = (float)kd };
            var sim = _sim.SimulateStepResponse(gains, reference: sp, duration: 0, dt: SearchDt);
            // Constraint valve 0–100% / flow 0–4.65 L/min: a gain that DEMANDS beyond the
            // limits is rejected — even though saturation makes its curve look fine.
            if (PidSimulator.ViolatesOperatingConstraints(sim))
                return;
            var (rise, os, settle, ssePct) =
                PidSimulator.ComputeStepMetrics(sim.Time, sim.Amplitude, sp);
            bool stable = PidSimulator.IsResponseStable(sim.Amplitude, sp);
            if (!stable)
                return;
            kept.Enqueue((gains, new PidMetricsPrediction
            {
                Overshoot        = (float)os,
                RiseTime         = (float)rise,
                SettlingTime     = (float)settle,
                SteadyStateError = (float)(ssePct / 100.0),
            }));
        });

        var list = kept.ToList();
        _gridCache[key] = list;
        _gridOrder.Enqueue(key);
        while (_gridOrder.Count > MaxCachedGrids && _gridOrder.TryDequeue(out int oldest))
            _gridCache.TryRemove(oldest, out _);
        return list;
    }
}
