using System.Globalization;

namespace SlowEnglishFmApp.Converters;

/// <summary>将字符串转换为 bool：非空且非空白为 true。用于按文本是否存在控制可见性。</summary>
public sealed class StringNotEmptyConverter : IValueConverter
{
    public object? Convert(object? value, Type? targetType, object? parameter, CultureInfo? culture)
        => !string.IsNullOrWhiteSpace(value as string);

    public object? ConvertBack(object? value, Type? targetType, object? parameter, CultureInfo? culture)
        => throw new NotSupportedException();
}
