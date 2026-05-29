using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using TLIGDashboard.Models;
using Windows.UI;

namespace TLIGDashboard.Converters;

// ── Cached brush instances (created once, reused forever) ──────────────────
internal static class CachedBrushes
{
    internal static readonly SolidColorBrush Transparent
        = new(Colors.Transparent);

    // Cell state — background (alpha 50)
    internal static readonly SolidColorBrush BgLow
        = new(Color.FromArgb(50, 255, 140, 0));
    internal static readonly SolidColorBrush BgUnder
        = new(Color.FromArgb(50, 220, 53, 69));
    internal static readonly SolidColorBrush BgOver
        = new(Color.FromArgb(50, 111, 66, 193));

    // Cell state — border (alpha 180)
    internal static readonly SolidColorBrush BdLow
        = new(Color.FromArgb(180, 255, 140, 0));
    internal static readonly SolidColorBrush BdUnder
        = new(Color.FromArgb(180, 220, 53, 69));
    internal static readonly SolidColorBrush BdOver
        = new(Color.FromArgb(180, 111, 66, 193));
    internal static readonly SolidColorBrush BdDefault
        = new(Color.FromArgb(30, 128, 128, 128));

    // Temperature
    internal static readonly SolidColorBrush TempCritical
        = new(Color.FromArgb(255, 220, 53, 69));
    internal static readonly SolidColorBrush TempWarn
        = new(Color.FromArgb(255, 255, 140, 0));

    // Status
    internal static readonly SolidColorBrush StatusCharging
        = new(Color.FromArgb(255, 37, 198, 133));
    internal static readonly SolidColorBrush StatusDischarging
        = new(Color.FromArgb(255, 0, 120, 212));
    internal static readonly SolidColorBrush StatusNeutral
        = new(Color.FromArgb(255, 128, 128, 128));
    internal static readonly SolidColorBrush StatusFault
        = new(Color.FromArgb(255, 220, 53, 69));
}

// ─────────────────────────────────────────────────────────────────────────────

public class CellStateToBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is CellState state
            ? state switch
            {
                CellState.Low         => CachedBrushes.BgLow,
                CellState.Undervoltage => CachedBrushes.BgUnder,
                CellState.Overvoltage  => CachedBrushes.BgOver,
                _                     => CachedBrushes.Transparent
            }
            : CachedBrushes.Transparent;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

public class CellStateToBorderConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is CellState state
            ? state switch
            {
                CellState.Low         => CachedBrushes.BdLow,
                CellState.Undervoltage => CachedBrushes.BdUnder,
                CellState.Overvoltage  => CachedBrushes.BdOver,
                _                     => CachedBrushes.BdDefault
            }
            : CachedBrushes.BdDefault;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool boolValue = value is bool b && b;
        bool invert = parameter is string s && s == "Invert";
        return (boolValue ^ invert) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

public class TempToForegroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is double temp)
        {
            if (temp >= 70) return CachedBrushes.TempCritical;
            if (temp >= 60) return CachedBrushes.TempWarn;
        }
        return CachedBrushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

public class StatusToForegroundConverter : IValueConverter
{
    // Case-insensitive Equals avoids the ToLowerInvariant() allocation that
    // ran on every property-change cycle when status text refreshed.
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string s)
        {
            if (s.Equals("charging",    StringComparison.OrdinalIgnoreCase)) return CachedBrushes.StatusCharging;
            if (s.Equals("discharging", StringComparison.OrdinalIgnoreCase)) return CachedBrushes.StatusDischarging;
            if (s.Equals("fault",       StringComparison.OrdinalIgnoreCase)) return CachedBrushes.StatusFault;
        }
        return CachedBrushes.StatusNeutral;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
