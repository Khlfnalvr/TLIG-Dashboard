using System;
using System.Collections.Generic;
using System.Linq;
using TLIGDashboard.Models.ControlEngineering;

namespace TLIGDashboard.Services.ControlEngineering;

public class PidSimulator
{
    // Plant parameters: G(s) = 6.223 / (s^2 + 6.556s + 0.2516)
    private const double B = 6.223;
    private const double A1 = 6.556;
    private const double A0 = 0.2516;

    /// <summary>
    /// The plant's transfer function as text, built from the very B/A1/A0 the integrator
    /// uses. Handed to the LLM advisor so its description of the plant can never drift
    /// from what is actually simulated.
    /// </summary>
    public static string PlantTransferFunction => $"G(s) = {B} / (s^2 + {A1}*s + {A0})";

    /// <summary>
    /// Steady-state (DC) gain and dominant (slowest) time constant of the open-loop plant.
    /// The plant is heavily lag-dominant (large DC gain, long time constant), which is why
    /// generic PID reflexes mislead on it; giving the advisor these numbers lets it reason
    /// from the real plant rather than rules of thumb.
    /// </summary>
    public static (double dcGain, double dominantTimeConstant) PlantCharacteristics()
    {
        double dcGain = B / A0;
        // Poles of s^2 + A1*s + A0; the one nearer the origin dominates the response.
        double disc = Math.Sqrt(Math.Max(0, A1 * A1 - 4 * A0));
        double dominantPole = (-A1 + disc) / 2.0;   // less negative => slower => dominant
        double tau = dominantPole != 0 ? 1.0 / Math.Abs(dominantPole) : double.PositiveInfinity;
        return (dcGain, tau);
    }

    // Ceiling on simulated time for gains that settle very slowly (or never).
    private const double MaxDuration = 120.0;
    // Always simulate at least this long, so a fast response still yields a usable curve.
    private const double MinDuration = 5.0;
    // The run stops once the response has stopped moving: the trailing window's spread
    // and |dy| both fall under this fraction of the reference. 1e-5 was measured against
    // a 400s ground-truth run — it lands overshoot and steady-state error within 0.004%
    // at the app's default gains, where 1e-3 was still 0.41% off (the exponential tail
    // is still creeping when a looser tolerance calls it settled).
    private const double SettleTolFraction = 1e-5;
    private const double SettleWindowSeconds = 2.0;
    private const double SettleCheckSeconds = 0.5;

    /// <summary>
    /// Integrates the closed-loop step response with RK4.
    /// <paramref name="duration"/> &lt;= 0 (the default) runs until the response actually
    /// settles, capped at <see cref="MaxDuration"/>. A fixed window cannot work here: the
    /// dominant closed-loop pole moves with the gains, so the old hard-coded 10s stopped
    /// mid-transient at the app's own default gains (which need ~14s just to reach the 2%
    /// band) and reported the not-yet-final value as steady state — inventing a ~3%
    /// steady-state error on a plant whose integral action drives it to zero.
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

        double scale = Math.Max(1.0, Math.Abs(reference));
        double tol = SettleTolFraction * scale;
        double blowUp = 1e6 * scale;

        var time = new List<double>(Math.Min(maxSteps, 8192));
        var amplitude = new List<double>(Math.Min(maxSteps, 8192));

        // State: [y, dy, z] where z is integral of error
        double y = 0;
        double dy = 0;
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

            if (auto && i >= minSteps && i >= window && i % checkEvery == 0 &&
                IsWindowFlat(amplitude, window, tol) && Math.Abs(dy) < tol)
            {
                break;
            }

            // RK4 Integration
            double[] k1 = Derivatives(y, dy, z, reference, kp, ki, kd);
            double[] k2 = Derivatives(y + 0.5 * dt * k1[0], dy + 0.5 * dt * k1[1], z + 0.5 * dt * k1[2], reference, kp, ki, kd);
            double[] k3 = Derivatives(y + 0.5 * dt * k2[0], dy + 0.5 * dt * k2[1], z + 0.5 * dt * k2[2], reference, kp, ki, kd);
            double[] k4 = Derivatives(y + dt * k3[0], dy + dt * k3[1], z + dt * k3[2], reference, kp, ki, kd);

            y += (dt / 6.0) * (k1[0] + 2 * k2[0] + 2 * k3[0] + k4[0]);
            dy += (dt / 6.0) * (k1[1] + 2 * k2[1] + 2 * k3[1] + k4[1]);
            z += (dt / 6.0) * (k1[2] + 2 * k2[2] + 2 * k3[2] + k4[2]);
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

    private double[] Derivatives(double y, double dy, double z, double r, double kp, double ki, double kd)
    {
        // dy/dt = dy
        // d(dy)/dt = B * (Kp*(r-y) + Ki*z - Kd*dy) - A1*dy - A0*y
        // dz/dt = r - y

        double d2y = B * (kp * (r - y) + ki * z - kd * dy) - A1 * dy - A0 * y;
        double dz = r - y;

        return new[] { dy, d2y, dz };
    }

    /// Rise time (10%→90%), overshoot %, 2%-band settling time, steady-state error % —
    /// shared by the Dashboard's chart display and PidDiagnosisAgent's input features,
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

    /// True if the simulated response stayed bounded (didn't blow up under the given
    /// gains) — a practical stand-in for the training data's Is_Stable flag. The
    /// bound scales with the reference so a large setpoint isn't misread as unstable.
    public static bool IsResponseStable(double[] amplitude, double reference = 1.0) =>
        amplitude.All(a => !double.IsNaN(a) && !double.IsInfinity(a)
            && Math.Abs(a) < 1000.0 * Math.Max(1.0, Math.Abs(reference)));
}
