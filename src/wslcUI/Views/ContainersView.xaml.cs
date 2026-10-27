using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using wslcUI.Models;
using wslcUI.ViewModels;

namespace wslcUI.Views;

/// <summary>
/// 容器页：列表 / 搜索 / 排序 / 多选批量 / 行内动作。
///
/// <para>
/// 从 MainWindow.xaml 拆出来的第一页。显隐仍由 <c>ViewModel.IsContainersPage</c>
/// 控制（P1 阶段只拆分文件，不改导航机制），因此 MainWindow 只需摆一个标签。
/// </para>
/// <para>
/// <c>ViewModel</c> 必须在 <see cref="UserControl.InitializeComponent"/> **之前**赋值：
/// x:Bind 的首次求值发生在 InitializeComponent 内部，晚赋值会直接 NRE。
/// </para>
/// </summary>
public sealed partial class ContainersView : UserControl
{
    public MainViewModel ViewModel { get; }

    public ContainersView()
    {
        ViewModel = App.Services.GetRequiredService<MainViewModel>();
        InitializeComponent();
    }

    /// <summary>行内「终端」：exec 一个交互式 shell。选中项由列表 TwoWay 绑定保证。</summary>
    private async void OpenTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedContainer is null) return;
        await ContainerActions.OpenTerminalAsync(ViewModel.SelectedContainer.Name, XamlRoot);
    }

    /// <summary>双击行直接开终端（与 Docker Desktop 一致）。</summary>
    private async void ContainersList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel.SelectedContainer is null) return;
        await ContainerActions.OpenTerminalAsync(ViewModel.SelectedContainer.Name, XamlRoot);
    }

    /// <summary>行内「文件」：浏览容器内文件系统。仅 Running 可用（按钮由 CanAttachContainer 守门）。</summary>
    private void OpenFiles_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedContainer is null) return;
        if (!ViewModel.SelectedContainer.IsRunning) return;
        ContainerActions.OpenFiles(ViewModel.SelectedContainer.Name);
    }

    /// <summary>
    /// 选中变化。多选模式下把选中项名字同步给 ViewModel（批量操作的唯一事实源），
    /// 顺带在这里切 SelectionMode —— 单选时行为与从前完全一致。
    /// </summary>
    private void ContainersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionMode 只能在模式真变化时切一次，否则会递归触发本事件。
        var want = ViewModel.IsSelectMode
            ? ListViewSelectionMode.Multiple
            : ListViewSelectionMode.Single;
        if (ContainersList.SelectionMode != want)
        {
            ContainersList.SelectionMode = want;
            return; // 模式切换会再次触发本事件，下一轮再同步名字
        }

        if (sender is not ListView list) return;
        var names = list.SelectedItems
            .OfType<ContainerInfo>()
            .Select(c => c.Name)
            .ToList();
        ViewModel.SetBulkSelection(names);
    }

    /// <summary>进入/退出多选。走 VM 命令而非直接改 IsSelectMode，以保留「退出时清空勾选」的清理逻辑。</summary>
    private void ToggleSelectMode_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ToggleSelectModeCommand.CanExecute(null))
            ViewModel.ToggleSelectModeCommand.Execute(null);
    }

    private void SelectAllVisible_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectAllVisibleCommand.CanExecute(null))
            ViewModel.SelectAllVisibleCommand.Execute(null);
    }
}
