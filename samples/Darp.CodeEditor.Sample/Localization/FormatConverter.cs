using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace Sample.Localization;

/// <summary>
/// Fills in a text with placeholders in XAML: the first value of a <c>MultiBinding</c> is the text, such as
/// <see cref="Strings.CaretPosition"/>, the others are its arguments.
/// </summary>
public sealed class FormatConverter : IMultiValueConverter
{
    public static FormatConverter Instance { get; } = new();

    /// <inheritdoc />
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count == 0 || values[0] is not string text || values.Any(value => value is UnsetValueType))
            return AvaloniaProperty.UnsetValue;
        return Strings.Current.Format(text, [.. values.Skip(1)]);
    }
}
