using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using TLIGDashboard.Services;

namespace TLIGDashboard.Models;

/// <summary>
/// One loggable column. Supports enable/disable toggle and drag-to-reorder.
/// Key parsing is cached after first access so GetString/GetObject are O(1)
/// on every subsequent call (no repeated StartsWith/TryParse on hot path).
/// </summary>
public sealed class LogColumn : INotifyPropertyChanged
{
    public string Key   { get; init; } = "";
    public string Label { get; init; } = "";
    public string Group { get; init; } = "";
    public string DisplayLabel
    {
        get
        {
            EnsureParsed();
            var lang = LocalizationManager.Instance;
            return _kind switch
            {
                ColKind.Timestamp   => lang.Log_HdrTimestamp,
                ColKind.PackVoltage => lang.Get("Log_ColPackVoltage"),
                ColKind.Soc         => lang.Log_HdrSoc,
                ColKind.Current     => lang.Get("Log_ColCurrent"),
                ColKind.Status      => lang.Log_HdrStatus,
                ColKind.Cell        => lang.Format("Log_ColCell", _idx + 1),
                ColKind.Bal         => lang.Format("Log_ColBalancing", _idx + 1),
                ColKind.Temp        => lang.Format("Log_ColTemp", _idx + 1),
                _                   => Label,
            };
        }
    }

    private bool _isEnabled = true;
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public void RefreshLocalization() => OnPropertyChanged(nameof(DisplayLabel));

    // ── Cached key metadata (parsed once on first use) ────────────────────

    private enum ColKind : byte
    { Timestamp, PackVoltage, Soc, Current, Status, Cell, Bal, Temp, Unknown }

    private ColKind _kind = (ColKind)255; // sentinel = not parsed yet
    private int     _idx;

    private void EnsureParsed()
    {
        if (_kind != (ColKind)255) return;

        _kind = Key switch
        {
            "Timestamp"     => ColKind.Timestamp,
            "PackVoltage_V" => ColKind.PackVoltage,
            "SOC_pct"       => ColKind.Soc,
            "Current_A"     => ColKind.Current,
            "Status"        => ColKind.Status,
            _               => ColKind.Unknown
        };

        if (_kind == ColKind.Unknown)
        {
            if (Key.StartsWith("Cell", StringComparison.Ordinal) &&
                Key.EndsWith("_V", StringComparison.Ordinal) &&
                int.TryParse(Key.AsSpan(4, Key.Length - 6), out int ci) &&
                ci is >= 1 and <= 20)
            { _kind = ColKind.Cell; _idx = ci - 1; }

            else if (Key.StartsWith("Bal", StringComparison.Ordinal) &&
                     !Key.Contains('_') &&
                     int.TryParse(Key.AsSpan(3), out int bi) &&
                     bi is >= 1 and <= 20)
            { _kind = ColKind.Bal; _idx = bi - 1; }

            else if (Key.StartsWith("Temp", StringComparison.Ordinal) &&
                     Key.EndsWith("_C", StringComparison.Ordinal) &&
                     int.TryParse(Key.AsSpan(4, Key.Length - 6), out int ti) &&
                     ti is >= 1 and <= 10)
            { _kind = ColKind.Temp; _idx = ti - 1; }
        }
    }

    // ── Value extraction ──────────────────────────────────────────────────

    /// <summary>Formatted string for CSV / TSV / live-stream output.</summary>
    public string GetString(DateTime ts, BmsData d)
    {
        EnsureParsed();
        return _kind switch
        {
            ColKind.Timestamp   => ts.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            ColKind.PackVoltage => d.PackVoltage.ToString("F3", CultureInfo.InvariantCulture),
            ColKind.Soc         => d.Soc.ToString("F2", CultureInfo.InvariantCulture),
            ColKind.Current     => d.Current.ToString("F3", CultureInfo.InvariantCulture),
            ColKind.Status      => d.Status,
            ColKind.Cell        => d.Cells[_idx].ToString("F4", CultureInfo.InvariantCulture),
            ColKind.Bal         => d.Balancing[_idx] ? "1" : "0",
            ColKind.Temp        => d.Temps[_idx].ToString("F2", CultureInfo.InvariantCulture),
            _                   => ""
        };
    }

    /// <summary>Typed object for Excel / JSON output.</summary>
    public object GetObject(DateTime ts, BmsData d)
    {
        EnsureParsed();
        return _kind switch
        {
            ColKind.Timestamp   => ts.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            ColKind.PackVoltage => (object)d.PackVoltage,
            ColKind.Soc         => (object)d.Soc,
            ColKind.Current     => (object)d.Current,
            ColKind.Status      => d.Status,
            ColKind.Cell        => (object)d.Cells[_idx],
            ColKind.Bal         => (object)(d.Balancing[_idx] ? 1 : 0),
            ColKind.Temp        => (object)d.Temps[_idx],
            _                   => ""
        };
    }

    // ── Factory ───────────────────────────────────────────────────────────

    public static ObservableCollection<LogColumn> CreateDefaults()
    {
        var list = new ObservableCollection<LogColumn>
        {
            new() { Key = "Timestamp",     Label = "Timestamp",        Group = "Core" },
            new() { Key = "PackVoltage_V", Label = "Pack Voltage (V)", Group = "Core" },
            new() { Key = "SOC_pct",       Label = "SOC (%)",          Group = "Core" },
            new() { Key = "Current_A",     Label = "Current (A)",      Group = "Core" },
            new() { Key = "Status",        Label = "Status",           Group = "Core" },
        };
        for (int i = 1; i <= 20; i++)
            list.Add(new() { Key = $"Cell{i}_V", Label = $"Cell {i} (V)",   Group = "Cells" });
        for (int i = 1; i <= 20; i++)
            list.Add(new() { Key = $"Bal{i}",    Label = $"Balancing {i}",  Group = "Balancing" });
        for (int i = 1; i <= 10; i++)
            list.Add(new() { Key = $"Temp{i}_C", Label = $"Temp {i} (°C)", Group = "Temperatures" });
        return list;
    }
}
