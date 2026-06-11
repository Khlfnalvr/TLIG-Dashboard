using System.ComponentModel;

namespace TLIGDashboard.Services;

public enum SimulationType
{
    Flow,
    Level,
    Temperature
}

/// <summary>Per-system physical constants and defaults.</summary>
public sealed record SystemConfig(
    double K,
    double Tau,
    string Unit,
    double DefaultSetpoint,
    string Description,
    string Icon,         // Segoe Fluent Icons glyph
    string PlantName
);

/// <summary>
/// Singleton that tracks which process simulation (Flow / Level / Temperature)
/// is currently active. All HMI pages subscribe to <see cref="SimulationTypeChanged"/>
/// and update their labels, units, and default values accordingly.
/// </summary>
public sealed class SimulationTypeService : INotifyPropertyChanged
{
    public static SimulationTypeService Instance { get; } = new();
    private SimulationTypeService() { }

    // ── Per-system config constants ──────────────────────────────────────────
    public static IReadOnlyDictionary<SimulationType, SystemConfig> Configs { get; } =
        new Dictionary<SimulationType, SystemConfig>
        {
            [SimulationType.Flow] = new(
                K: 1.2, Tau: 5,  Unit: "L/min", DefaultSetpoint: 10,
                Description: "Control laju aliran fluida dalam pipa",
                Icon: "",   // Fluent: Flow
                PlantName: "Flow Plant"),

            [SimulationType.Level] = new(
                K: 2.0, Tau: 10, Unit: "cm",    DefaultSetpoint: 50,
                Description: "Kontrol ketinggian cairan dalam tangki",
                Icon: "",   // Fluent: Water
                PlantName: "Tank / Level Plant"),

            [SimulationType.Temperature] = new(
                K: 0.8, Tau: 30, Unit: "°C",    DefaultSetpoint: 60,
                Description: "Regulasi suhu pada heat exchanger",
                Icon: "",   // Fluent: Temperature
                PlantName: "Heat Exchanger"),
        };

    // ── State ────────────────────────────────────────────────────────────────
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<SimulationType>? SimulationTypeChanged;

    private SimulationType _currentType = SimulationType.Flow;

    public SimulationType CurrentType
    {
        get => _currentType;
        set
        {
            if (_currentType == value) return;
            _currentType = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentType)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Config)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProcessVariableUnit)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SetpointLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlantLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TransferFunctionText)));
            SimulationTypeChanged?.Invoke(this, value);
        }
    }

    /// <summary>Shorthand for the active system's config.</summary>
    public SystemConfig Config => Configs[_currentType];

    public string DisplayName => _currentType switch
    {
        SimulationType.Flow        => "Flow",
        SimulationType.Level       => "Level",
        SimulationType.Temperature => "Temperature",
        _                          => "Flow"
    };

    public string ProcessVariableUnit => Config.Unit;

    public string SetpointLabel => _currentType switch
    {
        SimulationType.Flow        => $"Flow Setpoint ({Config.Unit})",
        SimulationType.Level       => $"Level Setpoint ({Config.Unit})",
        SimulationType.Temperature => $"Temp Setpoint ({Config.Unit})",
        _                          => "Setpoint"
    };

    public string PlantLabel => Config.PlantName;

    /// <summary>Transfer function string with actual K and tau values.</summary>
    public string TransferFunctionText =>
        $"G(s) = {Config.K:0.#} / ({Config.Tau}s + 1)";

    /// <summary>Ordered list for UI pickers.</summary>
    public static IReadOnlyList<SimulationType> AllTypes { get; } =
        new[] { SimulationType.Flow, SimulationType.Level, SimulationType.Temperature };
}
