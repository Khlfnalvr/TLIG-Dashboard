using System.Threading;
using System.Threading.Tasks;
using TLIGDashboard.Models.ControlEngineering;

namespace TLIGDashboard.Services.ControlEngineering;

public sealed class CascadeDesignResult
{
    public CascadeInput            Input                 { get; set; } = new();
    public CascadeSimulationResult Simulation            { get; set; } = new();
    public CascadeMetrics          Metrics               { get; set; } = new();
    /// <summary>Diagnosis label for the OUTER (temperature) PID, from PidDiagnosisAgent.</summary>
    public string                  Diagnosis             { get; set; } = "";
    public string                  AdvisorExplanation    { get; set; } = "";
    public CascadeRecommendation?  AdvisorRecommendation { get; set; }
}

/// <summary>
/// Single integration point for the Cascade Control designer, mirroring
/// <see cref="PidDesignService"/>. Unlike the single-loop PID designer, the cascade
/// simulation runs <b>locally in both build flavors</b>: the RK4 model is cheap and
/// self-contained, the diagnosis classifier reads the same bundled CSV in either flavor,
/// and the LLM advisor already routes through the server proxy on the Client — so no
/// extra server HTTP endpoint is needed.
/// </summary>
public static class CascadeDesignService
{
    private static readonly CascadeSimulator   _sim       = new();
    private static readonly PidDiagnosisAgent  _diagnosis = new();

    public static async Task<CascadeDesignResult> RunAsync(CascadeInput input, CancellationToken ct = default)
    {
        // Guard against a cleared/zero setpoint (NumberBox yields NaN) — a flat response
        // would divide the steady-state-error metric by zero.
        if (float.IsNaN(input.Setpoint) || input.Setpoint <= 0) input.Setpoint = 60f;

        var simResult = await Task.Run(() => _sim.Simulate(input), ct);
        var metrics   = CascadeSimulator.ComputeMetrics(simResult, input);

        // Diagnose the OUTER temperature PID using the shared single-loop classifier —
        // it reasons about a PID's gains + step metrics, which is exactly the primary loop.
        var diagnosis = await Task.Run(() => _diagnosis.Predict(new PidDiagnosisInput
        {
            Kp_Saat_Ini       = input.OuterKp,
            Ki_Saat_Ini       = input.OuterKi,
            Kd_Saat_Ini       = input.OuterKd,
            Overshoot_Riil    = metrics.Overshoot,
            RiseTime_Riil     = metrics.RiseTime,
            SettlingTime_Riil = metrics.SettlingTime,
            SteadyStateError  = metrics.SteadyStateError,
            Is_Stable         = metrics.Stable ? 1f : 0f,
        }).Rekomendasi_Aksi, ct);

        var advisor = await new CascadeAdvisorService().ReviewAsync(input, metrics, ct);

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
