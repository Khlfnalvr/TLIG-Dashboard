using System.IO.Ports;

namespace TLIGDashboard.Services.Transports;

/// <summary>
/// COM-port enumeration helper. Works around the trailing-NUL bug in
/// <see cref="SerialPort.GetPortNames"/> and sorts numerically so COM10
/// comes after COM2 instead of after COM1.
/// </summary>
internal static class SerialPortHelper
{
    public static SerialPortInfo[] EnumerateComChannels()
    {
        string[] names;
        try { names = SerialPort.GetPortNames(); }
        catch { return []; }

        var clean = names
            .Select(n => n?.TrimEnd('\0', ' ', '\r', '\n') ?? "")
            .Where(n => n.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => int.TryParse(n.AsSpan(3), out var num) ? num : int.MaxValue)
            .ToArray();

        return clean.Select(n => new SerialPortInfo(n, n)).ToArray();
    }
}
