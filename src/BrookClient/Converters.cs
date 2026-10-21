using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BrookClient;

/// <summary>non-null → true (or Visible).</summary>
public sealed class NotNullConverter : IValueConverter
{
    public static readonly NotNullConverter Instance = new(false);
    public static readonly NotNullConverter Visibility = new(true);

    private readonly bool _visibility;

    private NotNullConverter(bool visibility) => _visibility = visibility;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var notNull = value is not null && value is not string { Length: 0 };
        return _visibility ? notNull ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed : notNull;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public static readonly BoolToVisibilityConverter Normal = new(false);
    public static readonly BoolToVisibilityConverter Inverted = new(true);

    private readonly bool _invert;

    private BoolToVisibilityConverter(bool invert) => _invert = invert;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is true) != _invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
