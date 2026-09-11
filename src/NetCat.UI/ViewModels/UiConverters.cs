using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace NetCat.UI.ViewModels
{
    public class NavIndexConverter : IValueConverter
    {
        public static readonly NavIndexConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int current && parameter != null)
            {
                int target = int.Parse(parameter.ToString()!);
                return current == target;
            }
            return false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b && b && parameter != null)
            {
                return int.Parse(parameter.ToString()!);
            }
            return Binding.DoNothing;
        }
    }

    public class NavVisibilityConverter : IValueConverter
    {
        public static readonly NavVisibilityConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int current && parameter != null)
            {
                int target = int.Parse(parameter.ToString()!);
                return current == target ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class BoolToVisibilityConverter : IValueConverter
    {
        public static readonly BoolToVisibilityConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b)
            {
                return b ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Visibility v)
            {
                return v == Visibility.Visible;
            }
            return false;
        }
    }
}
