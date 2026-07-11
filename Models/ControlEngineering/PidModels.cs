using System;

namespace TLIGDashboard.Models.ControlEngineering;

public class PidInput
{
    public float Kp { get; set; }
    public float Ki { get; set; }
    public float Kd { get; set; }
}

public class PidPrediction
{
    public float Kp { get; set; }
    public float Ki { get; set; }
    public float Kd { get; set; }
}

public class SimulationResult
{
    public double[] Time { get; set; } = Array.Empty<double>();
    public double[] Amplitude { get; set; } = Array.Empty<double>();
    public PidPrediction UsedParameters { get; set; } = new();
}

/// Output of PidMetricsRegressor — an instant ML.NET estimate of step-response
/// metrics straight from Kp/Ki/Kd, independent of PidSimulator's exact RK4 curve.
public class PidMetricsPrediction
{
    public float Overshoot { get; set; }
    public float RiseTime { get; set; }
    public float SettlingTime { get; set; }
    public float SteadyStateError { get; set; }
}
