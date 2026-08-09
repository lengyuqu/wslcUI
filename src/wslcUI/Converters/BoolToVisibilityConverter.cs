using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace wslcUI.Converters;

/// <summary>
/// bool → Visibility。默认 true→Visible；传 <c>invert</c> 参数则取反
/// （用于列表的空状态：数据存在时隐藏占位提示）。
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isTrue = value is bool b && b;
        var invert = parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase);
        var visible = invert ? !isTrue : isTrue;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
