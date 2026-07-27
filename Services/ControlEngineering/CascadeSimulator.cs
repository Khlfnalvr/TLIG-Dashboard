using System;
using System.Linq;
using TLIGDashboard.Models.ControlEngineering;

namespace TLIGDashboard.Services.ControlEngineering;

/// <summary>
/// RK4 simulator for a two-loop cascade control system modelling a heat exchanger,
/// using the <b>lab-identified FOPDT plants</b> (open-loop step test, validated against
/// experiment):
/// <code>
///   Gp1(s) = 0.6361 / (101.53 s + 1) · e^(-52.0866 s)   primary : temperature (shell out), input = flow
///   Gp2(s) = 1.624  / (0.3645 s + 1) · e^(-3.089  s)    secondary: flow (tube),            input = valve
/// </code>
///
/// <para><b>Structure</b></para>
/// <code>
///  Tsp ─►(+)─► [PID temperature, OUTER] ─► Fsp ─►(+)─► [PI flow, INNER] ─► valve u
///        ▲ −                                       ▲ −                          │
///        │  T                                      │  F        ┌────────────────┘
///        │       Gp1 (τ=101.5s, θ=52s)   Gp2 (τ=0.36s, θ=3.1s) │   + disturbance
///        └──────── temperature ◄── F ◄──── flow ◄──────────────┴───────────┘
/// </code>
///
/// The outer PID's output is the inner loop's setpoint. Both loops carry integral
/// action → temperature reaches setpoint with zero steady-state error. Each plant
/// carries a pure transport <b>dead time</b> (the e^(−θs) term), realised as a delay
/// ring buffer on the plant input: the flow plant sees a delayed valve command, and the
/// temperature plant sees a delayed flow.
///
/// <para><b>State</b> — <c>[T, F, IT, IF]</c>: temperature, flow, ∫e_T, ∫e_F.</para>
/// The outer PID uses derivative-on-measurement (−Kd·dT/dt) to avoid derivative kick,
/// and both integrators use conditional (clamping) anti-windup so the response stays
/// well-behaved when the valve or flow setpoint saturates. Default gains were tuned by
/// SIMC for these FOPDT plants (inner PI is necessarily gentle — the flow plant is
/// delay-dominated, θ/τ ≈ 8.5).
/// </summary>
public class CascadeSimulator
{
    // ── Identified FOPDT plant constants ─────────────────────────────────────
    /// Primary (temperature) plant Gp1: gain, time constant (s), dead time (s). Input = flow.
    public const double K1 = 0.6361, Tau1 = 101.53, Theta1 = 52.0866;
    /// Secondary (flow) plant Gp2: gain, time constant (s), dead time (s). Input = valve.
    public const double K2 = 1.624,  Tau2 = 0.3645, Theta2 = 3.089;

    // ── Actuator / setpoint saturation ───────────────────────────────────────
    public const double FlowSpMin = 0.0, FlowSpMax = 300.0;   // outer output (inner SP)
    public const double ValveMin  = 0.0, ValveMax  = 100.0;   // inner output (valve %)

    /// <summary>
    /// Runs the cascade. When <paramref name="withComparison"/> is true a flow
    /// disturbance is injected at 60% of the run and an equivalent single-loop PID is
    /// simulated alongside. The default duration (1200 s) suits the slow temperature
    /// plant (τ≈101 s, θ≈52 s); dt=0.1 s resolves the fast flow plant and its 3.1 s delay.
    /// </summary>
    public CascadeSimulationResult Simulate(
        CascadeInput input, double duration = 1200.0, double dt = 0.1, bool withComparison = true)
    {
        int steps = (int)(duration / dt);
        double distTime = withComparison ? duration * 0.6 : -1;
        double distVal  = withComparison ? input.Disturbance : 0.0;
        int d1 = (int)Math.Round(Theta1 / dt);   // temp plant delay (flow → temperature)
        int d2 = (int)Math.Round(Theta2 / dt);   // flow plant delay (valve → flow)

        double[] time   = new double[steps];
        double[] temp   = new double[steps];
        double[] flow   = new double[steps];
        double[] flowSp = new double[steps];
        double[] valve  = new double[steps];

        // Input histories that feed the plant dead-time buffers.
        double[] uHist = new double[steps];   // valve command  → flow plant (delay d2)
        double[] fHist = new double[steps];   // flow           → temp plant (delay d1)

        // State: [T, F, IT, IF]
        double[] s = { 0, 0, 0, 0 };

        for (int i = 0; i < steps; i++)
        {
            double t = i * dt;
            double d = (distTime >= 0 && t >= distTime) ? distVal : 0.0;
            double uDel = i - d2 >= 0 ? uHist[i - d2] : 0.0;
            double fDel = i - d1 >= 0 ? fHist[i - d1] : 0.0;

            double[] k1 = Derivatives(s, input, d, uDel, fDel, out double fsp, out double u);
            time[i] = t; temp[i] = s[0]; flow[i] = s[1]; flowSp[i] = fsp; valve[i] = u;
            uHist[i] = u;      // valve command issued now
            fHist[i] = s[1];   // flow measured now

            // uDel/fDel are fixed past values, held constant across the sub-steps.
            double[] k2 = Derivatives(Add(s, k1, 0.5 * dt), input, d, uDel, fDel, out _, out _);
            double[] k3 = Derivatives(Add(s, k2, 0.5 * dt), input, d, uDel, fDel, out _, out _);
            double[] k4 = Derivatives(Add(s, k3, dt),       input, d, uDel, fDel, out _, out _);

            for (int j = 0; j < 4; j++)
                s[j] += dt / 6.0 * (k1[j] + 2 * k2[j] + 2 * k3[j] + k4[j]);
        }

        double[] single = withComparison
            ? SimulateSingleLoop(input, duration, dt, d1, d2, distTime, distVal)
            : Array.Empty<double>();

        return new CascadeSimulationResult
        {
            Time = time,
            Temperature = temp,
            Flow = flow,
            FlowSetpoint = flowSp,
            Valve = valve,
            SingleLoopTemperature = single,
            DisturbanceTime = distTime,
        };
    }

    // s' for the full cascade. uDel = delayed valve (into flow plant), fDel = delayed
    // flow (into temp plant). Also reports the (saturated) inner setpoint and valve
    // command for plotting.
    private static double[] Derivatives(
        double[] s, CascadeInput g, double dist, double uDel, double fDel, out double fsp, out double u)
    {
        double T = s[0], F = s[1], IT = s[2], IF = s[3];

        // Outer loop (temperature PID, derivative-on-measurement). Temp plant sees
        // the DELAYED flow.
        double dTdt = (K1 * fDel - T) / Tau1;
        double eT = g.Setpoint - T;
        double outerRaw = g.OuterKp * eT + g.OuterKi * IT - g.OuterKd * dTdt;
        fsp = Clamp(outerRaw, FlowSpMin, FlowSpMax);
        double dIT = AntiWindup(eT, outerRaw, FlowSpMin, FlowSpMax);

        // Inner loop (flow PI).
        double eF = fsp - F;
        double innerRaw = g.InnerKp * eF + g.InnerKi * IF;
        u = Clamp(innerRaw, ValveMin, ValveMax);
        double dIF = AntiWindup(eF, innerRaw, ValveMin, ValveMax);

        // Flow plant sees the DELAYED valve command, plus any disturbance.
        double dFdt = (K2 * uDel + dist - F) / Tau2;
        return new[] { dTdt, dFdt, dIT, dIF };
    }

    // Single-loop baseline: one temperature PID drives the valve directly (no inner
    // flow loop). Same plants, same dead times, same disturbance, same outer gains.
    // State: [T, F, IT].
    private static double[] SimulateSingleLoop(
        CascadeInput g, double duration, double dt, int d1, int d2, double distTime, double distVal)
    {
        int steps = (int)(duration / dt);
        double[] temp = new double[steps];
        double[] uHist = new double[steps];
        double[] fHist = new double[steps];
        double[] s = { 0, 0, 0 };

        for (int i = 0; i < steps; i++)
        {
            double t = i * dt;
            double d = (distTime >= 0 && t >= distTime) ? distVal : 0.0;
            double uDel = i - d2 >= 0 ? uHist[i - d2] : 0.0;
            double fDel = i - d1 >= 0 ? fHist[i - d1] : 0.0;

            double[] k1 = SingleDerivs(s, g, d, uDel, fDel, out double u);
            temp[i] = s[0];
            uHist[i] = u;
            fHist[i] = s[1];

            double[] k2 = SingleDerivs(Add(s, k1, 0.5 * dt), g, d, uDel, fDel, out _);
            double[] k3 = SingleDerivs(Add(s, k2, 0.5 * dt), g, d, uDel, fDel, out _);
            double[] k4 = SingleDerivs(Add(s, k3, dt),       g, d, uDel, fDel, out _);
            for (int j = 0; j < 3; j++)
                s[j] += dt / 6.0 * (k1[j] + 2 * k2[j] + 2 * k3[j] + k4[j]);
        }
        return temp;
    }

    private static double[] SingleDerivs(
        double[] s, CascadeInput g, double dist, double uDel, double fDel, out double u)
    {
        double T = s[0], F = s[1], IT = s[2];
        double dTdt = (K1 * fDel - T) / Tau1;
        double eT = g.Setpoint - T;
        double uRaw = g.OuterKp * eT + g.OuterKi * IT - g.OuterKd * dTdt;
        u = Clamp(uRaw, ValveMin, ValveMax);
        double dIT = AntiWindup(eT, uRaw, ValveMin, ValveMax);
        double dFdt = (K2 * uDel + dist - F) / Tau2;
        return new[] { dTdt, dFdt, dIT };
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

    private static double[] Add(double[] s, double[] k, double f)
    {
        var r = new double[s.Length];
        for (int i = 0; i < s.Length; i++) r[i] = s[i] + f * k[i];
        return r;
    }

    /// <summary>
    /// Reads metrics off a completed run. Temperature step metrics are computed on the
    /// pre-disturbance portion (so the injected disturbance dip doesn't corrupt the
    /// settling time); disturbance rejection is measured after it.
    /// </summary>
    public static CascadeMetrics ComputeMetrics(CascadeSimulationResult r, CascadeInput input)
    {
        double sp = input.Setpoint;
        double distTime = r.DisturbanceTime;

        int cut = distTime >= 0
            ? Math.Max(1, IndexAtOrAfter(r.Time, distTime))
            : r.Time.Length;

        double[] tPre = r.Time[..cut];
        double[] yPre = r.Temperature[..cut];
        var (rise, over, settle, sse) = PidSimulator.ComputeStepMetrics(tPre, yPre, sp);

        bool stable = PidSimulator.IsResponseStable(r.Temperature, sp)
                      && (r.SingleLoopTemperature.Length == 0
                          || PidSimulator.IsResponseStable(r.SingleLoopTemperature, sp));

        double flowPeak = 0, flowSettled = 0;
        if (cut > 0)
        {
            flowPeak = r.Flow[..cut].Max();
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
            FlowPeak = (float)flowPeak,
            FlowSettled = (float)flowSettled,
            CascadeMaxDeviation = cascadeDev,
            SingleLoopMaxDeviation = singleDev,
            Stable = stable,
        };
    }

    private static int IndexAtOrAfter(double[] time, double target)
    {
        for (int i = 0; i < time.Length; i++)
            if (time[i] >= target) return i;
        return time.Length;
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
