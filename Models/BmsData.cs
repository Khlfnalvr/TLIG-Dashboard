using System.ComponentModel;
using System.Runtime.CompilerServices;
using TLIGDashboard.Services;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace TLIGDashboard.Models;

public enum LogFormat { Csv, Tsv, Excel, Json }

public enum AlertSeverity { Info, Warning, Error, Alert }

public class AlertRecord : INotifyPropertyChanged
{
    public DateTime     Timestamp { get; }
    public AlertSeverity Severity { get; }
    private readonly string _title;
    private readonly string _body;
    private readonly string? _titleKey;
    private readonly string? _bodyKey;
    private readonly object[] _bodyArgs;

    public string Title => _titleKey is null
        ? _title
        : LocalizationManager.Instance.Get(_titleKey);

    public string Body => _bodyKey is null
        ? _body
        : LocalizationManager.Instance.Format(_bodyKey, _bodyArgs);

    public string TimeText => Timestamp.ToString("HH:mm:ss  dd/MM");

    // Segoe MDL2 Assets glyphs
    public string SeverityIcon => Severity switch
    {
        AlertSeverity.Info    => "",  // InfoSolid
        AlertSeverity.Warning => "",  // Warning
        AlertSeverity.Error   => "",  // StatusCircleErrorX
        AlertSeverity.Alert   => "",  // BellBadge
        _                     => "",
    };

    public SolidColorBrush SeverityColorBrush => new(Severity switch
    {
        AlertSeverity.Info    => Color.FromArgb(0xFF, 0x00, 0x78, 0xD4),  // accent blue
        AlertSeverity.Warning => Color.FromArgb(0xFF, 0xCC, 0x6E, 0x00),  // amber
        AlertSeverity.Error   => Color.FromArgb(0xFF, 0xC4, 0x26, 0x2F),  // red
        AlertSeverity.Alert   => Color.FromArgb(0xFF, 0xFF, 0x88, 0x00),  // orange
        _                     => Color.FromArgb(0xFF, 0x80, 0x80, 0x80),
    });

    public event PropertyChangedEventHandler? PropertyChanged;

    public AlertRecord(DateTime ts, string title, string body,
                       AlertSeverity severity = AlertSeverity.Alert)
    {
        Timestamp = ts;
        _title    = title;
        _body     = body;
        Severity  = severity;
        _bodyArgs = [];
    }

    private AlertRecord(DateTime ts, string titleKey, string bodyKey,
                        object[] bodyArgs, AlertSeverity severity)
    {
        Timestamp = ts;
        _title = "";
        _body = "";
        _titleKey = titleKey;
        _bodyKey = bodyKey;
        _bodyArgs = bodyArgs;
        Severity = severity;
    }

    public static AlertRecord Localized(
        DateTime ts,
        string titleKey,
        string bodyKey,
        object[] bodyArgs,
        AlertSeverity severity = AlertSeverity.Alert) =>
        new(ts, titleKey, bodyKey, bodyArgs, severity);

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Body));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}


public enum CellState { Normal, Low, Undervoltage, Overvoltage }

public class CellStatus
{
    public int Index { get; set; }
    public double Voltage { get; set; }
    public CellState State { get; set; }
    public bool IsBalancing { get; set; }
}

public class BmsData
{
    public double[] Cells { get; set; } = new double[20];
    public double[] Temps { get; set; } = new double[10];
    public double Soc { get; set; }
    public double Current { get; set; }
    public double PackVoltage { get; set; }
    public string Status { get; set; } = "idle";
    public bool[] Balancing { get; set; } = new bool[20];
}

public class BmsConfig
{
    public double NominalCapacityAh { get; set; } = 20.0;   // 20 Ah → shown as mAh on dashboard
    public double MaxDod { get; set; } = 80;
    public double MaxChargeCurrent { get; set; } = 20;
    public double MaxDischargeCurrent { get; set; } = 40;
    public double OvervoltageThreshold { get; set; } = 4.20;
    public double HighVoltageWarning { get; set; } = 4.10;
    public double UndervoltageThreshold { get; set; } = 2.80;
    public double LowVoltageWarning { get; set; } = 3.00;
    public double OverTempWarning { get; set; } = 60;
    public double OverTempCutoff { get; set; } = 70;
    public double BalancingStartDelta { get; set; } = 0.020;
    public double BalancingStopDelta { get; set; } = 0.005;
}
