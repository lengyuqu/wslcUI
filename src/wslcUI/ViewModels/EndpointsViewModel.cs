using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using wslcUI.Models;
using wslcUI.Services;

namespace wslcUI.ViewModels;

/// <summary>
/// 端点页的页面级 ViewModel：把所有**运行中容器**已发布到宿主的端口聚成一张表。
///
/// <para>
/// 对标 Docker Desktop 的 Endpoints 面板。数据来自容器行的 Ports 字符串，
/// 经 <see cref="EndpointParser"/> 纯函数解析（可单测），不额外调 CLI。
/// </para>
///
/// <para>
/// 与容器的耦合只有一个只读快照 <see cref="_containers"/>：需要的只是
/// 「哪些容器在跑、端口列写了什么」，不需要知道容器的任何可变状态。
/// </para>
///
/// <para>
/// 剪贴板与浏览器打开仍走外部回调注入（<see cref="CopyText"/> / <see cref="OpenUrl"/>）——
/// 子 VM 不引用 WinRT 的 Clipboard / Launcher，否则纯逻辑层被平台依赖污染、无法单测。
/// </para>
/// </summary>
public sealed partial class EndpointsViewModel : ObservableObject
{
    private readonly Func<IEnumerable<ContainerInfo>> _containers;

    /// <summary>请求写状态栏文本。</summary>
    public event EventHandler<string>? StatusRequested;

    /// <summary>请求弹提示条。</summary>
    public event EventHandler<(string Message, InfoBarSeverity Severity)>? NotifyRequested;

    /// <summary>剪贴板写入（由 MainWindow 注入，避免 VM 依赖 WinRT）。</summary>
    public Action<string>? CopyText { get; set; }

    /// <summary>用系统默认浏览器打开 URL（同上）。</summary>
    public Action<string>? OpenUrl { get; set; }

    [ObservableProperty] public partial ObservableCollection<EndpointInfo> Items { get; set; } = new();

    [ObservableProperty] public partial bool HasItems { get; set; }

    /// <summary>端点总数文本（指标卡用）。</summary>
    public string CountText => Items.Count.ToString(CultureInfo.InvariantCulture);

    /// <summary>是否有绑定到非回环地址的端点（局域网可访问）—— 决定是否显示提醒。</summary>
    public bool HasNonLoopback => Items.Any(e => !e.IsLoopback);

    /// <summary>非回环端点的提示文案，无则空串。</summary>
    public string NonLoopbackHint => HasNonLoopback
        ? "有端点绑定在 0.0.0.0，局域网内其他设备也能访问。"
        : "";

    public EndpointsViewModel(Func<IEnumerable<ContainerInfo>> containers) => _containers = containers;

    /// <summary>
    /// 重建端点表：遍历运行中的容器，把 Ports 列解析成端点行。
    /// 已停止容器的端口列本就是空，且端口不再真正监听，排除掉更符合直觉；
    /// 脏端口列只会被跳过，不会中断刷新。
    /// </summary>
    public void Rebuild()
    {
        var list = new List<EndpointInfo>();
        // 按容器名排序，端口再按宿主端口排 —— 否则列表刷新时集合顺序变化
        // 会让面板行乱跳（同一批端点每次刷新位置不同）。
        foreach (var c in _containers()
                              .Where(c => c.IsRunning)
                              .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            list.AddRange(EndpointParser.Parse(c.Ports, c.Name));
        }

        var sorted = list
            .OrderBy(e => e.ContainerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.HostPort)
            .ToList();

        Items = new ObservableCollection<EndpointInfo>(sorted);
        HasItems = Items.Count > 0;
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(HasNonLoopback));
        OnPropertyChanged(nameof(NonLoopbackHint));
    }

    /// <summary>把端点的地址复制到剪贴板（行内按钮）。</summary>
    [RelayCommand]
    private void CopyEndpoint(EndpointInfo? endpoint)
    {
        if (endpoint is null) return;
        var target = endpoint.Url.Length > 0 ? endpoint.Url : endpoint.Summary;
        CopyText?.Invoke(target);
        StatusRequested?.Invoke(this, $"已复制 {target}");
    }

    /// <summary>用系统默认浏览器打开端点。UDP 端点没有可打开的 URL，直接拒绝并说明。</summary>
    [RelayCommand]
    private void OpenEndpoint(EndpointInfo? endpoint)
    {
        if (endpoint is null) return;
        if (endpoint.Url.Length == 0)
        {
            NotifyRequested?.Invoke(this,
                ($"{endpoint.Summary} 是 UDP 端口，没有可打开的地址。", InfoBarSeverity.Informational));
            return;
        }
        OpenUrl?.Invoke(endpoint.Url);
        StatusRequested?.Invoke(this, $"已在浏览器打开 {endpoint.Url}");
    }

    /// <summary>把宿主的 IP:端口 部分复制出来（不含协议，方便拼到别的工具里）。</summary>
    [RelayCommand]
    private void CopyEndpointAddress(EndpointInfo? endpoint)
    {
        if (endpoint is null) return;
        var target = $"{endpoint.HostIp}:{endpoint.HostPort}";
        CopyText?.Invoke(target);
        StatusRequested?.Invoke(this, $"已复制 {target}");
    }
}
