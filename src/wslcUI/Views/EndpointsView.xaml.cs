using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using wslcUI.Models;
using wslcUI.ViewModels;

namespace wslcUI.Views;

/// <summary>
/// 端点页：把所有运行中容器已发布的端口聚成一张表，支持复制地址 / 打开浏览器。
///
/// <para>
/// 行内动作从 <see cref="FrameworkElement.DataContext"/> 取数据 —— DataTemplate 里
/// DataContext 就是该行的 <see cref="EndpointInfo"/> 本身；用 <c>x:Bind</c> 整体绑定
/// CommandParameter 在 WinUI 3 不被支持，所以走 Click + DataContext。
/// </para>
/// </summary>
public sealed partial class EndpointsView : UserControl
{
    public MainViewModel ViewModel { get; }

    public EndpointsView()
    {
        ViewModel = App.Services.GetRequiredService<MainViewModel>();
        InitializeComponent();
    }

    /// <summary>行内「复制」：把宿主的 ip:port 复制到剪贴板。</summary>
    private void CopyEndpoint_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EndpointInfo ep)
            ViewModel.CopyEndpointAddressCommand.Execute(ep);
    }

    /// <summary>行内「打开」：用默认浏览器访问该地址（unpackaged 下走 Shell 而非 Launcher）。</summary>
    private void OpenEndpoint_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EndpointInfo ep)
            ViewModel.OpenEndpointCommand.Execute(ep);
    }
}
