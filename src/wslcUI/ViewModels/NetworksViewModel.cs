using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using wslcUI.Models;
using wslcUI.Services;

namespace wslcUI.ViewModels;

/// <summary>
/// 网络页的页面级 ViewModel：列表 / 搜索 / 排序 / 创建 / 删除。
///
/// <para>
/// <see cref="FilteredListViewModel{T}"/> 的第一个派生类 —— 过滤、排序、
/// 选中项按业务键重指都在基类里，这里只回答「数据源、怎么匹配、按什么排、业务键是什么」。
/// </para>
/// </summary>
public sealed partial class NetworksViewModel : FilteredListViewModel<NetworkInfo>
{
    private readonly IWslcClient _client;
    private readonly IResourceAccess _res;
    private readonly IDialogService? _dialogs;

    /// <summary>忙碌状态变化（联动外壳进度条与其它命令）。</summary>
    public event EventHandler<bool>? BusyChanged;

    /// <summary>请求写状态栏文本。</summary>
    public event EventHandler<string>? StatusRequested;

    /// <summary>请求收起提示条（操作成功）。</summary>
    public event EventHandler? Succeeded;

    /// <summary>请求按统一规则报错。</summary>
    public event EventHandler<Exception>? ErrorRaised;

    [ObservableProperty] public partial string NewNetworkName { get; set; } = "";

    [ObservableProperty] public partial bool CanAct { get; set; }
    [ObservableProperty] public partial bool CanCreate { get; set; }
    [ObservableProperty] public partial bool IsWorking { get; set; }

    private CancellationTokenSource? _cts;
    private bool _globalBusy;

    public NetworksViewModel(IResourceAccess res, IWslcClient client, IDialogService? dialogs)
    {
        _res = res;
        _client = client;
        _dialogs = dialogs;
    }

    // ---- 基类要的四个答案 ----

    protected override IEnumerable<NetworkInfo> Source => _res.Networks;

    protected override bool IsMatch(NetworkInfo item, string query) =>
        item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || item.Driver.Contains(query, StringComparison.OrdinalIgnoreCase)
        || item.Scope.Contains(query, StringComparison.OrdinalIgnoreCase);

    protected override string SortKeyOf(NetworkInfo item, string column) => column switch
    {
        "Driver" => item.Driver,
        "Scope" => item.Scope,
        _ => item.Name,
    };

    protected override string BusinessKeyOf(NetworkInfo item) => item.Name;

    // ---- 可用状态 ----

    protected override void OnSelectionChanged(NetworkInfo? value) => RecomputeCanStates();

    partial void OnNewNetworkNameChanged(string value) => RecomputeCanStates();

    partial void OnIsWorkingChanged(bool value) => RecomputeCanStates();

    private void RecomputeCanStates()
    {
        CanAct = SelectedItem is not null && !_globalBusy && !IsWorking;
        CanCreate = !_globalBusy && !IsWorking && !string.IsNullOrWhiteSpace(NewNetworkName);
    }

    /// <summary>由壳同步全局忙碌（主刷新 / 其它页长操作期间不允许并发增删）。</summary>
    public void SetGlobalBusy(bool busy)
    {
        _globalBusy = busy;
        RecomputeCanStates();
    }

    // ---- 命令 ----

    [RelayCommand]
    private async Task CreateNetworkAsync()
    {
        var name = NewNetworkName.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        IsWorking = true;
        BusyChanged?.Invoke(this, true);
        StatusRequested?.Invoke(this, $"创建网络 {name} …");
        try
        {
            await _client.CreateNetworkAsync(name, _cts.Token);
            NewNetworkName = "";
            await _res.RefreshNetworksAsync();
            Refresh();
            StatusRequested?.Invoke(this, $"网络 {name} 已创建");
            Succeeded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ErrorRaised?.Invoke(this, ex);
        }
        finally
        {
            IsWorking = false;
            BusyChanged?.Invoke(this, false);
        }
    }

    [RelayCommand]
    private async Task RemoveNetworkAsync()
    {
        if (SelectedItem is null) return;
        var name = SelectedItem.Name;
        if (_dialogs is not null &&
            !await _dialogs.ConfirmAsync("删除网络", $"确定删除网络「{name}」吗？此操作不可撤销。"))
            return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        IsWorking = true;
        BusyChanged?.Invoke(this, true);
        StatusRequested?.Invoke(this, $"删除网络 {name} …");
        try
        {
            await _client.RemoveNetworkAsync(name, _cts.Token);
            SelectedItem = null;
            await _res.RefreshNetworksAsync();
            Refresh();
        }
        catch (Exception ex)
        {
            ErrorRaised?.Invoke(this, ex);
        }
        finally
        {
            IsWorking = false;
            BusyChanged?.Invoke(this, false);
        }
    }
}
