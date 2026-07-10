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
