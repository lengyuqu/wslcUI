using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using wslcUI.Services;
using wslcUI.ViewModels;

namespace wslcUI;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    public static DispatcherQueue DispatcherQueue { get; private set; } = null!;
    public static Window? MainWindow { get; private set; }

    public App()
    {
        this.InitializeComponent();

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
