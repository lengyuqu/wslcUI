using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WinRT.Interop;
using wslcUI.Models;
using wslcUI.Services;
using wslcUI.ViewModels;
using wslcUI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace wslcUI;

public sealed partial class MainWindow : Window
{
    /// <summary>
    /// 概览页四张计数卡的点击跳转（运行中容器 / 镜像 / 网络 / 卷）。
    /// Tag 即 <c>ResourcePage</c> 枚举名，与 NavigationView 的 Tag 约定一致；
    /// 统一走 <c>NavigateCommand</c>，让非导航入口的切页也经过同一条
    /// <c>SyncNavSelection</c> 反向同步，避免高亮与实际页面脱节。
    /// </summary>
    private void DashboardCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag)
            ViewModel.NavigateCommand.Execute(tag);
    }

    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        this.InitializeComponent();
        // TitleBar 控件要求内容延伸到标题栏区域：不设这个标志，窗口顶部会同时
        // 出现系统标题栏和我们的 TitleBar（高度翻倍、按钮点不到）。
        this.ExtendsContentIntoTitleBar = true;
        ViewModel = App.Services.GetRequiredService<MainViewModel>();
        // Hand the owner HWND to the VM so its FolderPicker can parent correctly.
        ViewModel.OwnerHandle = WindowNative.GetWindowHandle(this);
        // Set window size (WinUI 3 Window has no Height/Width XAML attributes)
        // 1280x900：够放下 220px 导航 + 340px 详情栏后主列表仍有两列可用宽度；
        // 1100x860 在 1440p 上显得局促，概览页四张卡会被压扁。
        this.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900));
        // NavigationView 的 pane 在窗口初始化阶段会被算成收起（compact），
        // 显式展开一次，避免首次启动只有一条图标带。
        NavView.IsPaneOpen = true;
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
        // 无条件跑一次：首屏是概览页，且状态栏的 wslc 版本号也来自这里
        // （system info 是唯一的引擎版本来源）。
        await ViewModel.RefreshDashboardCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// 弹出密码输入框并返回用户输入的明文；取消返回 null。
    /// 刻意用 ContentDialog 而非 PasswordBox 绑定：密码只在栈上存在，
    /// 不进任何绑定目标、不进 ViewModel 属性、不可能被 x:Bind 缓存。
    /// </summary>
    private async Task<string?> PromptPasswordDialog()
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

        // 必须 await：对话框的完成需要 UI 线程 pumping，同步等待会自锁整个窗口。
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? box.Password : null;
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
