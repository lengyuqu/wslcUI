using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using wslcUI.ViewModels;

namespace wslcUI.Views;

/// <summary>
/// 右侧详情面板：按当前页切换内容（容器 / 镜像 / 网络 / 卷 / 统计 / 构建）。
///
/// <para>
/// 从 MainWindow.xaml 抽出来的最大一块（原 525 行）。三个按钮动作都走
/// <see cref="ContainerActions"/>，与容器列表页共用同一套窗口打开逻辑。
/// </para>
/// </summary>
public sealed partial class DetailPaneView : UserControl
{
    public MainViewModel ViewModel { get; }

    public DetailPaneView()
    {
        // 必须在 InitializeComponent 之前赋值：x:Bind 首次求值在它内部发生。
        ViewModel = App.Services.GetRequiredService<MainViewModel>();
        InitializeComponent();
    }

    /// <summary>
    /// 容器详情的分区切换。Tag 是四个固定字符串（Overview / Stats / Mounts / Actions），
    /// 未知值由 VM 回落到「概览」。
    ///
    /// <para>
    /// 不用双向绑定：<c>SelectorBar.SelectedItem</c> 是 <see cref="SelectorBarItem"/>
    /// 对象而不是索引/字符串，绑到 VM 属性要么引入转换器要么让 VM 持有控件引用，
    /// 两条都比一个事件处理器更差。
    /// </para>
    /// </summary>
    private void ContainerDetailTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        ViewModel.SelectContainerDetailTab(sender.SelectedItem?.Tag as string);
    }

    private async void OpenTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedContainer is null) return;
        await ContainerActions.OpenTerminalAsync(ViewModel.SelectedContainer.Name, XamlRoot);
    }

    private async void Attach_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedContainer is null) return;
        if (!ViewModel.SelectedContainer.IsRunning) return;
        await ContainerActions.OpenAttachAsync(ViewModel.SelectedContainer.Name, XamlRoot);
    }

    private void OpenFiles_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedContainer is null) return;
        if (!ViewModel.SelectedContainer.IsRunning) return;
        ContainerActions.OpenFiles(ViewModel.SelectedContainer.Name);
    }

    /// <summary>整页模式的「返回列表」：收起整页，详情回 380 侧栏。</summary>
    private void BackToList_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.IsDetailFullPage = false;
        ViewModel.DetailPaneWidth = 380;
    }
}
