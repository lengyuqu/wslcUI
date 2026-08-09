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
}
