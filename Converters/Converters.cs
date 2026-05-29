using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace TLIGDashboard.Converters;

internal static class CachedBrushes
{
    internal static readonly SolidColorBrush Transparent
        = new(Colors.Transparent);

    internal static readonly SolidColorBrush TempCritical
        = new(Color.FromArgb(255, 220, 53, 69));
    internal static readonly SolidColorBrush TempWarn
        = new(Color.FromArgb(255, 255, 140, 0));

    internal static readonly SolidColorBrush StatusCharging
        = new(Color.FromArgb(255, 37, 198, 133));
    internal static readonly SolidColorBrush StatusDischarging
        = new(Color.FromArgb(255, 0, 120, 212));
    internal static readonly SolidColorBrush StatusNeutral
        = new(Color.FromArgb(255, 128, 128, 128));
    internal static readonly SolidColorBrush StatusFault
        = new(Color.FromArgb(255, 220, 53, 69));
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
