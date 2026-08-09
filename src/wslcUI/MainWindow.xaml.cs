using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
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
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs e)
    {
        this.Activated -= MainWindow_Activated;
        await ViewModel.RefreshCommand.ExecuteAsync(null);
    }

    private void OpenTerminalButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedContainer is null) return;
        var term = new TerminalWindow(ViewModel.SelectedContainer.Name);
        term.Activate();
    }
}
