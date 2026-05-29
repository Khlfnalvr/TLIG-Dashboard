namespace TLIGDashboard.Services;

public static class UnitFormatter
{
    public const string Missing = "\u2014";

    private const string Degree = "\u00B0";

    public static string NormalizeTemperatureUnit(string? unit) =>
        string.Equals(unit, "F", StringComparison.OrdinalIgnoreCase) ? "F" : "C";

    public static string NormalizeVoltageUnit(string? unit) =>
        string.Equals(unit, "mV", StringComparison.OrdinalIgnoreCase) ? "mV" : "V";

    public static string NormalizeCapacityUnit(string? unit) =>
        string.Equals(unit, "Ah", StringComparison.OrdinalIgnoreCase) ? "Ah" : "mAh";

    public static string TemperatureSymbol(string? unit) =>
        NormalizeTemperatureUnit(unit) == "F" ? $"{Degree}F" : $"{Degree}C";

    public static string VoltageSymbol(string? unit) =>
        NormalizeVoltageUnit(unit) == "mV" ? "mV" : "V";

    public static string CapacitySymbol(string? unit) =>
        NormalizeCapacityUnit(unit) == "Ah" ? "Ah" : "mAh";

    public static double ToDisplayTemperature(double celsius, string? unit) =>
        NormalizeTemperatureUnit(unit) == "F" ? celsius * 9.0 / 5.0 + 32.0 : celsius;

    public static double ToDisplayVoltage(double volts, string? unit) =>
        NormalizeVoltageUnit(unit) == "mV" ? volts * 1000.0 : volts;

    public static string FormatTemperature(double celsius, string? unit) =>
        $"{ToDisplayTemperature(celsius, unit):F1} {TemperatureSymbol(unit)}";

    public static string FormatPackVoltage(double volts, string? unit) =>
        NormalizeVoltageUnit(unit) == "mV"
            ? $"{volts * 1000.0:F0} mV"
            : $"{volts:F2} V";

    public static string FormatVoltage(double volts, string? unit, int vDecimals = 3, int mvDecimals = 1)
    {
        if (NormalizeVoltageUnit(unit) == "mV")
            return $"{(volts * 1000.0).ToString("F" + mvDecimals)} mV";

        return $"{volts.ToString("F" + vDecimals)} V";
    }

    public static string FormatVoltageValue(double volts, string? unit, int vDecimals = 3, int mvDecimals = 1)
    {
        if (NormalizeVoltageUnit(unit) == "mV")
            return (volts * 1000.0).ToString("F" + mvDecimals);

        return volts.ToString("F" + vDecimals);
    }

    public static string FormatVoltageDelta(double volts, string? unit) =>
        NormalizeVoltageUnit(unit) == "mV"
            ? $"{volts * 1000.0:F1} mV"
            : $"{volts:F3} V";

    public static string FormatCapacityFromAh(double ampHours, string? unit) =>
        NormalizeCapacityUnit(unit) == "Ah"
            ? $"{ampHours:F2} Ah"
            : $"{ampHours * 1000.0:N0} mAh";

    public static string MissingWithUnit(string unit) => $"{Missing} {unit}";
}
