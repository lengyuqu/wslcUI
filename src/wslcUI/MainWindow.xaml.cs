using Microsoft.UI.Xaml;
using WinRT.Interop;
using wslcUI.ViewModels;

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
    }

    private void OpenTerminalButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedContainer is null) return;
        var term = new TerminalWindow(ViewModel.SelectedContainer.Name);
        term.Activate();
    }
}
