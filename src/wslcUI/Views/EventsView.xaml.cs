using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using wslcUI.ViewModels;

namespace wslcUI.Views;

/// <summary>
/// 活动页。纯绑定视图：状态与命令全部在 <see cref="MainViewModel"/> 里，
/// code-behind 只负责把 DI 单例接给 x:Bind。
/// </summary>
public sealed partial class EventsView : UserControl
{
    public MainViewModel ViewModel { get; }

    public EventsView()
    {
        // 必须在 InitializeComponent 之前赋值：x:Bind 的首次求值发生在
        // InitializeComponent 内部，晚赋值会直接 NRE。
        ViewModel = App.Services.GetRequiredService<MainViewModel>();
        InitializeComponent();
    }
}
