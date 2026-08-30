using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Windows.Data;

namespace CloudLight.VideoCompressor.Infrastructure;

public sealed class EnumDescriptionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not Enum enumValue)
        {
            return value?.ToString() ?? string.Empty;
        }

        var field = enumValue.GetType().GetField(enumValue.ToString());
        return field?.GetCustomAttribute<DescriptionAttribute>()?.Description ?? enumValue.ToString();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}
