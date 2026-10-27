using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using wslcUI.Models;
using wslcUI.Services;

namespace wslcUI.ViewModels;

/// <summary>
/// 维护页的页面级 ViewModel：磁盘占用汇总 + 五条 prune。
///
/// <para>
/// 这是「耦合最重」的一页，也正因如此才需要先把读访问收口成
/// <see cref="IResourceAccess"/> 再拆：prune 的语义是「改引擎状态 → 列表必须刷新」，
/// 子 VM 既要读集合又要能触发刷新，但不能持有整个壳。
/// </para>
///
/// <para>
/// 界面状态（输出区、四个数字、按钮可用性）全部自理；只把「忙碌、状态栏、
/// 错误、成功」四件事上抛给壳，与其它子 VM 保持同一套约定。
/// </para>
/// </summary>
public sealed partial class MaintenanceViewModel : ObservableObject
{
    private readonly IResourceAccess _res;
    private readonly IWslcClient _client;
    private readonly IDialogService? _dialogs;

    /// <summary>忙碌状态变化（联动外壳的进度条与全局命令可用性）。</summary>
    public event EventHandler<bool>? BusyChanged;

    /// <summary>请求写状态栏文本。</summary>
    public event EventHandler<string>? StatusRequested;

    /// <summary>请求收起提示条（操作成功）。</summary>
    public event EventHandler? Succeeded;

    /// <summary>请求按统一规则报错。</summary>
    public event EventHandler<Exception>? ErrorRaised;

    // ---- 四个数字（字符串而非数值：占用是格式化后的，计数带「N / N」形态）----
    [ObservableProperty] public partial string ImageTotalSizeText { get; set; } = "—";
    [ObservableProperty] public partial string ImageCountText { get; set; } = "0";
    [ObservableProperty] public partial string ReclaimableContainersText { get; set; } = "0";
    [ObservableProperty] public partial string NetworkVolumeCountText { get; set; } = "0 / 0";

    /// <summary>清理操作的 CLI 原始输出累积（不解析，原样展示）。</summary>
    [ObservableProperty] public partial string CleanupOutput { get; set; } = "";
    [ObservableProperty] public partial bool HasCleanupOutput { get; set; }

    /// <summary>清理按钮可用性（忙碌时统一禁用，避免并发 prune）。</summary>
    [ObservableProperty] public partial bool CanPrune { get; set; } = true;

    /// <summary>清理进行中（替代壳的全局 IsBusy 做本地按钮守门）。</summary>
    [ObservableProperty] public partial bool IsPruning { get; set; }

    private CancellationTokenSource? _cts;

    // 壳的全局忙碌（主刷新、其它页的长操作）。prune 与它们不能并发 ——
    // 两个进程同时动引擎状态时，后完成的那次会覆盖前者刚刷新的列表。
    private bool _globalBusy;

    public MaintenanceViewModel(IResourceAccess res, IWslcClient client, IDialogService? dialogs)
    {
        _res = res;
        _client = client;
        _dialogs = dialogs;
    }

    partial void OnIsPruningChanged(bool value) => CanPrune = !_globalBusy && !value;

    /// <summary>由壳同步全局忙碌状态（见 <c>MainViewModel.OnIsBusyChanged</c>）。</summary>
    public void SetGlobalBusy(bool busy)
    {
        _globalBusy = busy;
        CanPrune = !busy && !IsPruning;
    }

    /// <summary>
    /// 重算四个数字。调用时点：进入维护页、主刷新成功后、每次清理之后。
    ///
    /// <para>
    /// **不做成派生属性**：镜像集合会被整体替换、容器集合会被就地合并改变内容，
    /// 两种变化在属性通知上的表现不同；统一在固定时点重算最不容易漏。
    /// </para>
    /// </summary>
    public void Recompute()
    {
        long total = 0;
        var unparsed = 0;
        foreach (var img in _res.Images)
        {
            if (SizeParser.TryParseBytes(img.Size, out var bytes)) total += bytes;
            else unparsed++;
        }

        // 有解析不了的 SIZE（如 "N/A"）时加 "≥"，明示这是**下界**而不是精确合计。
        ImageTotalSizeText = unparsed > 0 ? "≥" + SizeParser.Format(total) : SizeParser.Format(total);
        ImageCountText = _res.Images.Count.ToString(CultureInfo.InvariantCulture);
        ReclaimableContainersText = _res.Containers.Count(c => !c.IsRunning)
                                          .ToString(CultureInfo.InvariantCulture);
        NetworkVolumeCountText = $"{_res.Networks.Count} / {_res.Volumes.Count}";
    }

    /// <summary>最新的清理输出放在最上面：输出区不滚动时也能看到本次结果。</summary>
    private void AppendCleanupOutput(string text)
    {
        CleanupOutput = CleanupOutput.Length == 0 ? text : text + "\n" + CleanupOutput;
        HasCleanupOutput = true;
    }

    private async Task RunPruneAsync(
        string label,
        string confirmTitle,
        string confirmMessage,
        Func<CancellationToken, Task<string>> op,
        Func<Task>? refresh)
    {
        if (_dialogs is not null && !await _dialogs.ConfirmAsync(confirmTitle, confirmMessage))
            return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsPruning = true;
        BusyChanged?.Invoke(this, true);
        StatusRequested?.Invoke(this, $"清理{label}…");
        try
        {
            var output = await op(ct);
            var body = string.IsNullOrWhiteSpace(output) ? "（无输出）" : output.TrimEnd();
            AppendCleanupOutput($"[{DateTime.Now:HH:mm:ss}] 清理{label}\n{body}\n");
            if (refresh is not null) await refresh();
            Recompute();
            StatusRequested?.Invoke(this, $"已清理{label}");
            Succeeded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ErrorRaised?.Invoke(this, ex);
        }
        finally
        {
            IsPruning = false;
            BusyChanged?.Invoke(this, false);
        }
    }

    [RelayCommand]
    private Task PruneContainersAsync() => RunPruneAsync(
        "已停止的容器",
        "清理已停止的容器",
        "将删除所有**已停止**的容器及其可写层 —— 落在容器可写层里的数据会丢失。\n" +
        "挂载在卷或绑定目录中的数据不受影响，镜像也不会被删除。\n\n确定继续吗？",
        ct => _client.PruneContainersAsync(ct),
        _res.RefreshContainersAsync);

    [RelayCommand]
    private Task PruneDanglingImagesAsync() => RunPruneAsync(
        "悬空镜像",
        "清理悬空镜像",
        "将删除所有**悬空镜像**（没有标签、也没有任何容器引用的中间层）。\n" +
        "带标签的镜像不会被删除。\n\n确定继续吗？",
        ct => _client.PruneImagesAsync(false, ct),
        _res.RefreshImagesAsync);

    [RelayCommand]
    private Task PruneUnusedImagesAsync() => RunPruneAsync(
        $"未使用的镜像（共 {ImageCountText} 个）",
        "清理所有未使用的镜像",
        "⚠️ 这是高风险操作：会删除**所有没有被容器引用**的镜像，**包括带标签的**。\n" +
        "之后需要重新 pull 或重新 build 才能再用。\n\n确定继续吗？",
        ct => _client.PruneImagesAsync(true, ct),
        _res.RefreshImagesAsync);

    [RelayCommand]
    private Task PruneNetworksAsync() => RunPruneAsync(
        "未使用的网络",
        "清理未使用的网络",
        "将删除所有没有容器接入的网络。\n" +
        "bridge / host / none 这三个内置网络不会被删除。\n\n确定继续吗？",
        ct => _client.PruneNetworksAsync(ct),
        _res.RefreshNetworksAsync);

    [RelayCommand]
    private Task PruneVolumesAsync() => RunPruneAsync(
        "未使用的卷",
        "清理未使用的卷",
        "⚠️ 会删除所有没有被容器引用的**具名卷**，卷里的数据不可恢复。\n\n确定继续吗？",
        ct => _client.PruneVolumesAsync(ct),
        _res.RefreshVolumesAsync);

}
