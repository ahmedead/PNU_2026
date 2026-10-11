using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SEOAuditStudio.Models;

namespace SEOAuditStudio.Converters
{
    internal static class Palette
    {
        public static readonly SolidColorBrush Good = Freeze("#2BB673");
        public static readonly SolidColorBrush Medium = Freeze("#F2994A");
        public static readonly SolidColorBrush Bad = Freeze("#E5484D");
        public static readonly SolidColorBrush Info = Freeze("#3E8EF7");
        public static readonly SolidColorBrush Muted = Freeze("#8E8E9E");

        public static SolidColorBrush Freeze(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }
    }

    public class InverseBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b ? !b : false;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b ? !b : false;
    }

    /// <summary>Visible when the value is non-null / non-empty / true / non-zero. Invert flips it.</summary>
    public class HasValueToVisibilityConverter : IValueConverter
    {
        public bool Invert { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool has = value switch
            {
                null => false,
                bool b => b,
                string s => s.Length > 0,
                int i => i != 0,
                System.Collections.ICollection c => c.Count > 0,
                _ => true
            };
            return has ^ Invert ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>Visible when the int value equals the parameter (used to show the selected section).</summary>
    public class IntEqualsToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is int i && int.TryParse(parameter?.ToString(), out var p) && i == p ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>Two-way int ↔ bool for radio buttons: checked when the value equals the parameter.</summary>
    public class IntEqualsToBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is int i && int.TryParse(parameter?.ToString(), out var p) && i == p;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is true && int.TryParse(parameter?.ToString(), out var p) ? p : Binding.DoNothing;
    }

    public class SeverityToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
        {
            Severity.Error => Palette.Bad,
            Severity.Warning => Palette.Medium,
            Severity.Notice => Palette.Info,
            _ => Palette.Muted
        };

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>0-100 score → green / orange / red.</summary>
    public class ScoreToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not int s) return Palette.Muted;
            return s >= 80 ? Palette.Good : s >= 50 ? Palette.Medium : Palette.Bad;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>HTTP status → colour (2xx green, 3xx blue, 4xx orange, 5xx/failed red).</summary>
    public class StatusToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not int s) return Palette.Muted;
            return s switch
            {
                0 => Palette.Bad,
                >= 500 => Palette.Bad,
                >= 400 => Palette.Medium,
                >= 300 => Palette.Info,
                _ => Palette.Good
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    public class PassedToBrushConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length >= 2 && values[0] is bool passed && values[1] is Severity sev)
                return passed ? Palette.Good : sev == Severity.Error ? Palette.Bad : sev == Severity.Warning ? Palette.Medium : Palette.Info;
            return Palette.Muted;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>"+3" (worse) red, "-3" (better) green; parameter "invert" for metrics where up is good (Site Health).</summary>
    public class DeltaToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var s = value as string ?? "";
            bool upIsGood = parameter as string == "invert";
            if (s.StartsWith('+')) return upIsGood ? Palette.Good : Palette.Bad;
            if (s.StartsWith('-')) return upIsGood ? Palette.Bad : Palette.Good;
            return Palette.Muted;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>Ratio (0-1) × available width (parameter, default 260) for simple bar charts.</summary>
    public class RatioToWidthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double max = double.TryParse(parameter?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var m) ? m : 260;
            double ratio = value switch { double d => d, int i => i / 100.0, _ => 0 };
            return Math.Max(2, ratio * max);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    public class HexToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is string hex ? Palette.Freeze(hex) : Palette.Muted;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
