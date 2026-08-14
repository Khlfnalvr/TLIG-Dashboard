using System;

namespace TLIGDashboard.Models.ControlEngineering;

/// <summary>
/// Tuning + operating point the cascade designer runs against. The cascade controls a
/// heat exchanger: the <b>outer</b> loop regulates temperature with a full PID whose
/// output is the setpoint for the <b>inner</b> loop, which regulates flow with a PI and
/// drives the valve. Inner is fast, outer is slow — the classic cascade arrangement.
/// </summary>
public class CascadeInput
{
    // ── Outer loop — Temperature (PID, primary) ──────────────────────────────
    // SIMC-tuned for the identified Gp1 (K=0.6361, τ=101.53 s, θ=52.09 s), with a mild
    // derivative to trim overshoot.
    public float OuterKp { get; set; } = 1.53f;
    public float OuterKi { get; set; } = 0.015f;
    public float OuterKd { get; set; } = 8.0f;

    // ── Inner loop — Flow (PI, secondary) ────────────────────────────────────
    // SIMC-tuned for the identified Gp2 (K=1.624, τ=0.3645 s, θ=3.089 s). Necessarily
    // gentle: the flow plant is delay-dominated (θ/τ ≈ 8.5).
    public float InnerKp { get; set; } = 0.036f;
    public float InnerKi { get; set; } = 0.10f;

    /// <summary>Temperature setpoint (°C) the outer loop drives to.</summary>
    public float Setpoint { get; set; } = 60f;

    /// <summary>
    /// Step change in the flow path injected partway through the run (e.g. a supply-
    /// pressure drop), in flow units. The inner loop sees and corrects it fast; a
    /// single-loop temperature controller does not — this is what makes cascade shine.
    /// Zero disables the disturbance (pure setpoint-tracking view).
    /// </summary>
    public float Disturbance { get; set; } = -40f;
}

/// <summary>Time series produced by <see cref="Services.ControlEngineering.CascadeSimulator"/>.</summary>
public class CascadeSimulationResult
{
    public double[] Time             { get; set; } = Array.Empty<double>();
    /// <summary>Primary process variable — temperature (°C).</summary>
    public double[] Temperature      { get; set; } = Array.Empty<double>();
    /// <summary>Secondary process variable — flow (L/min).</summary>
    public double[] Flow             { get; set; } = Array.Empty<double>();
    /// <summary>Inner-loop setpoint = outer PID output (L/min).</summary>
    public double[] FlowSetpoint     { get; set; } = Array.Empty<double>();
    /// <summary>Actuator command — valve opening (%).</summary>
    public double[] Valve            { get; set; } = Array.Empty<double>();
    /// <summary>
    /// Temperature response of a single-loop PID (same outer gains, no inner flow loop)
    /// on the identical plant + disturbance — the baseline cascade is compared against.
    /// Empty when the run was requested without the comparison.
    /// </summary>
    public double[] SingleLoopTemperature { get; set; } = Array.Empty<double>();
    /// <summary>Time the flow disturbance was applied (s), or a negative value if none.</summary>
    public double DisturbanceTime    { get; set; } = -1;
}

/// <summary>
/// Metrics read off a cascade run. The primary block mirrors the single-loop PID metric
/// cards (temperature step response); the rest quantify the inner loop and the
/// disturbance-rejection advantage of cascade over single-loop control.
/// </summary>
public class CascadeMetrics
{
    // Primary (temperature) step-response metrics — same definitions as PidSimulator.
    public float RiseTime         { get; set; }
    public float Overshoot        { get; set; }   // %
    public float SettlingTime     { get; set; }
    public float SteadyStateError { get; set; }   // fraction (0..1)

    // Secondary (flow) loop.
    public float FlowPeak         { get; set; }   // L/min, transient peak
    public float FlowSettled      { get; set; }   // L/min, value just before the disturbance

    // Disturbance rejection — max |T − setpoint| after the flow disturbance.
    public float CascadeMaxDeviation    { get; set; }   // °C
    public float SingleLoopMaxDeviation { get; set; }   // °C

    public bool Stable { get; set; }

    /// <summary>How many times smaller the cascade's peak deviation is vs single-loop.</summary>
    public float DisturbanceImprovement =>
        CascadeMaxDeviation > 1e-6 ? SingleLoopMaxDeviation / CascadeMaxDeviation : 0f;
}
