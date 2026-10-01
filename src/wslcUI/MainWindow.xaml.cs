using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WinRT.Interop;
using wslcUI.Models;
using wslcUI.Services;
using wslcUI.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace wslcUI;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        this.InitializeComponent();
        ViewModel = App.Services.GetRequiredService<MainViewModel>();
        // Hand the owner HWND to the VM so its FolderPicker can parent correctly.
        ViewModel.OwnerHandle = WindowNative.GetWindowHandle(this);
        // Set window size (WinUI 3 Window has no Height/Width XAML attributes)
        this.AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 860));
        // Auto-load data once the window is activated (Window has no Loaded event).
        this.Activated += MainWindow_Activated;
        // 关窗即释放 DI 容器：WslcSdkClient.Dispose 会 Terminate SDK Session，
        // 否则进程退出时 session storage（%LOCALAPPDATA%\wslcUI\session 下的
        // VM / 镜像资源）不会被清理，一直残留。
        this.Closed += (_, _) => App.Services.Dispose();

        // 恢复上次保存的主题（用户偏好），无保存值时保持 VM 默认 = ElementTheme.Default
        var saved = UserSettings.LoadTheme();
        if (saved is not null)
        {
            // ApplicationTheme(Light=0,Dark=1) 与 ElementTheme(Default=0,Light=1,Dark=2)
            // 枚举值不一致，不能直接强转；按语义映射。
            ViewModel.Theme = saved.Value == ApplicationTheme.Dark
                ? ElementTheme.Dark
                : ElementTheme.Light;
        }

        // 主题跟随 VM：用户在顶栏点「主题」翻转 VM.Theme，RootRequestedTheme 同步生效。
        // Grid.RequestedTheme 是 FrameworkElement 上的合法属性（WinUI 3）。
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Root.RequestedTheme = ViewModel.Theme;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Theme))
        {
            Root.RequestedTheme = ViewModel.Theme;
            // ElementTheme.Default 含义是「跟系统」，无对应 ApplicationTheme 值，不持久化。
            if (ViewModel.Theme == ElementTheme.Default) return;
            // ElementTheme(Default=0,Light=1,Dark=2) 与 ApplicationTheme(Light=0,Dark=1)
            // 枚举值不一致，不能强转（强转会把 Light 存成 Dark、Dark 存成非法值 2）；
            // 按语义映射。走到这里只可能是 Light/Dark。
            UserSettings.SaveTheme(ViewModel.Theme == ElementTheme.Light
                ? ApplicationTheme.Light
                : ApplicationTheme.Dark);
        }
        else if (e.PropertyName == nameof(MainViewModel.CurrentPage))
        {
            // 页面也可由非导航入口切换（如容器空态的「去拉取镜像」按钮），
            // 此时 NavigationView 高亮要跟上，否则侧栏停留在旧页。
            SyncNavSelection();
        }
    }

    /// <summary>把 VM.CurrentPage 反向同步到 NavigationView 选中项。</summary>
    private void SyncNavSelection()
    {
        var tag = ViewModel.CurrentPage.ToString();
        if (NavView.SelectedItem is NavigationViewItem current &&
            string.Equals(current.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            return; // 已是目标项，避免无谓的 SelectionChanged 回环

        foreach (var mi in NavView.MenuItems)
        {
            if (mi is NavigationViewItem nvi &&
                string.Equals(nvi.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                // 触发 SelectionChanged → NavigateCommand，但 CurrentPage 已是同一值，
                // VM 的 setter 不重复通知，无死循环。
                NavView.SelectedItem = nvi;
                return;
            }
        }
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs e)
    {
        this.Activated -= MainWindow_Activated;
        await ViewModel.RefreshCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// 打开终端。详情面板和容器列表的「终端」按钮共享同一个入口。
    /// 开窗前做 ConPTY 健康预检（带 5 分钟缓存）：机器级 attach 故障时
    /// （子进程 0xC0000142 启动即死）弹诊断而不是开一个永远黑屏的死窗口。
    /// </summary>
    private async void OpenTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedContainer is null) return;
        if (!await EnsureConPtyHealthyAsync()) return;
        var term = new TerminalWindow(ViewModel.SelectedContainer.Name);
        term.Activate();
    }

    /// <summary>
    /// 容器行双击：直接打开终端（与 docker desktop 一致）。
    /// SelectedItem 已通过 TwoWay 绑定自动同步，无需从 sender 二次取数据。
    /// </summary>
    private async void ContainersList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel.SelectedContainer is null) return;
        if (!await EnsureConPtyHealthyAsync()) return;
        var term = new TerminalWindow(ViewModel.SelectedContainer.Name);
        term.Activate();
    }

    /// <summary>
    /// 附加到运行中容器的前台进程（`wslc attach &lt;name&gt;`），区别于 exec 的
    /// 「启动一个新进程」。按钮可见性由 <c>CanAttachContainer</c> 守门（仅 Running），
    /// 这里仍兜底校验一次，处理 0xC0000142 等机器级故障。
    /// </summary>
    private async void Attach_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedContainer is null) return;
        if (!ViewModel.SelectedContainer.IsRunning) return;
        if (!await EnsureConPtyHealthyAsync()) return;
        var term = new TerminalWindow(ViewModel.SelectedContainer.Name, TerminalWindow.Mode.Attach);
        term.Activate();
    }

    /// <summary>
    /// ConPTY 健康预检兜底：不健康时弹 ContentDialog 说明原因。
    /// 探测要起一次 conhost 并等子进程退出（最长 5s），放到线程池执行，
    /// 不阻塞 UI 线程；await 续体经 DispatcherQueueSynchronizationContext
    /// 回到 UI 线程，弹对话框安全。
    /// </summary>
    private async Task<bool> EnsureConPtyHealthyAsync()
    {
        var (healthy, exitCode) = await Task.Run(wslcUI.Terminal.PseudoConsole.ProbeHealth);
        if (healthy) return true;

        // 0xFFFFFFFF = 探针自身失败（非子进程退出码），文案区分开避免误导。
        var reason = exitCode == unchecked((int)0xFFFFFFFF)
            ? "ConPTY 探测过程自身失败（无法创建伪控制台或启动测试进程）"
            : $"测试子进程退出码 0x{unchecked((uint)exitCode):X8}";
        var dialog = new ContentDialog
        {
            Title = "终端功能暂不可用",
            Content = $"此机器的 ConPTY 组件存在系统故障（{reason}），" +
                      "打开的终端窗口将无法显示任何输出。\n\n" +
                      "建议：安装 Windows 更新后重试；也可把此退出码反馈给系统管理员。",
            CloseButtonText = "知道了",
            XamlRoot = Content.XamlRoot,
        };
        _ = dialog.ShowAsync();
        return false;
    }

    /// <summary>
    /// 打开容器内文件浏览窗口。与 exec 同源限制：`wslc exec` 需要容器**正在运行**，
    /// 所以按钮的可用性跟「附加到容器」共用 <c>CanAttachContainer</c>（= 选中且 Running）。
    /// </summary>
    private void OpenFiles_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedContainer is null) return;
        if (!ViewModel.SelectedContainer.IsRunning) return;
        var files = new ContainerFilesWindow(ViewModel.SelectedContainer.Name);
        files.Activate();
    }

    /// <summary>
    /// 把 NavigationView 的选中项映射成 VM.CurrentPage。
    /// 用 Tag("Containers" / "Images" / ...) → ViewModel.NavigateCommand 解析。
    /// 不要直接写 VM.CurrentPage —— 走命令保留 OnCurrentPageChanged 的副作用（清搜索、刷新过滤）。
    /// </summary>
    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;
        var tag = item.Tag as string;
        if (string.IsNullOrEmpty(tag)) return;
        if (System.Enum.TryParse<ResourcePage>(tag, ignoreCase: true, out _))
            ViewModel.NavigateCommand.Execute(tag);
    }
}
