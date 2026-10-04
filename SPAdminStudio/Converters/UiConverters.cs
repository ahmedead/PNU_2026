using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace SPAdminStudio.Converters
{
    public class InverseBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b) return !b;
            return false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b) return !b;
            return false;
        }
    }

    public class NullToVisibilityConverter : IValueConverter
    {
        public bool Invert { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool isNull = value == null;
            if (Invert)
            {
                // Invert = true ("NotNullToVis"): Visible when value is NOT null, Collapsed when null
                return isNull ? Visibility.Collapsed : Visibility.Visible;
            }
            else
            {
                // Invert = false ("NullToVis"): Visible when value IS null, Collapsed when NOT null
                return isNull ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class IntEqualsToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int intVal && parameter is string paramStr && int.TryParse(paramStr, out var targetVal))
            {
                return intVal == targetVal ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Binds an enum property to ComboBox.SelectedIndex. WPF does not convert int ↔ enum on its own,
    /// so without this the binding fails silently (nothing shown, selection never saved).
    /// </summary>
    public class EnumToIndexConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is Enum e ? System.Convert.ToInt32(e) : -1;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (value is int index && index >= 0 && enumType.IsEnum && Enum.IsDefined(enumType, index))
            {
                return Enum.ToObject(enumType, index);
            }
            return Binding.DoNothing;
        }
    }

    public class DeployStatusToBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush Green = new(Color.FromRgb(152, 195, 121));
        private static readonly SolidColorBrush Orange = new(Color.FromRgb(229, 192, 123));
        private static readonly SolidColorBrush Red = new(Color.FromRgb(224, 108, 117));
        private static readonly SolidColorBrush Blue = new(Color.FromRgb(97, 175, 239));
        private static readonly SolidColorBrush Gray = new(Color.FromRgb(142, 142, 158));

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
        {
            Models.DeployStatus.New => Blue,
            Models.DeployStatus.Deploying => Blue,
            Models.DeployStatus.Overwrite => Orange,
            Models.DeployStatus.Warning => Orange,
            Models.DeployStatus.Done => Green,
            Models.DeployStatus.Blocked => Red,
            Models.DeployStatus.Failed => Red,
            _ => Gray
        };

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class StatusToBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush Green = new(Color.FromRgb(152, 195, 121)); // Approved / Checked in
        private static readonly SolidColorBrush Orange = new(Color.FromRgb(229, 192, 123)); // Checked out / Draft
        private static readonly SolidColorBrush Red = new(Color.FromRgb(224, 108, 117)); // Denied
        private static readonly SolidColorBrush Blue = new(Color.FromRgb(97, 175, 239)); // Pending
        private static readonly SolidColorBrush Gray = new(Color.FromRgb(171, 178, 191)); // Default

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string s)
            {
                if (s.Contains("Approved", StringComparison.OrdinalIgnoreCase) || s.Contains("Checked In", StringComparison.OrdinalIgnoreCase))
                    return Green;
                if (s.Contains("Checked Out", StringComparison.OrdinalIgnoreCase) || s.Contains("Draft", StringComparison.OrdinalIgnoreCase))
                    return Orange;
                if (s.Contains("Denied", StringComparison.OrdinalIgnoreCase))
                    return Red;
                if (s.Contains("Pending", StringComparison.OrdinalIgnoreCase))
                    return Blue;
            }
            return Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
