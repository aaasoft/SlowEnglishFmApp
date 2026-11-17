using System.Globalization;

namespace SlowEnglishFmApp.Converters;

/// <summary>
/// 布尔取反。用于「字幕关闭时才显示居中标题」这类互斥可见性：
/// 直接把 <c>ShowKaraoke</c> 取反绑定，省掉代码里再维护一份相反状态。
/// </summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type? targetType, object? parameter, CultureInfo? culture)
        => value is not true;

    public object? ConvertBack(object? value, Type? targetType, object? parameter, CultureInfo? culture)
        => value is not true;
}
