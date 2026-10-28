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
    // SIMC for Gp1 (K=10 °C per L/min above the flow floor, τ=267.3 s, θ=0), taking the
    // inner loop's closed-loop response (~33.9 s = τc2+θ2) as the effective delay and
    // τc = 4× that. The identified K=1.2 gains (Kp=1.8, Ki=0.007) were scaled by the same
    // 1.2/10 gain ratio to keep the loop gain, so this is the starting point the
    // recommender improves on by simulation, not a claimed optimum. Kd is 0: with no dead
    // time on the temperature plant, derivative action only stretches settling without
    // trimming the overshoot.
    public float OuterKp { get; set; } = 0.216f;
    public float OuterKi { get; set; } = 0.00084f;
    public float OuterKd { get; set; } = 0f;

    // ── Inner loop — Flow (PI, secondary) ────────────────────────────────────
    // SIMC for the identified Gp2 (K=0.0465 L/min per % valve for the 0–4.65 L/min
    // operating range, τ=1.34 s, θ=13.56 s) at τc = 1.5θ: Kp = τ/(K·(τc+θ)) ≈ 0.85,
    // Ki = Kp/τ ≈ 0.63. The flow plant is strongly dead-time-dominant (θ/τ ≈ 10.1), so
    // the inner PI must stay gentle — its closed-loop speed is the effective delay the
    // outer loop feels.
    public float InnerKp { get; set; } = 0.85f;
    public float InnerKi { get; set; } = 0.63f;

    /// <summary>Temperature setpoint (°C) the outer loop drives to.</summary>
    public float Setpoint { get; set; } = 60f;
    // With T_inlet = 30 °C, 60 °C needs 3 L/min of flow (valve ≈ 65%), which is what
    // sizes the disturbance below.

    /// <summary>
    /// Inlet water temperature (°C) — the operating point the response is built on:
    /// <c>T(t) = InletTemperature + ΔT(t)</c>. Default 30 °C (room-temperature water).
    /// Changing it shifts the whole reachable window (T_inlet … T_inlet + 60 °C) without
    /// changing the plant dynamics.
    /// </summary>
    public float InletTemperature { get; set; } = 30f;

    /// <summary>
    /// Step change in the flow path (e.g. a supply-pressure drop), in flow units, injected once
    /// the temperature step has settled so its rejection is measured on a system already at the
    /// setpoint. The inner loop sees and corrects it fast; a single-loop temperature controller
    /// does not — this is what makes cascade shine. Zero disables the disturbance entirely (no
    /// injection, and the disturbance-rejection cards read "--") for a pure setpoint-tracking view.
    /// Sized so that at the default 60 °C the inner loop can still recover it below full
    /// valve ((3 + 1) / 0.0465 ≈ 86%), the same headroom the old −2 L/min had on 1–7 L/min.
    /// </summary>
    public float Disturbance { get; set; } = -1f;

    /// <summary>
    /// Upper limit on the valve opening (%), 0–100 — the Dashboard's "Bukaan Valve" box.
    /// The inner PI still decides the valve, but never beyond this: a wider valve allows
    /// more flow, so the temperature reaches the setpoint faster; too narrow a valve
    /// cannot reach it at all (see <see cref="Services.ControlEngineering.CascadeSimulator.RequiredValve"/>).
    /// </summary>
    public float ValveLimit { get; set; } = 100f;
}

/// <summary>Time series produced by <see cref="Services.ControlEngineering.CascadeSimulator"/>.</summary>
public class CascadeSimulationResult
{
    public double[] Time             { get; set; } = Array.Empty<double>();
    /// <summary>Primary process variable — temperature (°C).</summary>
    public double[] Temperature      { get; set; } = Array.Empty<double>();
    /// <summary>Secondary process variable — flow (L/min).</summary>
    public double[] Flow             { get; set; } = Array.Empty<double>();
    /// <summary>Inner-loop setpoint = outer PID output (L/min, 0–4.65).</summary>
    public double[] FlowSetpoint     { get; set; } = Array.Empty<double>();
    /// <summary>Actuator command — valve opening (%, 0–100).</summary>
    public double[] Valve            { get; set; } = Array.Empty<double>();
    /// <summary>
    /// Permintaan MENTAH outer (L/min) sebelum saturasi 0–4,65 — dipakai filter
    /// constraint recommender (meminta di luar batas = ditolak).
    /// </summary>
    public double[] RawFlowSetpoint  { get; set; } = Array.Empty<double>();
    /// <summary>
    /// Permintaan MENTAH inner (%) sebelum saturasi 0–100 — dipakai filter
    /// constraint recommender.
    /// </summary>
    public double[] RawValve         { get; set; } = Array.Empty<double>();
    /// <summary>
    /// Temperature response of a single-loop PID (same outer gains, no inner flow loop)
    /// on the identical plant + disturbance — the baseline cascade is compared against.
    /// Empty when the run was requested without the comparison.
    /// </summary>
    public double[] SingleLoopTemperature { get; set; } = Array.Empty<double>();
    /// <summary>Time the flow disturbance was applied (s), or a negative value if none.</summary>
    public double DisturbanceTime    { get; set; } = -1;
    /// <summary>
    /// True if the temperature step actually settled at the setpoint within the run budget
    /// (adaptive runs). A loop that oscillates or is too sluggish to settle runs to the cap
    /// with this false — the honest stability signal for a saturating plant whose output can
    /// never diverge to infinity, so a bounded-amplitude test alone never catches a limit cycle.
    /// </summary>
    public bool Settled              { get; set; }
}

/// <summary>
/// One past cascade RUN in a tuning session: the five gains tried and the temperature step
/// metrics they produced. Carried into the advisor prompt so it reviews the current attempt
/// against the trajectory instead of in isolation (mirrors <see cref="PidAttempt"/>).
/// </summary>
public class CascadeAttempt
{
    public float OuterKp { get; set; }
    public float OuterKi { get; set; }
    public float OuterKd { get; set; }
    public float InnerKp { get; set; }
    public float InnerKi { get; set; }
    public float Overshoot { get; set; }
    public float RiseTime { get; set; }
    public float SettlingTime { get; set; }
    public float SteadyStateError { get; set; }
    /// <summary>PidDiagnosisCode name for this attempt (see PidDiagnosisCalculator).</summary>
    public string Diagnosis { get; set; } = "";
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

    // Error-performance indices over the temperature step response — integrals of the tracking
    // error e = setpoint − T (see PidSimulator.ComputePerformanceIndices). Lower is better.
    public float IAE  { get; set; }   // ∫|e| dt    (°C·s)
    public float ISE  { get; set; }   // ∫e² dt     (°C²·s)
    public float ITAE { get; set; }   // ∫t·|e| dt  (°C·s²)

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

    /// <summary>
    /// The primary (temperature) step-response metrics packed as a
    /// <see cref="PidMetricsPrediction"/> so the shared
    /// <see cref="Services.ControlEngineering.PidDiagnosisCalculator"/> can diagnose the
    /// outer loop. The outer plant is the identical FOPDT Gp1 the calculator's thresholds
    /// are anchored to, and these four fields carry the same units it expects
    /// (overshoot %, seconds, steady-state error as a fraction).
    /// </summary>
    public PidMetricsPrediction PrimaryStepMetrics() => new()
    {
        Overshoot        = Overshoot,
        RiseTime         = RiseTime,
        SettlingTime     = SettlingTime,
        SteadyStateError = SteadyStateError,
    };
}
