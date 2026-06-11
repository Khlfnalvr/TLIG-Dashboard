using System.ComponentModel;

namespace TLIGDashboard.Services;

/// <summary>
/// Singleton that holds the most recent PID step-response metrics
/// produced by the Dashboard simulation. Any page can read these values
/// so the Challenge page can auto-populate student metric results.
/// </summary>
public sealed class PidMetricsService : INotifyPropertyChanged
{
    public static PidMetricsService Instance { get; } = new();
    private PidMetricsService() { }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? MetricsUpdated;

    private double? _riseTime;
    private double? _overshoot;
    private double? _settling;
    private double? _steadyStateError;

    public double? RiseTime
    {
        get => _riseTime;
        set { _riseTime = value; Notify(nameof(RiseTime)); }
    }
    public double? Overshoot
    {
        get => _overshoot;
        set { _overshoot = value; Notify(nameof(Overshoot)); }
    }
    public double? Settling
    {
        get => _settling;
        set { _settling = value; Notify(nameof(Settling)); }
    }
    public double? SteadyStateError
    {
        get => _steadyStateError;
        set { _steadyStateError = value; Notify(nameof(SteadyStateError)); }
    }

    /// <summary>True if at least one metric has been recorded.</summary>
    public bool HasData => _riseTime.HasValue || _overshoot.HasValue
                        || _settling.HasValue || _steadyStateError.HasValue;

    /// <summary>Bulk-update all four metrics at once (from simulation result).</summary>
    public void Update(double? riseTime, double? overshoot,
                       double? settling, double? steadyStateError)
    {
        _riseTime         = riseTime;
        _overshoot        = overshoot;
        _settling         = settling;
        _steadyStateError = steadyStateError;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        MetricsUpdated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Get value for a given TaskMetrics constant. Null if not available.</summary>
    public double? Get(string metric) => metric switch
    {
        TaskMetrics.RiseTime         => _riseTime,
        TaskMetrics.Overshoot        => _overshoot,
        TaskMetrics.Settling         => _settling,
        TaskMetrics.SteadyStateError => _steadyStateError,
        _                            => null
    };

    /// <summary>Unit string for display.</summary>
    public static string UnitOf(string metric) => metric switch
    {
        TaskMetrics.RiseTime  or TaskMetrics.Settling => "s",
        TaskMetrics.Overshoot or TaskMetrics.SteadyStateError => "%",
        _ => ""
    };

    private void Notify(string prop) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}
