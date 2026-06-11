using System;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace TLIGDashboard.ViewModels
{
    /// <summary>
    /// Determines form label based on whether Guid == Empty (new) or not (edit).
    /// Parameter: "Label Baru|Label Edit"
    /// </summary>
    public class GuidEmptyConverter : IValueConverter
    {
        public static readonly GuidEmptyConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var parts = (parameter?.ToString() ?? "Baru|Edit").Split('|');
            if (value is Guid g)
                return g == Guid.Empty ? parts[0] : (parts.Length > 1 ? parts[1] : parts[0]);
            return parts[0];
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();
    }
}

namespace TLIGDashboard.Converters
{
    using Microsoft.UI.Xaml;
    using Microsoft.UI.Xaml.Data;

    /// <summary>Null → Collapsed, not-null → Visible</summary>
    public class NotNullToVisibilityConverter : IValueConverter
    {
        public static readonly NotNullToVisibilityConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, string language)
            => value != null ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();
    }

    /// <summary>Bool → Collapsed if True (inverse BooleanToVisibility)</summary>
    public class InverseBoolToVisibilityConverter : IValueConverter
    {
        public static readonly InverseBoolToVisibilityConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, string language)
            => value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();
    }
}
