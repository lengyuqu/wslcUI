using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using wslcUI.Models;
using wslcUI.Services;

namespace wslcUI.ViewModels;

/// <summary>
/// 卷页的页面级 ViewModel：列表 / 搜索 / 排序 / 增删 + **反向挂载映射**。
///
/// <para>
/// 比网络页多的只有反向映射：选中卷时扫描已 inspect 过的容器，算出「哪些容器
/// 用了这个卷」。核心算法在 <see cref="VolumeReverseMapping.Match"/>（纯函数、可单测），
/// 这里只负责在正确的时点调用它并缓存「还有多少容器没查过」的计数。
/// </para>
/// </summary>
public sealed partial class VolumesViewModel : FilteredListViewModel<VolumeInfo>
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

    [ObservableProperty] public partial string NewVolumeName { get; set; } = "";

    [ObservableProperty] public partial bool CanAct { get; set; }
    [ObservableProperty] public partial bool CanCreate { get; set; }
    [ObservableProperty] public partial bool IsWorking { get; set; }

    /// <summary>
    /// 当前选中卷对应的「还有多少容器未 inspect」提示。
    /// 未查过的容器 Mounts 为空，直接当「无引用」会误导用户删掉在用的卷 ——
    /// 所以宁可留一行坦白文案。
    /// </summary>
    [ObservableProperty] public partial string UninspectedForVolumeHint { get; set; } = "";

    private int _uninspectedCount;
    private CancellationTokenSource? _cts;
    private bool _globalBusy;

    public VolumesViewModel(IResourceAccess res, IWslcClient client, IDialogService? dialogs)
    {
        _res = res;
        _client = client;
        _dialogs = dialogs;
    }

    // ---- 基类要的四个答案 ----

    protected override IEnumerable<VolumeInfo> Source => _res.Volumes;

    protected override bool IsMatch(VolumeInfo item, string query) =>
        item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || item.Driver.Contains(query, StringComparison.OrdinalIgnoreCase)
        || item.Mountpoint.Contains(query, StringComparison.OrdinalIgnoreCase);

    protected override string SortKeyOf(VolumeInfo item, string column) => column switch
    {
        "Driver" => item.Driver,
        "Mountpoint" => item.Mountpoint,
        _ => item.Name,
    };

    protected override string BusinessKeyOf(VolumeInfo item) => item.Name;

    // ---- 选中 → 反向映射 + 按钮可用性 ----

    protected override void OnSelectionChanged(VolumeInfo? value)
    {
        RecomputeCanStates();
        RecomputeUsedBy(value);
    }

    partial void OnNewVolumeNameChanged(string value) => RecomputeCanStates();

    partial void OnIsWorkingChanged(bool value) => RecomputeCanStates();

    private void RecomputeCanStates()
    {
        CanAct = SelectedItem is not null && !_globalBusy && !IsWorking;
        CanCreate = !_globalBusy && !IsWorking && !string.IsNullOrWhiteSpace(NewVolumeName);
    }

    /// <summary>由壳同步全局忙碌（主刷新 / 其它页长操作期间不允许并发增删）。</summary>
    public void SetGlobalBusy(bool busy)
    {
        _globalBusy = busy;
        RecomputeCanStates();
    }

    /// <summary>
    /// 重算反向映射。触发时点：选中卷变化、容器 Mounts 写入后、主刷新后、切到卷页时。
    /// 仅覆盖**已 MountsLoaded** 的容器 —— 未点过的容器 Mounts 仍为空，
    /// 误判为零等于把「未查」当成「无引用」。
    /// </summary>
    public void RecomputeUsedBy(VolumeInfo? volume)
    {
        if (volume is null) return;

        var (usedBy, notInspected) = VolumeReverseMapping.Match(_res.Containers, volume.Name);
        volume.UsedBy = usedBy;

        // 仅在计数变化时通知，避免反复切卷导致 XAML 重复拉 binding。
        if (_uninspectedCount == notInspected) return;
        _uninspectedCount = notInspected;
        UninspectedForVolumeHint = notInspected == 0
            ? ""
            : $"还有 {notInspected} 个容器未查看（点过容器详情才会触发 inspect）。";
    }

    /// <summary>进入卷页：按当前选中项重扫一次（容器可能在别的页被就地合并过）。</summary>
    public void OnPageEntered()
    {
        if (SelectedItem is not null) RecomputeUsedBy(SelectedItem);
    }

    /// <summary>离开卷页：清零未查计数（旧值属于上一轮扫描，留着会显示陈旧数字）。</summary>
    public void OnPageLeft()
    {
        if (_uninspectedCount == 0) return;
        _uninspectedCount = 0;
        UninspectedForVolumeHint = "";
    }

    // ---- 命令 ----

    [RelayCommand]
    private async Task CreateVolumeAsync()
    {
        var name = NewVolumeName.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        IsWorking = true;
        BusyChanged?.Invoke(this, true);
        StatusRequested?.Invoke(this, $"创建卷 {name} …");
        try
        {
            await _client.CreateVolumeAsync(name, _cts.Token);
            NewVolumeName = "";
            await _res.RefreshVolumesAsync();
            Refresh();
            StatusRequested?.Invoke(this, $"卷 {name} 已创建");
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
    private async Task RemoveVolumeAsync()
    {
        if (SelectedItem is null) return;
        var name = SelectedItem.Name;
        if (_dialogs is not null &&
            !await _dialogs.ConfirmAsync("删除卷", $"确定删除卷「{name}」吗？此操作不可撤销。"))
            return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        IsWorking = true;
        BusyChanged?.Invoke(this, true);
        StatusRequested?.Invoke(this, $"删除卷 {name} …");
        try
        {
            await _client.RemoveVolumeAsync(name, _cts.Token);
            SelectedItem = null;
            await _res.RefreshVolumesAsync();
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
