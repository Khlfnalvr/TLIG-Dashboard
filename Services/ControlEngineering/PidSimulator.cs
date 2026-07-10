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

    public SimulationResult SimulateStepResponse(PidPrediction pid, double reference = 1.0, double duration = 10.0, double dt = 0.01)
    {
        int steps = (int)(duration / dt);
        double[] time = new double[steps];
        double[] amplitude = new double[steps];

        // State: [y, dy, z] where z is integral of error
        double y = 0;
        double dy = 0;
        double z = 0;

        double kp = pid.Kp;
        double ki = pid.Ki;
        double kd = pid.Kd;

        for (int i = 0; i < steps; i++)
        {
            time[i] = i * dt;
            amplitude[i] = y;

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
            Time = time,
            Amplitude = amplitude,
            UsedParameters = pid
        };
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
    /// gains) — a practical stand-in for the training data's Is_Stable flag.
    public static bool IsResponseStable(double[] amplitude) =>
        amplitude.All(a => !double.IsNaN(a) && !double.IsInfinity(a) && Math.Abs(a) < 1000.0);
}
