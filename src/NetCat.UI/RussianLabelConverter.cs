using System.Globalization;
using System.Windows.Data;
namespace NetCat.UI;
public sealed class RussianLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => NetCat.Core.RussianLabels.Of(value);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
