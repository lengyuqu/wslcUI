using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using wslcUI.ViewModels;

namespace wslcUI.Views;

/// <summary>
/// 概览页（默认首屏）：四张计数卡 + 引擎健康卡 + CPU 合计 + 运行中容器性能表。
///
/// <para>
/// 最后一个从 MainWindow.xaml 拆出来的页面 —— 之前它一直整体留在壳文件里，
/// 「10 个页面各自成视图」当时漏了它。四张计数卡可点击跳转到对应资源页。
/// </para>
/// <para>
/// <c>ViewModel</c> 必须在 <see cref="UserControl.InitializeComponent"/> **之前**赋值：
/// x:Bind 的首次求值发生在 InitializeComponent 内部，晚赋值会直接 NRE。
/// </para>
/// </summary>
public sealed partial class DashboardView : UserControl
{
    public MainViewModel ViewModel { get; }

    public DashboardView()
    {
        ViewModel = App.Services.GetRequiredService<MainViewModel>();
        InitializeComponent();
    }

    /// <summary>
    /// 四张计数卡的点击跳转（运行中容器 / 镜像 / 网络 / 卷）。
    /// Tag 即 <c>ResourcePage</c> 枚举名，与 NavigationView 的 Tag 约定一致；
    /// 统一走 <c>NavigateCommand</c>，让非导航入口的切页也经过同一条
    /// <c>SyncNavSelection</c> 反向同步，避免高亮与实际页面脱节。
    /// </summary>
    private void DashboardCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag)
            ViewModel.NavigateCommand.Execute(tag);
    }
}
