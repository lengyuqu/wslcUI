using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using wslcUI;

namespace wslcUI.Services;

/// <summary>
/// 基于 WinUI <see cref="ContentDialog"/> 的确认对话框实现。
/// 在 unpackaged WinUI 3 中 ContentDialog 必须设置 <c>XamlRoot</c>，
/// 这里直接从 <see cref="App.MainWindow"/> 的内容取。
/// </summary>
public sealed class DialogService : IDialogService
{
    public async Task<bool> ConfirmAsync(string title, string message, string? primaryText = null, string? closeText = null)
    {
        var window = App.MainWindow;
        // 取不到 UI 上下文时一律**不确认**（返回 false）：这里的 ConfirmAsync 守着
        // 全部破坏性操作（prune 卷 / 删全部未用镜像 / 删容器），"退化为直接继续"
        // 等于窗口尚未就绪那一刻跳过确认就真删数据。宁可让用户看着没反应、
        // 再点一次，也不能默默放行。
        if (window?.Content is not FrameworkElement fe || fe.XamlRoot is null)
            return false;

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
