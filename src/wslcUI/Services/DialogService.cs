using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using wslcUI;

namespace wslcUI.Services;

/// <summary>
/// 基于 WinUI <see cref="ContentDialog"/> 的确认对话框实现。
/// 在 unpackaged WinUI 3 中 ContentDialog 必须设置 <c>XamlRoot</c>，
/// 这里直接从 <see cref="App.MainWindow"/> 的内容取。若窗口尚未就绪
/// （XamlRoot 为空）则退化为「直接继续」，避免阻断操作。
/// </summary>
public sealed class DialogService : IDialogService
{
    public async Task<bool> ConfirmAsync(string title, string message, string? primaryText = null, string? closeText = null)
    {
        var window = App.MainWindow;
        if (window?.Content is not FrameworkElement fe || fe.XamlRoot is null)
            return true; // 无 UI 上下文：退化为直接继续

        var dialog = new ContentDialog
        {
            XamlRoot = fe.XamlRoot,
            Title = title,
            Content = message,
            PrimaryButtonText = primaryText ?? "确定",
            CloseButtonText = closeText ?? "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }
}
