using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using wslcUI.Services;
using wslcUI.ViewModels;

namespace wslcUI;

public partial class App : Application
{
    // 声明为 ServiceProvider（而非 IServiceProvider）：关窗时 MainWindow 要
    // 调用 Dispose 触发 WslcSdkClient.Dispose → Session.Terminate()。
    public static ServiceProvider Services { get; private set; } = null!;
    public static DispatcherQueue DispatcherQueue { get; private set; } = null!;
    public static Window? MainWindow { get; private set; }

    public App()
    {
        this.InitializeComponent();

        // 在 Application 创建阶段读取并应用主题。App 继承自 Application（DependencyObject）
        // 且自带 RequestedTheme 属性（Window 没有 RequestedTheme），App-level 的 RequestedTheme
        // 会被 Window.Content 及所有子元素正确继承并触发 ThemeResource 重解析。
        // 在 MainWindow 构造之前调用，XAML 树构建期间就拿到正确的 theme。
        var saved = UserSettings.LoadTheme();
        if (saved is not null) this.RequestedTheme = saved.Value;

        DispatcherQueue = DispatcherQueue.GetForCurrentThread();

        var services = new ServiceCollection();

        // Real backend: drives wslc via the Microsoft.WSL.Containers SDK.
        services.AddSingleton<IWslcClient, WslcSdkClient>();
        //
        // To run the UI without WSL installed (pure XAML/VM development), swap the
        // line above for the fake implementation:
        //     services.AddSingleton<IWslcClient, FakeWslcClient>();

        // Confirmation dialogs for destructive operations (delete container/image/…).
        services.AddSingleton<IDialogService, DialogService>();

        services.AddSingleton<MainViewModel>();

        Services = services.BuildServiceProvider();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
