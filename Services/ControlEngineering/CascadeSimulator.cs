using System;
using System.Collections.Generic;
using System.Linq;
using TLIGDashboard.Models.ControlEngineering;

namespace TLIGDashboard.Services.ControlEngineering;

/// <summary>
/// RK4 simulator for a two-loop cascade control system modelling a heat exchanger,
/// using the <b>lab-identified FOPDT plants</b> (open-loop step test, validated against
/// experiment):
/// <code>
///   Gp1(s) = 10.0 / (267.3 s + 1)                         primary : temperature (shell out), input = flow
///   Gp2(s) = 0.0465 / (1.34 s + 1) · e^(-13.56 s)          secondary: flow,                   input = valve
///   F(t)   = FlowBias + Gp2(s)·valve  (0.0465·valve → 0…4.65 L/min)
///
/// <para>The primary loop models the temperature <b>rise above the inlet water temperature</b>:
/// <c>T(t) = T_inlet + ΔT(t)</c>, with <c>ΔT</c> driven by the flow above its floor
/// (<c>F − FlowBias</c>). <c>T_inlet</c> is an input (default 30 °C), not a fixed bias.</para>
/// </code>
///
/// <para><b>Structure</b></para>
/// <code>
///  Tsp ─►(+)─► [PID temperature, OUTER] ─► Fsp ─►(+)─► [PI flow, INNER] ─► valve u
///        ▲ −                                       ▲ −                          │
///        │  T                                      │  F        ┌────────────────┘
///        │       Gp1 (τ=267.3s, θ=0)     Gp2 (τ=1.34s, θ=13.6s) │   + disturbance
///        └──────── temperature ◄── F ◄──── flow ◄──────────────┴───────────┘
/// </code>
///
/// The outer PID's output is the inner loop's setpoint. Both loops carry integral
/// action → temperature reaches setpoint with zero steady-state error. A plant with a
/// pure transport <b>dead time</b> (the e^(−θs) term) gets a delay ring buffer on its
/// input, so the flow plant sees a delayed valve command. The latest identification puts
/// θ1 at zero, so the temperature plant sees the flow of the current instant instead —
/// <see cref="Theta1"/> = 0 selects that path (see <c>NoDelay</c>).
///
/// <para><b>State</b> — <c>[T, F, IT, IF]</c>: temperature, flow, ∫e_T, ∫e_F.</para>
/// The outer PID uses derivative-on-measurement (−Kd·dT/dt) to avoid derivative kick,
/// and both integrators use conditional (clamping) anti-windup so the response stays
/// well-behaved when the valve or flow setpoint saturates. Default gains were tuned by
/// SIMC for these FOPDT plants and verified on this simulator. The flow plant is strongly
/// dead-time-dominant (θ/τ ≈ 10.1), so the inner PI must stay gentle (SIMC τc = 1.5θ) —
/// its closed loop (≈ τc2+θ2 ≈ 33.9 s) is the effective delay the outer loop is tuned against.
/// </summary>
public class CascadeSimulator
{
    // ── Identified FOPDT plant constants ─────────────────────────────────────
    // Lab open-loop step test (Sundaresan-Krishnaswamy 35.3%/85.3%): secondary step
    // 0%→0.5% valve ⇒ 3.25→3.75 L/min flow; primary step 3.25→3.75 L/min flow ⇒
    // 39.2→39.8 °C. The primary's raw θ came out negative (noise, t2/t1 ≈ 45 ≫ 4.48
    // positivity limit), so θ1 = 0. The secondary is strongly dead-time-dominant
    // (θ/τ ≈ 10.1), so the inner PI must stay gentle — its closed loop is what the
    // outer loop feels as delay.
    //
    // Flow maps valve 0–100% to 0–4.65 L/min — the maximum flow LabVIEW reads on the rig
    // at full valve (see OperatingConstraints):
    //   flow  F = FlowBias + K2·valve             (0.0465·valve → 0…4.65 L/min)
    //   temp  T = T_inlet + K1·(flow − FlowBias)  (T_inlet + 0…46.5 °C)
    // K1 stays at 10 so the outer gains (sent to LabVIEW as KC/KI/KD) keep their loop gain.
    // The valve can additionally be capped below 100% (CascadeInput.ValveLimit — the
    // Dashboard's "Bukaan Valve" box): a lower cap means less flow, a slower rise, and
    // eventually a setpoint that cannot be reached at all.
    // The inlet water temperature is an INPUT (default 30 °C), not a hard-coded bias,
    // so the reachable window shifts with the lab's actual incoming temperature.
    /// Primary (temperature) plant Gp1: gain (°C per L/min above the flow floor),
    /// time constant (s), dead time (s). Input = flow.
    public const double K1 = 10.0, Tau1 = 267.3, Theta1 = 0.0;
    /// <summary>Inlet water temperature (°C) used when the caller does not supply one.</summary>
    public const double DefaultInletTemperature = 30.0;
    /// Secondary (flow) plant Gp2: gain, time constant (s), dead time (s). Input = valve.
    /// K2 = 4.65/100 = 0.0465 L/min per % valve so 0–100% ↔ 0–4.65 L/min.
    public const double K2 = 0.0465, Tau2 = 1.34, Theta2 = 13.56;
    /// <summary>Flow floor (L/min): flow at valve 0%, i.e. the zero of the ΔF deviation.</summary>
    public const double FlowBias = 0.0;

    // ── Actuator / setpoint saturation ───────────────────────────────────────
    // Outer output (inner SP) = flow operating range; inner output = valve range.
    // Mirrors OperatingConstraints.FlowMin/Max and ValveMin/Max.
    public const double FlowSpMin = 0.0, FlowSpMax = 4.65;  // outer output (inner SP, L/min)
    public const double ValveMin  = 0.0, ValveMax  = 100.0;   // inner output (valve %)

    // ── Jangkauan suhu yang SECARA FISIK bisa dicapai plant ──────────────────
    // Valve 0%     → flow FlowBias (0 L/min) → T = T_inlet                         (ΔT = 0)
    // Valve batas  → flow 0,0465·batas       → T = T_inlet + 10·0,0465·batas
    //                (batas 100% → 4,65 L/min → T_inlet + 46,5 °C)
    // Di luar rentang ini tidak ada nilai Kp/Ki/Kd yang bisa mencapai setpoint:
    // aktuator sudah di batas, jadi "tidak pernah settle" adalah batas otoritas
    // aktuator, bukan ketidakstabilan loop.
    public static double MinReachableTemperature(double inletTemp) =>
        inletTemp + K1 * (FlowSpMin - FlowBias);

    public static double MaxReachableTemperature(double inletTemp, double valveLimit = ValveMax) =>
        inletTemp + K1 * (MaxFlow(valveLimit) - FlowBias);

    /// <summary>Flow tunak tertinggi (L/min) yang bisa diberikan valve yang dibatasi <paramref name="valveLimit"/> %.</summary>
    public static double MaxFlow(double valveLimit = ValveMax) =>
        Math.Min(FlowSpMax, FlowBias + K2 * NormalizeValveLimit(valveLimit));

    /// <summary>Batas valve yang dipakai simulator: 0–100 %, NaN dibaca 100 % (tanpa batas).</summary>
    public static double NormalizeValveLimit(double valveLimit) =>
        double.IsFinite(valveLimit) ? Clamp(valveLimit, ValveMin, ValveMax) : ValveMax;

    /// <summary>
    /// Bukaan valve tunak (%) yang dibutuhkan untuk menahan <paramref name="setpoint"/> —
    /// rekomendasi bukaan minimum untuk kotak "Bukaan Valve". Bisa &gt; 100 bila setpoint
    /// di luar jangkauan rig.
    /// </summary>
    public static double RequiredValve(double setpoint, double inletTemp) =>
        Math.Max(0.0, (setpoint - inletTemp) / (K1 * K2));

    /// <summary>
    /// True bila <paramref name="setpoint"/> berada di dalam jangkauan tunak plant untuk
    /// suhu air masuk <paramref name="inletTemp"/> dan batas valve <paramref name="valveLimit"/>.
    /// Toleransi kecil menyerap pembulatan float dari kotak input.
    /// </summary>
    public static bool IsSetpointReachable(double setpoint, double inletTemp, double valveLimit = ValveMax) =>
        setpoint >= MinReachableTemperature(inletTemp) - 0.05 &&
        setpoint <= MaxReachableTemperature(inletTemp, valveLimit) + 0.05;

    /// <summary>
    /// Suhu masuk yang dipakai simulator: nilai pemanggil bila finite, selain itu default 30 °C.
    /// </summary>
    public static double NormalizeInletTemperature(double inletTemp) =>
        double.IsFinite(inletTemp) ? inletTemp : DefaultInletTemperature;

    // ── Dead-time lookup ──────────────────────────────────────────────────────
    /// <summary>
    /// Stands in for "this plant has no dead time", so the derivative functions use the value
    /// of the current instant instead of a past one. It cannot be read out of the history: the
    /// derivative for step i is evaluated BEFORE sample i is appended, so a zero-step lookup
    /// would run off the end of the buffer (or, in the pre-sized single-loop arrays, silently
    /// read a zero that was never written). Only the state vector carries the current value.
    /// </summary>
    private const double NoDelay = double.NaN;

    /// <summary>
    /// The plant input from <paramref name="steps"/> samples ago — zero before the buffer has
    /// filled, <see cref="NoDelay"/> when the plant carries no dead time at all.
    /// </summary>
    private static double Delayed(IReadOnlyList<double> history, int i, int steps) =>
        steps <= 0 ? NoDelay
                   : i - steps >= 0 ? history[i - steps]
                                    : 0.0;

    // ── Adaptive-run settle detection (temperature loop) ─────────────────────
    // The run stops once the temperature has stopped moving AND has arrived at the
    // setpoint: the trailing window is flat, dT/dt ≈ 0, and T is within a small band of the
    // setpoint. The setpoint-proximity test is essential — both loops carry integral action,
    // so the true steady state IS the setpoint; without it a sluggish response creeping to
    // target (tiny Ki) can look "flat" while still far below the setpoint, and overshoot
    // (measured against that last sample) would be badly overstated. Same rule now also
    // implemented in PidSimulator; here scaled for the slow temperature plant
    // (Gp1 τ≈267 s, Gp2 τ≈1.34 s + 13.6 s dead time).
    private const double MaxDuration         = 8000.0;  // hard cap on total simulated time (s)
    private const double MinStepSeconds      = 80.0;    // θ1 is 0 now, but a slow start is still nearly flat — don't read it as "settled"
    private const double MinRecoverSeconds   = 120.0;   // after injection, long enough for the disturbance dip to develop
    private const double SettleTolFraction   = 1e-5;    // flat-window span + |dT/dt|, as a fraction of the setpoint
    private const double SettleBandFraction  = 0.005;   // |T − setpoint| must be within this fraction to count as settled
    private const double SettleWindowSeconds = 8.0;
    private const double SettleCheckSeconds  = 1.0;

    /// <summary>
    /// Runs the cascade step response and — when <paramref name="withComparison"/> is true and
    /// the disturbance is non-zero — injects a flow disturbance <b>after the step has settled</b>,
    /// simulating an equivalent single-loop PID alongside for comparison.
    /// <para>
    /// <paramref name="duration"/> &lt;= 0 (the default) runs <b>adaptively</b>: the step is
    /// integrated until the temperature actually settles, the disturbance is injected there, and
    /// the run continues until the disturbance recovery settles — capped at <see cref="MaxDuration"/>.
    /// This guarantees the pre-disturbance window handed to <see cref="ComputeMetrics"/> holds the
    /// fully-settled step, so overshoot/settling are measured against the real steady state rather
    /// than a truncated sample, and disturbance rejection is measured on a settled system. A
    /// positive <paramref name="duration"/> forces a fixed-length run with the disturbance at 60%
    /// of it (legacy/testing). dt=0.1 s resolves the fast flow plant and its 13.6 s delay.
    /// </para>
    /// </summary>
    public CascadeSimulationResult Simulate(
        CascadeInput input, double duration = 0.0, double dt = 0.1, bool withComparison = true)
    {
        bool adaptive  = duration <= 0;
        int  maxSteps  = adaptive ? (int)(MaxDuration / dt) : Math.Max(1, (int)(duration / dt));
        int  window    = (int)(SettleWindowSeconds / dt);
        int  checkEvery= Math.Max(1, (int)(SettleCheckSeconds / dt));
        int  minStep   = (int)(MinStepSeconds / dt);
        int  minRecover= (int)(MinRecoverSeconds / dt);
        int  d1 = (int)Math.Round(Theta1 / dt);   // temp plant delay (flow → temperature)
        int  d2 = (int)Math.Round(Theta2 / dt);   // flow plant delay (valve → flow)

        double scale = Math.Max(1.0, Math.Abs(input.Setpoint));
        double tol = SettleTolFraction * scale;
        double settleBand = SettleBandFraction * scale;

        int cap = Math.Min(maxSteps, 16384);
        var time   = new List<double>(cap);
        var temp   = new List<double>(cap);
        var flow   = new List<double>(cap);
        var flowSp = new List<double>(cap);
        var valve  = new List<double>(cap);
        var rawFlowSp = new List<double>(cap);
        var rawValve  = new List<double>(cap);

        // Input histories that feed the plant dead-time buffers (grow with the run).
        var uHist = new List<double>(cap);   // valve command  → flow plant (delay d2)
        var fHist = new List<double>(cap);   // flow           → temp plant (delay d1)

        // Inlet water temperature: caller-supplied (default 30 °C). The absolute level is
        // T_inlet + ΔT, so a colder/hotter inlet shifts the whole response; the dynamics
        // depend only on the flow above its floor.
        input.InletTemperature = (float)NormalizeInletTemperature(input.InletTemperature);
        input.ValveLimit       = (float)NormalizeValveLimit(input.ValveLimit);
        if (input.ManualValve is { } mv) input.ManualValve = (float)NormalizeValveLimit(mv);
        bool manual = input.IsManual;

        // Manual mode has no controller to reject a disturbance, so injecting one only shows the
        // flow being knocked down with nothing answering it. In Auto the step is scaled to what
        // the valve can still make up (see EffectiveDisturbance).
        double distEff = manual ? 0.0 : EffectiveDisturbance(input);
        bool wantDisturbance = withComparison && distEff != 0.0;

        // State scalars: T, F, IT, IF (kept as locals, not an array, so the RK4
        // sub-steps allocate nothing per step). Valve 0% → flow FlowBias (0 L/min) → T = T_inlet.
        double sT = input.InletTemperature, sF = FlowBias, sIT = 0, sIF = 0;

        // Disturbance injection step: preset for a fixed-length run, discovered at the step's
        // settle for an adaptive one. phase 0 = settling the step, 1 = settling the recovery.
        int distStep = (!adaptive && wantDisturbance) ? (int)(maxSteps * 0.6) : -1;
        int phase = 0;
        bool stepSettled = false;   // did the temperature step reach the setpoint band? (stability)

        for (int i = 0; ; i++)
        {
            double t = i * dt;
            double d = (distStep >= 0 && i >= distStep) ? distEff : 0.0;
            double uDel = Delayed(uHist, i, d2);
            double fDel = Delayed(fHist, i, d1);

            Derivatives(sT, sF, sIT, sIF, input, d, uDel, fDel,
                out double a1T, out double a1F, out double a1IT, out double a1IF, out double fsp, out double u, out double fspRaw, out double uRaw);
            time.Add(t); temp.Add(sT); flow.Add(sF); flowSp.Add(fsp); valve.Add(u);
            rawFlowSp.Add(fspRaw); rawValve.Add(uRaw);
            uHist.Add(u);    // valve command issued now
            fHist.Add(sF);   // flow measured now

            // Settle check (adaptive only): the temperature window is flat, dT/dt ≈ 0, and
            // T has actually reached the setpoint band (see SettleBandFraction). In manual mode
            // nothing drives T to the setpoint — it settles wherever the fixed valve takes it.
            bool settled = adaptive && i >= window && i % checkEvery == 0
                           && (manual || Math.Abs(temp[^1] - input.Setpoint) < settleBand)
                           && IsWindowFlat(temp, window, tol) && Math.Abs(a1T) < tol;
            if (phase == 0 && i >= minStep && settled)
            {
                stepSettled = true;                                    // reached setpoint → stable
                if (wantDisturbance) { distStep = i + 1; phase = 1; }  // inject next step, then recover
                else break;                                            // pure step run — done
            }
            else if (phase == 1 && i >= distStep + minRecover && settled)
            {
                break;                                                 // recovery settled — done
            }
            if (i + 1 >= maxSteps) break;

            // uDel/fDel are fixed past values, held constant across the sub-steps.
            Derivatives(sT + 0.5 * dt * a1T, sF + 0.5 * dt * a1F, sIT + 0.5 * dt * a1IT, sIF + 0.5 * dt * a1IF,
                input, d, uDel, fDel, out double a2T, out double a2F, out double a2IT, out double a2IF, out _, out _, out _, out _);
            Derivatives(sT + 0.5 * dt * a2T, sF + 0.5 * dt * a2F, sIT + 0.5 * dt * a2IT, sIF + 0.5 * dt * a2IF,
                input, d, uDel, fDel, out double a3T, out double a3F, out double a3IT, out double a3IF, out _, out _, out _, out _);
            Derivatives(sT + dt * a3T, sF + dt * a3F, sIT + dt * a3IT, sIF + dt * a3IF,
                input, d, uDel, fDel, out double a4T, out double a4F, out double a4IT, out double a4IF, out _, out _, out _, out _);

            sT  += dt / 6.0 * (a1T + 2 * a2T + 2 * a3T + a4T);
            sF  += dt / 6.0 * (a1F + 2 * a2F + 2 * a3F + a4F);
            sIT += dt / 6.0 * (a1IT + 2 * a2IT + 2 * a3IT + a4IT);
            sIF += dt / 6.0 * (a1IF + 2 * a2IF + 2 * a3IF + a4IF);
        }

        double distTime = distStep >= 0 ? distStep * dt : -1;
        double[] single = withComparison
            ? SimulateSingleLoop(input, temp.Count, dt, d1, d2, distStep, distEff)
            : Array.Empty<double>();

        return new CascadeSimulationResult
        {
            Time = time.ToArray(),
            Temperature = temp.ToArray(),
            Flow = flow.ToArray(),
            FlowSetpoint = flowSp.ToArray(),
            Valve = valve.ToArray(),
            RawFlowSetpoint = rawFlowSp.ToArray(),
            RawValve = rawValve.ToArray(),
            SingleLoopTemperature = single,
            DisturbanceTime = distTime,
            Settled = stepSettled,
        };
    }

    // s' for the full cascade. uDel = delayed valve (into flow plant), fDel = delayed
    // flow (into temp plant). Also reports the (saturated) inner setpoint and valve
    // command for plotting, plus their RAW unsaturated demands (for the operating-
    // constraint check: a tuning that *asks* beyond 0–4.65 L/min / 0–100% is rejected
    // even though the saturated trace looks inside the limits).
    private static void Derivatives(
        double T, double F, double IT, double IF, CascadeInput g, double dist, double uDel, double fDel,
        out double dTdt, out double dFdt, out double dIT, out double dIF, out double fsp, out double u,
        out double fspRaw, out double uRaw)
    {
        // Outer loop (temperature PID, derivative-on-measurement). The temp plant sees the
        // DELAYED flow, or — with θ1 = 0, as the latest identification puts it — the flow of
        // this very instant, which is the one the state vector is carrying.
        // Deviation form: ΔT = K1·(flow − FlowBias), absolute T = T_inlet + ΔT.
        double fIn = double.IsNaN(fDel) ? F : fDel;
        dTdt = (g.InletTemperature + K1 * (fIn - FlowBias) - T) / Tau1;

        // Manual mode: the valve is held where the operator put it and no controller acts —
        // the plant's own open-loop answer to that opening (the VI with "PID Control" off).
        if (g.ManualValve is { } manual)
        {
            u = uRaw = manual;
            fsp = fspRaw = FlowTarget(manual, 0.0);   // the flow that opening settles to
            dIT = dIF = 0.0;
            double uManIn = double.IsNaN(uDel) ? u : uDel;
            dFdt = (FlowTarget(uManIn, dist) - F) / Tau2;
            return;
        }

        double eT = g.Setpoint - T;
        double outerRaw = g.OuterKp * eT + g.OuterKi * IT - g.OuterKd * dTdt;
        fspRaw = outerRaw;
        fsp = Clamp(outerRaw, FlowSpMin, FlowSpMax);
        dIT = AntiWindup(eT, outerRaw, FlowSpMin, FlowSpMax);

        // Inner loop (flow PI). The valve saturates at the user's cap (ValveLimit), not
        // only at 100% — that cap is what the Dashboard's "Bukaan Valve" box sets.
        double eF = fsp - F;
        double innerRaw = g.InnerKp * eF + g.InnerKi * IF;
        uRaw = innerRaw;
        u = Clamp(innerRaw, ValveMin, g.ValveLimit);
        dIF = AntiWindup(eF, innerRaw, ValveMin, g.ValveLimit);

        // Flow plant sees the DELAYED valve command, plus any disturbance.
        // Absolute form: F → FlowBias + K2·valve (0.0465·valve → 0…4.65 L/min).
        double uIn = double.IsNaN(uDel) ? u : uDel;
        dFdt = (FlowTarget(uIn, dist) - F) / Tau2;
    }

    /// <summary>
    /// Flow the plant settles toward for a valve opening plus disturbance. Floored at 0: a pipe
    /// cannot run backwards, so a supply drop on an already-closed valve leaves the flow at 0
    /// instead of driving it negative — which would also cool the water below its own inlet
    /// temperature, something the rig can never do.
    /// </summary>
    private static double FlowTarget(double valve, double dist) =>
        Math.Max(OperatingConstraints.FlowMin, FlowBias + K2 * valve + dist);

    // Single-loop baseline: one temperature PID drives the valve directly (no inner
    // flow loop). Same plants, same dead times, the SAME outer control law — run over the SAME
    // time grid (<paramref name="steps"/>) and with the SAME disturbance step as the cascade run,
    // so the two temperature traces overlay and their post-disturbance deviations compare.
    // State: [T, F, IT].
    private static double[] SimulateSingleLoop(
        CascadeInput g, int steps, double dt, int d1, int d2, int distStep, double dist)
    {
        double[] temp = new double[steps];
        double[] uHist = new double[steps];
        double[] fHist = new double[steps];
        double sT = g.InletTemperature, sF = FlowBias, sIT = 0;

        for (int i = 0; i < steps; i++)
        {
            double d = (distStep >= 0 && i >= distStep) ? dist : 0.0;
            double uDel = Delayed(uHist, i, d2);
            double fDel = Delayed(fHist, i, d1);

            SingleDerivs(sT, sF, sIT, g, d, uDel, fDel, out double a1T, out double a1F, out double a1IT, out double u);
            temp[i] = sT;
            uHist[i] = u;
            fHist[i] = sF;

            SingleDerivs(sT + 0.5 * dt * a1T, sF + 0.5 * dt * a1F, sIT + 0.5 * dt * a1IT, g, d, uDel, fDel, out double a2T, out double a2F, out double a2IT, out _);
            SingleDerivs(sT + 0.5 * dt * a2T, sF + 0.5 * dt * a2F, sIT + 0.5 * dt * a2IT, g, d, uDel, fDel, out double a3T, out double a3F, out double a3IT, out _);
            SingleDerivs(sT + dt * a3T, sF + dt * a3F, sIT + dt * a3IT, g, d, uDel, fDel, out double a4T, out double a4F, out double a4IT, out _);
            sT  += dt / 6.0 * (a1T + 2 * a2T + 2 * a3T + a4T);
            sF  += dt / 6.0 * (a1F + 2 * a2F + 2 * a3F + a4F);
            sIT += dt / 6.0 * (a1IT + 2 * a2IT + 2 * a3IT + a4IT);
        }
        return temp;
    }

    private static void SingleDerivs(
        double T, double F, double IT, CascadeInput g, double dist, double uDel, double fDel,
        out double dTdt, out double dFdt, out double dIT, out double u)
    {
        double fIn = double.IsNaN(fDel) ? F : fDel;
        dTdt = (g.InletTemperature + K1 * (fIn - FlowBias) - T) / Tau1;
        double eT = g.Setpoint - T;
        if (g.ManualValve is { } manual)
        {
            u = manual;      // same fixed opening as the cascade run — no controller in manual
            dIT = 0.0;
        }
        else
        {
            // The outer gains are in L/min of flow per °C (their output is the inner loop's flow
            // setpoint); this PID's output is valve %. Dividing by K2 (L/min per % valve) gives
            // the same control law in valve units — "ask for that much flow" becomes "open the
            // valve that much" — so the comparison isolates what the inner loop adds instead of
            // pitting cascade against a controller ~21× too weak.
            double uRaw = (g.OuterKp * eT + g.OuterKi * IT - g.OuterKd * dTdt) / K2;
            u = Clamp(uRaw, ValveMin, g.ValveLimit);
            dIT = AntiWindup(eT, uRaw, ValveMin, g.ValveLimit);
        }
        double uIn = double.IsNaN(uDel) ? u : uDel;
        dFdt = (FlowTarget(uIn, dist) - F) / Tau2;
    }

    /// <summary>
    /// The disturbance step actually injected: the requested <see cref="CascadeInput.Disturbance"/>,
    /// scaled down to 80% of what the valve can still make up at this setpoint. A supply drop
    /// larger than the valve's headroom (e.g. −1 L/min at 70 °C, which already needs 4 of the
    /// 4.65 L/min) cannot be rejected by ANY controller — the temperature would just fall to a
    /// new, lower plateau and the run would never settle — which tests the rig's capacity, not
    /// the loop. 0 when there is no headroom left at all (setpoint at or beyond the limit).
    /// </summary>
    public static double EffectiveDisturbance(CascadeInput g)
    {
        double d = g.Disturbance;
        if (!double.IsFinite(d) || d == 0.0) return 0.0;

        double fNeed = FlowBias + Math.Max(0.0, (g.Setpoint - g.InletTemperature) / K1);
        double room  = d < 0
            ? MaxFlow(g.ValveLimit) - fNeed   // valve must open further to make up a drop
            : fNeed - FlowBias;               // valve must close to absorb a surge
        double limit = 0.8 * room;
        if (limit < 0.05) return 0.0;         // no meaningful headroom: skip the disturbance
        return Math.Sign(d) * Math.Min(Math.Abs(d), limit);
    }

    // Conditional-integration anti-windup: stop integrating when the output is
    // saturated and the error would only push it further into the limit.
    private static double AntiWindup(double error, double rawOutput, double lo, double hi)
    {
        if (rawOutput > hi && error > 0) return 0.0;
        if (rawOutput < lo && error < 0) return 0.0;
        return error;
    }

    private static double Clamp(double x, double lo, double hi) => Math.Max(lo, Math.Min(hi, x));

    /// <summary>
    /// True bila run MELANGGAR batas operasi valve 0–100% / flow 0–4,65 L/min
    /// (lihat <see cref="OperatingConstraints"/>). Diperiksa:
    /// permintaan mentah outer (flow SP) &amp; inner (valve), plus trace aktual
    /// flow &amp; valve. Meminta JELAS di luar batas = pelanggaran walau sinyal jenuhnya
    /// tampak di dalam batas — inilah yang membuat recommender MENOLAK gain agresif.
    /// Sentuhan batas setara noise numerik (slack
    /// <see cref="OperatingConstraints.RawValveTolerance"/>,
    /// <see cref="OperatingConstraints.RawFlowTolerance"/>) bukan pelanggaran;
    /// trace AKTUAL harus benar-benar di dalam batas.
    /// </summary>
    public static bool ViolatesOperatingConstraints(CascadeSimulationResult r)
    {
        if (r.RawFlowSetpoint is { Length: > 0 } rfsp)
            foreach (double v in rfsp)
                if (!OperatingConstraints.IsRawFlowOk(v)) return true;
        if (r.RawValve is { Length: > 0 } rv)
            foreach (double v in rv)
                if (!OperatingConstraints.IsRawValveOk(v)) return true;
        if (r.Flow is { Length: > 0 } fl)
            foreach (double v in fl)
            {
                if (double.IsNaN(v) || double.IsInfinity(v)) return true;
                if (!OperatingConstraints.IsFlowWithin(v)) return true;
            }
        if (r.Valve is { Length: > 0 } vv)
            foreach (double v in vv)
            {
                if (double.IsNaN(v) || double.IsInfinity(v)) return true;
                if (!OperatingConstraints.IsValveWithin(v)) return true;
            }
        // Flow SP jenuh harus di dalam 0–4,65 (selalu benar bila clamp dipakai,
        // tapi diperiksa eksplisit agar perubahan clamp di masa depan ketahuan).
        if (r.FlowSetpoint is { Length: > 0 } fsp)
            foreach (double v in fsp)
            {
                if (double.IsNaN(v) || double.IsInfinity(v)) return true;
                if (!OperatingConstraints.IsFlowWithin(v)) return true;
            }
        return false;
    }

    /// <summary>True if the last <paramref name="window"/> samples span less than <paramref name="tol"/>.</summary>
    private static bool IsWindowFlat(List<double> values, int window, double tol)
    {
        double min = double.MaxValue, max = double.MinValue;
        for (int i = values.Count - window; i < values.Count; i++)
        {
            double v = values[i];
            if (v < min) min = v;
            if (v > max) max = v;
            if (max - min >= tol) return false;
        }
        return true;
    }

    /// <summary>
    /// Reads metrics off a completed run. Temperature step metrics are computed on the
    /// pre-disturbance portion (so the injected disturbance dip doesn't corrupt the
    /// settling time); disturbance rejection is measured after it.
    /// <para>
    /// The run is ABSOLUTE (T0 = T_inlet at valve 0%), so metrics are read on the
    /// DEVIATION (T−T0 vs SP−T0) — otherwise a 30→60 °C step would already sit above the
    /// 10/90% lines of SP=60 and report rise ≈ 0. Error indices use the same deviation
    /// (identical error to absolute).
    /// </para>
    /// </summary>
    public static CascadeMetrics ComputeMetrics(CascadeSimulationResult r, CascadeInput input)
    {
        double sp = input.Setpoint;
        double distTime = r.DisturbanceTime;

        int cut = distTime >= 0
            ? Math.Max(1, IndexAtOrAfter(r.Time, distTime))
            : r.Time.Length;

        double[] tPre = cut < r.Time.Length ? r.Time[..cut] : r.Time;
        double[] yAbs = cut < r.Temperature.Length ? r.Temperature[..cut] : r.Temperature;
        double y0 = r.Temperature.Length > 0 ? r.Temperature[0] : 0.0;
        double spDev = sp - y0;
        double[] yPre = new double[yAbs.Length];
        for (int i = 0; i < yAbs.Length; i++) yPre[i] = yAbs[i] - y0;
        var (rise, over, settle, sse) = PidSimulator.ComputeStepMetrics(tPre, yPre, spDev);
        var (iae, ise, itae) = PidSimulator.ComputePerformanceIndices(tPre, yPre, spDev);

        // Manual mode: nothing drives T to the setpoint, so the shape of the response is read
        // against where it actually settles (the open-loop step test: rise/settling of the plant
        // itself at that valve opening). The steady-state error and IAE/ISE/ITAE stay against
        // the setpoint — they say how far that fixed opening leaves the student from the target.
        if (input.IsManual && yPre.Length > 0)
        {
            double finalDev = yPre[^1];
            if (Math.Abs(finalDev) > 1e-6)
                (rise, over, settle, _) = PidSimulator.ComputeStepMetrics(tPre, yPre, finalDev);
            else
                (rise, over, settle) = (0.0, 0.0, 0.0);   // valve shut: nothing moves
        }

        // Stable ⇔ the temperature step actually settled at the setpoint within the run
        // budget. The valve/flow limits keep every response bounded, so a "did it blow up?"
        // test can never fail here; "did it reach and hold the setpoint?" is the signal that
        // separates a usable tuning from one that oscillates or is hopelessly sluggish.
        //
        // KECUALI bila setpoint berada di luar jangkauan plant (lihat
        // MinReachableTemperature/MaxReachableTemperature untuk suhu masuk ini): saat itu
        // valve memang sudah jenuh dan plant tidak mampu mencapai setpoint, jadi "tidak
        // settle" bukan ketidakstabilan. Perlakukan sebagai steady-state error — label yang
        // sama yang dipakai PidSimulator untuk loop tunggal — supaya grafik yang datar/stabil
        // tidak dilabeli "tidak stabil" dan saran "turunkan Kp/Ki" tidak menyesatkan.
        bool reachable = IsSetpointReachable(sp, input.InletTemperature, input.ValveLimit);
        bool stable = r.Settled || !reachable;

        double flowPeak = 0, flowSettled = 0;
        if (cut > 0)
        {
            flowPeak = cut < r.Flow.Length ? r.Flow[..cut].Max() : r.Flow.Max();
            flowSettled = r.Flow[cut - 1];
        }

        float cascadeDev = MaxDeviationAfter(r.Time, r.Temperature, sp, distTime);
        float singleDev = r.SingleLoopTemperature.Length == r.Time.Length
            ? MaxDeviationAfter(r.Time, r.SingleLoopTemperature, sp, distTime)
            : 0f;

        return new CascadeMetrics
        {
            RiseTime = (float)rise,
            Overshoot = (float)over,
            SettlingTime = (float)settle,
            SteadyStateError = (float)(sse / 100.0),
            IAE = (float)iae,
            ISE = (float)ise,
            ITAE = (float)itae,
            FlowPeak = (float)flowPeak,
            FlowSettled = (float)flowSettled,
            CascadeMaxDeviation = cascadeDev,
            SingleLoopMaxDeviation = singleDev,
            Stable = stable,
        };
    }

    private static int IndexAtOrAfter(double[] time, double target)
    {
        int idx = Array.BinarySearch(time, target);
        if (idx >= 0) return idx;
        int insert = ~idx;
        return insert <= time.Length ? insert : time.Length;
    }

    private static float MaxDeviationAfter(double[] time, double[] y, double sp, double after)
    {
        if (after < 0) return 0f;
        double max = 0;
        for (int i = 0; i < time.Length; i++)
            if (time[i] >= after) max = Math.Max(max, Math.Abs(y[i] - sp));
        return (float)max;
    }
}
