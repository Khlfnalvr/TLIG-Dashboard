using System;
using System.Collections.Generic;
using System.Linq;
using TLIGDashboard.Models.ControlEngineering;

namespace TLIGDashboard.Services.ControlEngineering;

public class PidSimulator
{
    // Identified primary plant (lab open-loop test, validated against experiment):
    //   Gp1(s) = 0.6361 / (101.53 s + 1) * e^(-52.0866 s)   temperature, shell outlet.
    // A First-Order-Plus-Dead-Time (FOPDT) plant: a single lag plus a pure transport
    // delay. The delay is realised in SimulateStepResponse as a ring buffer on the
    // plant input.
    private const double Gain     = 0.6361;
    private const double Tau      = 101.53;
    private const double DeadTime = 52.0866;

    /// <summary>
    /// The plant's transfer function as text, built from the very constants the integrator
    /// uses. Handed to the LLM advisor so its description of the plant can never drift
    /// from what is actually simulated.
    /// </summary>
    public static string PlantTransferFunction =>
        $"G(s) = {Gain} / ({Tau}s + 1) * e^(-{DeadTime}s)";

    /// <summary>
    /// Steady-state (DC) gain and dominant time constant of the open-loop plant. For this
    /// FOPDT model that is simply the plant gain and time constant; the long dead time
    /// (see <see cref="PlantTransferFunction"/>) is what makes the loop hard to control and
    /// is quoted to the advisor via the transfer-function string.
    /// </summary>
    public static (double dcGain, double dominantTimeConstant) PlantCharacteristics()
        => (Gain, Tau);

    // Ceiling on simulated time — this plant is slow (tau ~101 s + 52 s dead time), so a
    // well-tuned closed loop still settles in a few hundred seconds.
    private const double MaxDuration = 800.0;
    // Always simulate at least this long. Must exceed the dead time: until t = DeadTime the
    // output sits flat at zero, and the settle detector below would otherwise mistake that
    // initial plateau for a settled response and stop before the plant has even reacted.
    private const double MinDuration = 80.0;
    // The run stops once the response has stopped moving: the trailing window's spread
    // and |dy| both fall under this fraction of the reference.
    private const double SettleTolFraction = 1e-5;
    private const double SettleWindowSeconds = 4.0;
    private const double SettleCheckSeconds = 1.0;

    /// <summary>
    /// Integrates the closed-loop step response with RK4.
    /// <paramref name="duration"/> &lt;= 0 (the default) runs until the response actually
    /// settles, capped at <see cref="MaxDuration"/>.
    /// </summary>
    /// <remarks>
    /// Returns the full-resolution run: <see cref="ComputeStepMetrics"/> must see the real
    /// settled tail to be correct. Use <see cref="BuildDisplayCurve"/> for what to plot.
    /// </remarks>
    public SimulationResult SimulateStepResponse(PidPrediction pid, double reference = 1.0, double duration = 0, double dt = 0.01)
    {
        bool auto = duration <= 0;
        double limit = auto ? MaxDuration : duration;
        int maxSteps = Math.Max(1, (int)(limit / dt));
        int minSteps = auto ? (int)(MinDuration / dt) : maxSteps;
        int window = (int)(SettleWindowSeconds / dt);
        int checkEvery = Math.Max(1, (int)(SettleCheckSeconds / dt));
        int delay = (int)Math.Round(DeadTime / dt);

        double scale = Math.Max(1.0, Math.Abs(reference));
        double tol = SettleTolFraction * scale;
        double blowUp = 1e6 * scale;

        var time = new List<double>(Math.Min(maxSteps, 8192));
        var amplitude = new List<double>(Math.Min(maxSteps, 8192));
        // Plant-input history feeding the dead-time buffer (u delayed by `delay` samples).
        var uHist = new double[maxSteps];

        // State: [y, z] where z is the integral of the error. The plant is first-order,
        // so there is no separate velocity state — dy/dt is algebraic in y and the
        // delayed input.
        double y = 0;
        double z = 0;

        double kp = pid.Kp;
        double ki = pid.Ki;
        double kd = pid.Kd;

        for (int i = 0; i < maxSteps; i++)
        {
            time.Add(i * dt);
            amplitude.Add(y);

            // Diverging gains would otherwise spend the whole budget producing infinities.
            if (double.IsNaN(y) || double.IsInfinity(y) || Math.Abs(y) > blowUp) break;

            double uDel = i - delay >= 0 ? uHist[i - delay] : 0.0;

            // RK4 Integration. uDel is a known past value, held constant across the four
            // sub-steps; the valve command issued now (k1's u) is recorded for future steps.
            double[] k1 = Derivatives(y, z, uDel, reference, kp, ki, kd, out double u);
            uHist[i] = u;

            // dy/dt is k1[0]; the loop has settled once the recent window is flat and the
            // output has stopped moving.
            if (auto && i >= minSteps && i >= window && i % checkEvery == 0 &&
                IsWindowFlat(amplitude, window, tol) && Math.Abs(k1[0]) < tol)
            {
                break;
            }

            double[] k2 = Derivatives(y + 0.5 * dt * k1[0], z + 0.5 * dt * k1[1], uDel, reference, kp, ki, kd, out _);
            double[] k3 = Derivatives(y + 0.5 * dt * k2[0], z + 0.5 * dt * k2[1], uDel, reference, kp, ki, kd, out _);
            double[] k4 = Derivatives(y + dt * k3[0], z + dt * k3[1], uDel, reference, kp, ki, kd, out _);

            y += (dt / 6.0) * (k1[0] + 2 * k2[0] + 2 * k3[0] + k4[0]);
            z += (dt / 6.0) * (k1[1] + 2 * k2[1] + 2 * k3[1] + k4[1]);
        }

        return new SimulationResult
        {
            Time = time.ToArray(),
            Amplitude = amplitude.ToArray(),
            UsedParameters = pid
        };
    }

    /// <summary>True if the last <paramref name="window"/> samples span less than <paramref name="tol"/>.</summary>
    private static bool IsWindowFlat(List<double> amplitude, int window, double tol)
    {
        int start = amplitude.Count - window;
        double min = double.MaxValue, max = double.MinValue;
        for (int i = start; i < amplitude.Count; i++)
        {
            double v = amplitude[i];
            if (v < min) min = v;
            if (v > max) max = v;
            if (max - min >= tol) return false;
        }
        return true;
    }

    /// <summary>
    /// The slice of <paramref name="full"/> worth plotting: the transient plus a short
    /// tail, decimated to at most <paramref name="maxPoints"/>. Metrics need the settled
    /// tail (see <see cref="SimulateStepResponse"/>), but plotting it means a curve whose
    /// interesting part is squeezed into the first fraction of the width — the same
    /// unreadable result as an over-wide chart.
    /// </summary>
    public static SimulationResult BuildDisplayCurve(SimulationResult full, double settlingTime, int maxPoints = 2000)
    {
        double[] t = full.Time, a = full.Amplitude;
        if (t.Length == 0) return full;

        // settlingTime == 0 means it never left the band (or never settled) — show it all.
        double show = settlingTime > 0 ? Math.Max(settlingTime * 1.3, MinDuration) : t[^1];
        int count = t.Length;
        for (int i = 0; i < t.Length; i++)
            if (t[i] > show) { count = i; break; }
        count = Math.Max(2, Math.Min(count, t.Length));

        int stride = Math.Max(1, (int)Math.Ceiling(count / (double)maxPoints));
        int outLen = (count + stride - 1) / stride;
        var ot = new double[outLen];
        var oa = new double[outLen];
        for (int i = 0, j = 0; i < count && j < outLen; i += stride, j++)
        {
            ot[j] = t[i];
            oa[j] = a[i];
        }

        return new SimulationResult { Time = ot, Amplitude = oa, UsedParameters = full.UsedParameters };
    }

    // Closed-loop first-order plant with input dead time:
    //   dy/dt = (Gain * u(t-θ) - y) / Tau       — FOPDT plant, delayed input `uDel`
    //   u     = Kp*(r-y) + Ki*z - Kd*(dy/dt)    — PID, derivative on measurement
    //   dz/dt = r - y
    // u depends on dy/dt, which uses the DELAYED input (a known past value), so there is
    // no algebraic loop.
    private double[] Derivatives(double y, double z, double uDel, double r, double kp, double ki, double kd, out double u)
    {
        double dydt = (Gain * uDel - y) / Tau;
        double e = r - y;
        u = kp * e + ki * z - kd * dydt;
        double dz = e;
        return new[] { dydt, dz };
    }

    /// Rise time (10%→90%), overshoot %, 2%-band settling time, steady-state error % —
    /// shared by the Dashboard's chart display and the diagnosis' input features,
    /// so both read the exact same numbers off a given response curve.
    public static (double rise, double overshootPct, double settling, double steadyErrPct) ComputeStepMetrics(
        double[] time, double[] amplitude, double reference = 1.0)
    {
        if (time.Length == 0 || amplitude.Length == 0) return (0, 0, 0, 0);

        double final = amplitude[^1];
        double peak  = amplitude.Max();
        double overshootPct = final > 0 ? Math.Max(0, (peak - final) / final * 100.0) : 0;
        double steadyErrPct = reference > 0 ? Math.Abs(reference - final) / reference * 100.0 : 0;

        double lo = 0.1 * final, hi = 0.9 * final;
        double t10 = 0, t90 = 0;
        bool got10 = false, got90 = false;
        for (int i = 0; i < amplitude.Length; i++)
        {
            if (!got10 && amplitude[i] >= lo) { t10 = time[i]; got10 = true; }
            if (!got90 && amplitude[i] >= hi) { t90 = time[i]; got90 = true; break; }
        }
        double rise = got90 ? t90 - t10 : 0;

        double band = 0.02 * Math.Abs(final);
        int lastOutside = -1;
        for (int i = 0; i < amplitude.Length; i++)
            if (Math.Abs(amplitude[i] - final) > band) lastOutside = i;
        double settling = lastOutside < 0 ? 0
            : lastOutside + 1 < time.Length ? time[lastOutside + 1] : time[^1];

        return (rise, overshootPct, settling, steadyErrPct);
    }

    /// <summary>
    /// Classic error-performance indices for a step response, integrated (trapezoidal) over the
    /// supplied window with the tracking error e(t) = <paramref name="reference"/> − y(t):
    /// <list type="bullet">
    /// <item><b>IAE</b> = ∫|e| dt — total absolute error.</item>
    /// <item><b>ISE</b> = ∫e² dt — squares the error, so it weighs large (usually early) deviations hardest.</item>
    /// <item><b>ITAE</b> = ∫t·|e| dt — time-weights the error, so it penalises deviations that linger late.</item>
    /// </list>
    /// Lower is better for all three. Read off the same curve as <see cref="ComputeStepMetrics"/>,
    /// so they never disagree with the plotted response. Units follow the signal: IAE in y·s,
    /// ISE in y²·s, ITAE in y·s².
    /// </summary>
    public static (double iae, double ise, double itae) ComputePerformanceIndices(
        double[] time, double[] amplitude, double reference = 1.0)
    {
        double iae = 0, ise = 0, itae = 0;
        int n = Math.Min(time.Length, amplitude.Length);
        for (int i = 1; i < n; i++)
        {
            double dt = time[i] - time[i - 1];
            if (dt <= 0) continue;
            double e0 = reference - amplitude[i - 1];
            double e1 = reference - amplitude[i];
            // trapezoidal step for each integrand
            iae  += 0.5 * (Math.Abs(e0)               + Math.Abs(e1))               * dt;
            ise  += 0.5 * (e0 * e0                    + e1 * e1)                    * dt;
            itae += 0.5 * (time[i - 1] * Math.Abs(e0) + time[i] * Math.Abs(e1))     * dt;
        }
        return (iae, ise, itae);
    }

    /// True if the simulated response stayed bounded (didn't blow up under the given
    /// gains) — a practical stand-in for the training data's Is_Stable flag. The
    /// bound scales with the reference so a large setpoint isn't misread as unstable.
    public static bool IsResponseStable(double[] amplitude, double reference = 1.0) =>
        amplitude.All(a => !double.IsNaN(a) && !double.IsInfinity(a)
            && Math.Abs(a) < 1000.0 * Math.Max(1.0, Math.Abs(reference)));
}
