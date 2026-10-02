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

        // 仓库登录的密码输入：PasswordBox 没有可绑定的 Password 属性，
        // 只能代码建一个小对话框。返回值直接交给 ViewModel 转进 --password-stdin，
        // 不落盘、不进日志、不留在 ViewModel 状态里。
        ViewModel.PromptPassword = PromptPasswordDialog;

        // 端点面板的平台动作注入：ViewModel 不直接引用 Clipboard / Launcher，
        // 否则它就绑死了 WinRT、无法单测。这里做回调中转。
        ViewModel.CopyText = text =>
        {
            var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
            pkg.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
        };
        ViewModel.OpenUrl = url =>
        {
            // 打开外部程序走shell 执行而不是 WinRT Launcher：
            // 本项目是 **unpackaged**（WindowsPackageType=None），没有包身份时
            // Launcher.LaunchUri 的默认处理器解析并不可靠，而 UseShellExecute
            // 交给Shell 直接命中关联程序，行为与在资源管理器里双击一致。
            try
            {
                using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true,
                });
                if (proc is null) throw new InvalidOperationException("Shell 未返回进程。");
            }
            catch (System.Exception ex)
            {
                ViewModel.InfoMessage = $"无法打开 {url}：{ex.Message}";
                ViewModel.InfoSeverity = InfoBarSeverity.Warning;
                ViewModel.IsInfoBarOpen = true;
            }
        };
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
    /// 端点行「复制」：把宿主的 ip:port 复制到剪贴板。
    /// 行数据从 <see cref="FrameworkElement.DataContext"/> 取 —— DataTemplate 内
    /// DataContext 就是该行的 <see cref="EndpointInfo"/> 本身。
    /// </summary>
    private void CopyEndpoint_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EndpointInfo ep)
            ViewModel.CopyEndpointAddressCommand.Execute(ep);
    }

    private void OpenEndpoint_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EndpointInfo ep)
            ViewModel.OpenEndpointCommand.Execute(ep);
    }

    /// <summary>
    /// 容器列表选择变化。多选模式下把选中项名字同步给 ViewModel（批量操作的唯一事实源）。
    /// 顺带在这里切<b>SelectionMode</b>：单选时与从前行为一致，多选时才允许多选。
    /// </summary>
    private void ContainersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionMode 只能在 SelectionMode 变化时切一次，否则会递归触发本事件。
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

    /// <summary>
    /// 弹出密码输入框并返回用户输入的明文；取消返回 null。
    /// 刻意用 ContentDialog 而非 PasswordBox 绑定：密码只在栈上存在，
    /// 不进任何绑定目标、不进 ViewModel 属性、不可能被 x:Bind 缓存。
    /// </summary>
    private string? PromptPasswordDialog()
    {
        var box = new PasswordBox
        {
            PlaceholderText = "密码或 PAT（只经 stdin 传给 wslc，不保存）",
            MinWidth = 320,
        };

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "登录镜像仓库",
            Content = box,
            PrimaryButtonText = "登录",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = dialog.ShowAsync().GetAwaiter().GetResult();
        return result == ContentDialogResult.Primary ? box.Password : null;
    }

    /// <summary>
    /// 进入/退出多选模式。走 VM 命令而非直接改 IsSelectMode，
    /// 以保留「退出时清空勾选」的清理逻辑。
    /// </summary>
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
