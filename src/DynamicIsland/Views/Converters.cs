using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DynamicIsland.Views;

/// <summary>bool → Visibility; <see cref="Invert"/> shows the element when the value is false.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Binds a RadioButton to one value of an enum or int property: checked when equal, writes the value when checked.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not true || parameter is null)
            return Binding.DoNothing;
        var text = parameter.ToString()!;
        return targetType.IsEnum
            ? Enum.Parse(targetType, text)
            : System.Convert.ChangeType(text, targetType, CultureInfo.InvariantCulture);
    }
}
