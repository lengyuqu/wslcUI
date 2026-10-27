using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using wslcUI.Terminal;
using wslcUI.ViewModels;

namespace wslcUI.Views;

/// <summary>
/// 容器级窗口动作（终端 / attach / 文件浏览）的共用入口。
///
/// <para>
/// 这三件事原本写在 <c>MainWindow.xaml.cs</c> 里 —— 那时按钮只存在于容器列表。
/// 容器列表拆成独立 <see cref="ContainersView"/> 后，详情面板（仍在 MainWindow）
/// 和列表页都要调同一套逻辑；复制一份必然漂移，所以提到这里。
/// </para>
/// <para>
/// 不做成实例服务：它不持有任何状态，且必须在 code-behind 里同步调用。
/// </para>
/// </summary>
internal static class ContainerActions
{
    /// <summary>
    /// 打开交互式终端（<c>wslc exec -it</c>，经 ConPTY 真 TTY）。
    /// 开窗前先做健康预检：机器级 attach 故障时（子进程 0xC0000142 启动即死）
    /// 弹诊断，而不是开一个永远黑屏的死窗口。
    /// </summary>
    public static async Task OpenTerminalAsync(string containerName, XamlRoot? xamlRoot)
    {
        if (!await EnsureConPtyHealthyAsync(xamlRoot)) return;
        new TerminalWindow(containerName).Activate();
    }

    /// <summary>
    /// 附加到运行中容器的前台进程（<c>wslc attach</c>），
    /// 区别于 exec 的「启动一个新进程」。调用方负责确认容器 Running。
    /// </summary>
    public static async Task OpenAttachAsync(string containerName, XamlRoot? xamlRoot)
    {
        if (!await EnsureConPtyHealthyAsync(xamlRoot)) return;
        new TerminalWindow(containerName, TerminalWindow.Mode.Attach).Activate();
    }

    /// <summary>
    /// 打开容器内文件浏览窗口。与 exec 同源限制：<c>wslc exec</c> 需要容器
    /// **正在运行**，所以调用方必须先用 <c>CanAttachContainer</c> 守门。
    /// </summary>
    public static void OpenFiles(string containerName) =>
        new ContainerFilesWindow(containerName).Activate();

    /// <summary>
    /// ConPTY 健康预检（内部带 5 分钟缓存）。探测要起一次 conhost 并等子进程退出
    /// （最长 5s），放在线程池执行，不阻塞 UI 线程；await 续体经
    /// DispatcherQueueSynchronizationContext 回到 UI 线程，弹对话框安全。
    /// </summary>
    public static async Task<bool> EnsureConPtyHealthyAsync(XamlRoot? xamlRoot)
    {
        var (healthy, exitCode) = await Task.Run(PseudoConsole.ProbeHealth);
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
            XamlRoot = xamlRoot,
        };
        _ = dialog.ShowAsync();
        return false;
    }
}
