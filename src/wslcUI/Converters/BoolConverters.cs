using Microsoft.UI.Xaml;

namespace wslcUI.Converters;

/// <summary>
/// Static helper for x:Bind function binding (avoids StaticResource converter
/// which requires FrameworkElement on Window root in WinUI 3).
/// </summary>
public static class BoolConverters
{
    public static Visibility InvertToVisibility(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility ToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>选中项为空时隐藏详情内容区（改用引导文案）。</summary>
    public static Visibility NullToVisibility(object? value) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility InvertNullToVisibility(object? value) =>
        value is null ? Visibility.Visible : Visibility.Collapsed;
}
