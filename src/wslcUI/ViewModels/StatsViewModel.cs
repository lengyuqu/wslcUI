using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using wslcUI.Models;
using wslcUI.Services;

namespace wslcUI.ViewModels;

/// <summary>
/// 统计页的列表部分：资源快照表格（搜索 / 排序 / 选中）。
///
/// <para>
/// 采样与曲线在 <see cref="StatsSamplingViewModel"/>，两者刻意分开：
/// 列表是「拉一次看一次」的快照，采样是「定时轮询自己攒历史」的常驻行为，
/// 生命周期完全不同（离开页面要停采样，但快照表格可以留着）。
/// </para>
///
/// <para>
/// 刷新入口走 <see cref="IResourceAccess.RefreshStatsAsync"/>，因为快照换掉之后
/// 壳还要把数值回填进容器、重算概览页 CPU 合计 —— 那些联动不该由子 VM 负责。
/// </para>
/// </summary>
public sealed partial class StatsViewModel : FilteredListViewModel<StatInfo>
{
    private readonly IResourceAccess _res;

    /// <summary>请求写状态栏文本。</summary>
    public event EventHandler<string>? StatusRequested;

    /// <summary>请求按统一规则报错。</summary>
    public event EventHandler<Exception>? ErrorRaised;

    /// <summary>刷新进行中（按钮守门，避免重复拉快照）。</summary>
    [ObservableProperty] public partial bool IsRefreshing { get; set; }

    [ObservableProperty] public partial bool CanRefresh { get; set; } = true;

    public StatsViewModel(IResourceAccess res) => _res = res;

    // ---- 基类要的四个答案 ----

    protected override IEnumerable<StatInfo> Source => _res.Stats;

    protected override bool IsMatch(StatInfo item, string query) =>
        item.Container.Contains(query, StringComparison.OrdinalIgnoreCase);

    protected override string SortKeyOf(StatInfo item, string column) => column switch
    {
        "Cpu" => item.Cpu,
        "Mem" => item.Mem,
        "MemPercent" => item.MemPercent,
        "NetIo" => item.NetIo,
        "Pids" => item.Pids,
        _ => item.Container,
    };

    /// <summary>统计页除容器名外全是数值列，都要按量级比（"451MB" vs "1.5GB"）。</summary>
    protected override bool IsNumericColumn(string column) => column is
        "Cpu" or "Mem" or "MemPercent" or "NetIo" or "Pids";

    protected override string BusinessKeyOf(StatInfo item) => item.Container;

    partial void OnIsRefreshingChanged(bool value) => CanRefresh = !value;

    [RelayCommand]
    private async Task RefreshSnapshotAsync()
    {
        IsRefreshing = true;
        try
        {
            await _res.RefreshStatsAsync();
            Refresh();
        }
        catch (Exception ex)
        {
            ErrorRaised?.Invoke(this, ex);
        }
        finally
        {
            IsRefreshing = false;
        }
    }
}
