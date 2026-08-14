using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TLIGDashboard.Models.ControlEngineering;

namespace TLIGDashboard.Services.ControlEngineering;

public sealed class CascadeDesignResult
{
    public CascadeInput            Input                 { get; set; } = new();
    public CascadeSimulationResult Simulation            { get; set; } = new();
    public CascadeMetrics          Metrics               { get; set; } = new();
    /// <summary>
    /// <see cref="PidDiagnosisCode"/> name for the OUTER (temperature) PID, from
    /// <see cref="PidDiagnosisCalculator"/> — a stable identifier, not display text.
    /// Render it with <see cref="PidDiagnosisCalculator.Describe(string, PidMetricsPrediction)"/>
    /// so a Client shows its own language rather than the Server's.
    /// </summary>
    public string                  Diagnosis             { get; set; } = "";
    public string                  AdvisorExplanation    { get; set; } = "";
    public CascadeRecommendation?  AdvisorRecommendation { get; set; }
}

/// <summary>
/// Single integration point for the Cascade Control designer, mirroring
/// <see cref="PidDesignService"/>. Unlike the single-loop PID designer, the cascade
/// simulation runs <b>locally in both build flavors</b>: the RK4 model is cheap and
/// self-contained, the diagnosis is direct calculation (no model, no CSV), and the LLM
/// advisor already routes through the server proxy on the Client — so no extra server
/// HTTP endpoint is needed.
/// </summary>
public static class CascadeDesignService
{
    private static readonly CascadeSimulator _sim = new();

    public static async Task<CascadeDesignResult> RunAsync(
        CascadeInput input, IReadOnlyList<CascadeAttempt>? history = null, CancellationToken ct = default)
    {
        // Guard against a cleared/zero setpoint (NumberBox yields NaN) — a flat response
        // would divide the steady-state-error metric by zero.
        if (float.IsNaN(input.Setpoint) || input.Setpoint <= 0) input.Setpoint = 60f;

        var simResult = await Task.Run(() => _sim.Simulate(input), ct);
        var metrics   = CascadeSimulator.ComputeMetrics(simResult, input);

        // Diagnose the OUTER temperature loop by direct calculation on its step metrics
        // (PidDiagnosisCalculator), not the ML classifier the single-loop designer
        // deliberately dropped: the outer plant is the identical FOPDT Gp1 the calculator's
        // thresholds are anchored to, so the verdict agrees with the metric cards, quotes
        // the number behind it, and is a localizable code rather than a raw, out-of-
        // distribution classifier label.
        var diagnosis = PidDiagnosisCalculator.Evaluate(metrics.PrimaryStepMetrics(), metrics.Stable).ToString();

        // Gains to offer come from searching the simulator (verified against the same criteria as
        // the diagnosis), not from the LLM — so the card can't contradict the diagnosis. Null when
        // the current tuning is already ideal. The per-setpoint search is cached after the first run.
        var recommendation = await Task.Run(() => CascadeRecommender.Recommend(metrics, metrics.Stable, input.Setpoint), ct);

        // The LLM now only explains the recommended gains; it no longer picks numbers.
        var advisor = await new CascadeAdvisorService().ReviewAsync(
            input, metrics, history, recommendation?.gains, recommendation?.metrics, ct);

        return new CascadeDesignResult
        {
            Input = input,
            Simulation = simResult,
            Metrics = metrics,
            Diagnosis = diagnosis,
            AdvisorExplanation = advisor.Explanation,
            AdvisorRecommendation = advisor.Recommendation,
        };
    }
}
