using Microsoft.UI.Xaml;
using wslcUI.ViewModels;

namespace wslcUI;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        this.InitializeComponent();
        ViewModel = App.Services.GetRequiredService<MainViewModel>();
    }
}
