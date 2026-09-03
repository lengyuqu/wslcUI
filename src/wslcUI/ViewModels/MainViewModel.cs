using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using wslcUI.Models;
using wslcUI.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace wslcUI.ViewModels;

/// <summary>左侧导航的六个页面。替换原先平铺的 Pivot。</summary>
public enum ResourcePage
{
    Containers,
    Images,
    Networks,
    Volumes,
    Stats,
    Build,
}

public partial class MainViewModel : ObservableObject
{
    private readonly IWslcClient _client;
    private readonly IDialogService? _dialogs;
    private CancellationTokenSource? _opCts;
    // stats 快照独立取消源：与主操作共享一个 CTS 会让「刷新快照」直接取消
    // 进行中的主刷新（被取消方还会弹「操作已取消」错误条），违背
    // 「stats 刷新不波及主流程」的设计意图。
    private CancellationTokenSource? _statsCts;
    // inspect 链：独立取消源，避免选中快速切换时上一次 inspect 覆盖新选中的 mounts。
    private CancellationTokenSource? _inspectCts;

    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "就绪";

    // 5 个集合的 setter 同时通知 5 个 summary 字符串属性重算（get-only）。
    // 用 [NotifyPropertyChangedFor] 而不是字段缓存，避免 XAML 多次 OneWay
    // 通知被 dispatcher 合并/丢弃导致 status bar 与 metric card 不一致。
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBarSummary))]
    [NotifyPropertyChangedFor(nameof(RunningCountText))]
    [NotifyPropertyChangedFor(nameof(StoppedCountText))]
    [NotifyPropertyChangedFor(nameof(ResourceSummaryText))]
    public partial ObservableCollection<ContainerInfo> Containers { get; set; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBarSummary))]
    [NotifyPropertyChangedFor(nameof(ResourceSummaryText))]
    public partial ObservableCollection<ImageInfo> Images { get; set; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBarSummary))]
    [NotifyPropertyChangedFor(nameof(ResourceSummaryText))]
    public partial ObservableCollection<NetworkInfo> Networks { get; set; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBarSummary))]
    [NotifyPropertyChangedFor(nameof(ResourceSummaryText))]
    public partial ObservableCollection<VolumeInfo> Volumes { get; set; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBarSummary))]
    [NotifyPropertyChangedFor(nameof(StatsCountText))]
    public partial ObservableCollection<StatInfo> Stats { get; set; } = new();

    [ObservableProperty] public partial ContainerInfo? SelectedContainer { get; set; }
    [ObservableProperty] public partial ImageInfo? SelectedImage { get; set; }
    [ObservableProperty] public partial NetworkInfo? SelectedNetwork { get; set; }
    [ObservableProperty] public partial VolumeInfo? SelectedVolume { get; set; }
    [ObservableProperty] public partial StatInfo? SelectedStat { get; set; }

    [ObservableProperty] public partial string PullReference { get; set; } = "alpine:latest";
    [ObservableProperty] public partial string NewNetworkName { get; set; } = "";
    [ObservableProperty] public partial string NewVolumeName { get; set; } = "";
    [ObservableProperty] public partial string Logs { get; set; } = "";
    [ObservableProperty] public partial string BuildContext { get; set; } = "";
    [ObservableProperty] public partial string BuildTag { get; set; } = "myimage:latest";
    [ObservableProperty] public partial string BuildOutput { get; set; } = "";

    // --- 错误 / 提示上屏（InfoBar）---
    [ObservableProperty] public partial bool IsInfoBarOpen { get; set; }
    [ObservableProperty] public partial string InfoMessage { get; set; } = "";
    [ObservableProperty] public partial InfoBarSeverity InfoSeverity { get; set; } = InfoBarSeverity.Informational;

    // --- 派生状态：列表是否有数据（空状态用）。基于过滤后的集合。---
    [ObservableProperty] public partial bool HasContainers { get; set; }
    [ObservableProperty] public partial bool HasImages { get; set; }
    [ObservableProperty] public partial bool HasNetworks { get; set; }
    [ObservableProperty] public partial bool HasVolumes { get; set; }
    [ObservableProperty] public partial bool HasStats { get; set; }
    [ObservableProperty] public partial bool HasBuildOutput { get; set; }

    // --- 派生状态：是否有选中项（按钮启用用）---
    [ObservableProperty] public partial bool HasSelectedContainer { get; set; }
    [ObservableProperty] public partial bool HasSelectedImage { get; set; }
    [ObservableProperty] public partial bool HasSelectedNetwork { get; set; }
    [ObservableProperty] public partial bool HasSelectedVolume { get; set; }
    [ObservableProperty] public partial bool HasSelectedStat { get; set; }

    // --- 派生状态：按钮是否可点（选中 / 输入 / 空闲 的组合）---
    [ObservableProperty] public partial bool CanActOnContainer { get; set; }
    [ObservableProperty] public partial bool CanActOnImage { get; set; }
    [ObservableProperty] public partial bool CanActOnNetwork { get; set; }
    [ObservableProperty] public partial bool CanActOnVolume { get; set; }
    [ObservableProperty] public partial bool CanCreateNetwork { get; set; }
    [ObservableProperty] public partial bool CanCreateVolume { get; set; }
    [ObservableProperty] public partial bool CanPull { get; set; }
    [ObservableProperty] public partial bool CanBuild { get; set; }

    // ---------------- 导航 / 搜索 / 排序 / 面板 ----------------

    [ObservableProperty] public partial ResourcePage CurrentPage { get; set; } = ResourcePage.Containers;
    [ObservableProperty] public partial bool IsContainersPage { get; set; } = true;
    [ObservableProperty] public partial bool IsImagesPage { get; set; }
    [ObservableProperty] public partial bool IsNetworksPage { get; set; }
    [ObservableProperty] public partial bool IsVolumesPage { get; set; }
    [ObservableProperty] public partial bool IsStatsPage { get; set; }
    [ObservableProperty] public partial bool IsBuildPage { get; set; }

    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial string SortedColumn { get; set; } = "";
    [ObservableProperty] public partial bool SortDescending { get; set; }
    [ObservableProperty] public partial string SortSummary { get; set; } = "默认";

    [ObservableProperty] public partial ObservableCollection<ContainerInfo> FilteredContainers { get; set; } = new();
    [ObservableProperty] public partial ObservableCollection<ImageInfo> FilteredImages { get; set; } = new();
    [ObservableProperty] public partial ObservableCollection<NetworkInfo> FilteredNetworks { get; set; } = new();
    [ObservableProperty] public partial ObservableCollection<VolumeInfo> FilteredVolumes { get; set; } = new();
    [ObservableProperty] public partial ObservableCollection<StatInfo> FilteredStats { get; set; } = new();

    /// <summary>指标卡与状态栏用的计数文本。用 string 而非 int，避免 x:Bind 的类型转换坑。</summary>
    // 5 个 summary 字符串：get-only，每次拉 binding 时按 5 个集合实时计算。
    // 上面的 [NotifyPropertyChangedFor] 装饰保证 5 个集合任一变化都会触发
    // 这 5 个字符串的 PropertyChanged，XAML OneWay 拉到最新值。
    public string RunningCountText => Containers.Count(c => c.IsRunning).ToString(CultureInfo.InvariantCulture);
    public string StoppedCountText => Containers.Count(c => !c.IsRunning).ToString(CultureInfo.InvariantCulture);
    public string StatsCountText => Stats.Count.ToString(CultureInfo.InvariantCulture);
    public string ResourceSummaryText => $"{Images.Count} / {Networks.Count} / {Volumes.Count}";
    public string StatusBarSummary => $"{Containers.Count} 容器 · {Images.Count} 镜像 · {Networks.Count} 网络 · {Volumes.Count} 卷";

    [ObservableProperty] public partial bool IsLogPaneOpen { get; set; }
    [ObservableProperty] public partial bool IsDetailOpen { get; set; } = true;
    [ObservableProperty] public partial string LogTarget { get; set; } = "";
    [ObservableProperty] public partial ElementTheme Theme { get; set; } = ElementTheme.Default;

    /// <summary>Owner window handle, set by MainWindow so the FolderPicker can parent.</summary>
    public IntPtr OwnerHandle { get; set; }

    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);
    public bool HasSelection => CurrentPage switch
    {
        ResourcePage.Containers => HasSelectedContainer,
        ResourcePage.Images => HasSelectedImage,
        ResourcePage.Networks => HasSelectedNetwork,
        ResourcePage.Volumes => HasSelectedVolume,
        ResourcePage.Stats => HasSelectedStat,
        _ => false,
    };

    public MainViewModel(IWslcClient client, IDialogService? dialogs = null)
    {
        _client = client;
        _dialogs = dialogs;
        RecomputeCanStates();
    }

    // ---------------- 属性变更钩子 ----------------

    partial void OnIsBusyChanged(bool value) => RecomputeCanStates();

    partial void OnSelectedContainerChanged(ContainerInfo? value)
    {
        HasSelectedContainer = value is not null;
        LogTarget = value?.Name ?? "";
        RecomputeCanStates();

        // 切换选中即取消上一次的 inspect 拉取，避免延迟返回把上一选中
        // 的 Mounts 覆盖到新选中的实例。
        _inspectCts?.Cancel();
        _inspectCts?.Dispose();
        _inspectCts = null;

        if (value is null) return;
        _inspectCts = new CancellationTokenSource();
        // value 是引用捕获；async 续体回到 UI 线程后再按实例身份比对。
        _ = LoadContainerMountsAsync(value, _inspectCts.Token);
    }

    /// <summary>
    /// 拉取选中容器的 Mounts 并回填。失败时静默（inspect 是辅助数据，
    /// 不能因它阻塞 UI 主流程），仅 Debug.WriteLine 留痕。
    /// </summary>
    private async Task LoadContainerMountsAsync(ContainerInfo target, CancellationToken ct)
    {
        try
        {
            var mounts = await _client.InspectContainerAsync(target.Name, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;

            // 三道校验防陈旧数据回填：
            // ① 取消（切换选中 / dispose）——直接丢；
            // ② 选中的实例已变 ——陈旧，不写；
            // ③ 列表里该名容器已不存在（已被删除/移除）——陈旧，不写。
            if (!ReferenceEquals(SelectedContainer, target)) return;
            var live = Containers.FirstOrDefault(c => c.Name == target.Name);
            if (live is null) return;
            live.Mounts = mounts;
            live.MountsLoaded = true;
        }
        catch (OperationCanceledException) { /* 切换或 dispose，忽略 */ }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[wslcUI] inspect {target.Name} 失败: {ex.Message}");
            // 标记为已查（即便失败），让 UI 退出 spinner 显示空态，而不是无限转圈。
            if (!ct.IsCancellationRequested)
            {
                var live = Containers.FirstOrDefault(c => c.Name == target.Name);
                if (live is not null) live.MountsLoaded = true;
            }
        }
    }
    partial void OnSelectedImageChanged(ImageInfo? value)
    {
        HasSelectedImage = value is not null;
        LogTarget = value is null ? "" : value.Reference;
        RecomputeCanStates();
    }
    partial void OnSelectedNetworkChanged(NetworkInfo? value) { HasSelectedNetwork = value is not null; RecomputeCanStates(); }
    partial void OnSelectedVolumeChanged(VolumeInfo? value) { HasSelectedVolume = value is not null; RecomputeCanStates(); }
    partial void OnSelectedStatChanged(StatInfo? value)
    {
        HasSelectedStat = value is not null;
        LogTarget = value?.Container ?? "";
        RecomputeCanStates();
    }

    partial void OnContainersChanged(ObservableCollection<ContainerInfo> value) => ApplyFilter();
    partial void OnImagesChanged(ObservableCollection<ImageInfo> value) => ApplyFilter();
    partial void OnNetworksChanged(ObservableCollection<NetworkInfo> value) => ApplyFilter();
    partial void OnVolumesChanged(ObservableCollection<VolumeInfo> value) => ApplyFilter();
    partial void OnStatsChanged(ObservableCollection<StatInfo> value) => ApplyFilter();
    partial void OnBuildOutputChanged(string value) => HasBuildOutput = !string.IsNullOrEmpty(value);

    partial void OnPullReferenceChanged(string value) => RecomputeCanStates();
    partial void OnNewNetworkNameChanged(string value) => RecomputeCanStates();
    partial void OnNewVolumeNameChanged(string value) => RecomputeCanStates();
    partial void OnBuildContextChanged(string value) => RecomputeCanStates();
    partial void OnBuildTagChanged(string value) => RecomputeCanStates();

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsSearching));
        ApplyFilter();
    }

    partial void OnCurrentPageChanged(ResourcePage value)
    {
        IsContainersPage = value == ResourcePage.Containers;
        IsImagesPage = value == ResourcePage.Images;
        IsNetworksPage = value == ResourcePage.Networks;
        IsVolumesPage = value == ResourcePage.Volumes;
        IsStatsPage = value == ResourcePage.Stats;
        IsBuildPage = value == ResourcePage.Build;
        // 切页即清空搜索，避免"上页的过滤条件残留到本页"这种隐形状态。
        SearchText = "";
        OnPropertyChanged(nameof(HasSelection));
    }

    private void RecomputeCanStates()
    {
        CanActOnContainer = HasSelectedContainer && !IsBusy;
        CanActOnImage = HasSelectedImage && !IsBusy;
        CanActOnNetwork = HasSelectedNetwork && !IsBusy;
        CanActOnVolume = HasSelectedVolume && !IsBusy;
        CanCreateNetwork = !IsBusy && !string.IsNullOrWhiteSpace(NewNetworkName);
        CanCreateVolume = !IsBusy && !string.IsNullOrWhiteSpace(NewVolumeName);
        CanPull = !IsBusy && !string.IsNullOrWhiteSpace(PullReference);
        CanBuild = !IsBusy && !string.IsNullOrWhiteSpace(BuildContext) && !string.IsNullOrWhiteSpace(BuildTag);
        OnPropertyChanged(nameof(HasSelection));
    }

    // ---------------- 过滤 + 排序 ----------------

    /// <summary>重建可见集合。列表绑定 Filtered*，原始集合只作数据源。</summary>
    private void ApplyFilter()
    {
        var q = (SearchText ?? "").Trim();
        var desc = SortDescending;

        FilteredContainers = new ObservableCollection<ContainerInfo>(OrderBy(
            Containers.Where(c => Matches(q, c.Name, c.Image, c.StatusLabel, c.Ports)),
            SortedColumn switch
            {
                "Image" => c => c.Image,
                "Status" => c => c.StatusLabel,
                "Ports" => c => c.Ports,
                "Cpu" => c => c.Cpu,
                "CreatedAt" => c => c.CreatedAt,
                _ => c => c.Name,
            },
            numeric: SortedColumn == "Cpu", desc));

        FilteredImages = new ObservableCollection<ImageInfo>(OrderBy(
            Images.Where(i => Matches(q, i.Repository, i.Tag, i.Size, i.Reference)),
            SortedColumn switch
            {
                "Tag" => i => i.Tag,
                "Size" => i => i.Size,
                _ => i => i.Repository,
            },
            numeric: SortedColumn == "Size", desc));

        FilteredNetworks = new ObservableCollection<NetworkInfo>(OrderBy(
            Networks.Where(n => Matches(q, n.Name, n.Driver, n.Scope)),
            SortedColumn switch
            {
                "Driver" => n => n.Driver,
                "Scope" => n => n.Scope,
                _ => n => n.Name,
            },
            numeric: false, desc));

        FilteredVolumes = new ObservableCollection<VolumeInfo>(OrderBy(
            Volumes.Where(v => Matches(q, v.Name, v.Driver, v.Mountpoint)),
            SortedColumn switch
            {
                "Driver" => v => v.Driver,
                "Mountpoint" => v => v.Mountpoint,
                _ => v => v.Name,
            },
            numeric: false, desc));

        FilteredStats = new ObservableCollection<StatInfo>(OrderBy(
            Stats.Where(s => Matches(q, s.Container)),
            SortedColumn switch
            {
                "Cpu" => s => s.Cpu,
                "Mem" => s => s.Mem,
                "MemPercent" => s => s.MemPercent,
                "NetIo" => s => s.NetIo,
                "Pids" => s => s.Pids,
                _ => s => s.Container,
            },
            numeric: SortedColumn is "Cpu" or "MemPercent" or "Pids", desc));

        HasContainers = FilteredContainers.Count > 0;
        HasImages = FilteredImages.Count > 0;
        HasNetworks = FilteredNetworks.Count > 0;
        HasVolumes = FilteredVolumes.Count > 0;
        HasStats = FilteredStats.Count > 0;

        // 5 个 summary 字符串已改为 get-only，XAML 拉 binding 时按 5 个集合实时算。
        // 显式 PropertyChanged 通知（防 XAML 把集合 setter 通知合并掉），
        // 让 status bar / metric card 在本帧内拉新值。
        OnPropertyChanged(nameof(RunningCountText));
        OnPropertyChanged(nameof(StoppedCountText));
        OnPropertyChanged(nameof(StatsCountText));
        OnPropertyChanged(nameof(ResourceSummaryText));
        OnPropertyChanged(nameof(StatusBarSummary));
    }

    private static bool Matches(string query, params string?[] fields)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        return fields.Any(f => f is not null && f.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private static List<T> OrderBy<T>(IEnumerable<T> source, Func<T, string> selector,
                                      bool numeric, bool descending)
    {
        var list = source.ToList();
        var factor = descending ? -1 : 1;
        list.Sort((a, b) =>
        {
            var x = selector(a) ?? "";
            var y = selector(b) ?? "";
            var cmp = numeric
                ? LeadingNumber(x).CompareTo(LeadingNumber(y))
                : string.Compare(x, y, CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
            return cmp * factor;
        });
        return list;
    }

    /// <summary>取字符串开头的数值，用于 "12.3%" / "48.2 MB" 这类显示串的数值排序。</summary>
    private static double LeadingNumber(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        var i = 0;
        while (i < s.Length && !char.IsDigit(s[i]) && s[i] != '.') i++;
        if (i >= s.Length) return 0;
        var j = i;
        while (j < s.Length && (char.IsDigit(s[j]) || s[j] == '.')) j++;
        return double.TryParse(s[i..j], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    // ---------------- 导航 / 排序 / 面板命令 ----------------

    [RelayCommand]
    private void Navigate(string page)
    {
        if (!Enum.TryParse<ResourcePage>(page, ignoreCase: true, out var target)) return;
        CurrentPage = target;
    }

    [RelayCommand]
    private void SortBy(string column)
    {
        if (string.Equals(SortedColumn, column, StringComparison.OrdinalIgnoreCase))
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortedColumn = column;
            SortDescending = false;
        }
        SortSummary = $"{LabelOf(column)} {(SortDescending ? "↓" : "↑")}";
        ApplyFilter();
    }

    [RelayCommand]
    private void ClearSort()
    {
        SortedColumn = "";
        SortDescending = false;
        SortSummary = "默认";
        ApplyFilter();
    }

    [RelayCommand]
    private void ToggleLogPane() => IsLogPaneOpen = !IsLogPaneOpen;

    [RelayCommand]
    private void ToggleDetail() => IsDetailOpen = !IsDetailOpen;

    [RelayCommand]
    private void ToggleTheme() =>
        Theme = Theme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    // ---------------- 操作基础设施 ----------------

    /// <summary>开始一次新操作：取消上一次未完成的操作，并返回本次的取消令牌。</summary>
    private CancellationToken BeginOp()
    {
        _opCts?.Cancel();
        _opCts = new CancellationTokenSource();
        return _opCts.Token;
    }

    /// <summary>当前操作的取消令牌（供内部子刷新使用）。</summary>
    private CancellationToken CurrentToken => _opCts?.Token ?? CancellationToken.None;

    private void ShowError(System.Exception ex)
    {
        string msg;
        InfoBarSeverity severity;
        if (ex is OperationCanceledException)
        {
            msg = "操作已取消。";
            severity = InfoBarSeverity.Informational;
            Status = "已取消";
        }
        else
        {
            // 展开 AggregateException，拿到最有信息量的内层消息。
            var inner = ex is System.AggregateException ae && ae.InnerException is not null
                ? ae.InnerException
                : ex;
            // wslc 报错带「错误代码: XXX」机器码，先翻译成中文建议；
            // 未命中映射（TranslateCliError 返回 null）保留原文兜底。
            msg = WslcCli.TranslateCliError(inner.Message) ?? inner.Message;
            severity = InfoBarSeverity.Error;
            Status = "错误";
        }

        InfoMessage = msg;
        InfoSeverity = severity;
        IsInfoBarOpen = true;
    }

    [RelayCommand]
    private void DismissInfo() => IsInfoBarOpen = false;

    /// <summary>
    /// 把 `wslc stats` 快照按容器名回填到 Containers，让列表与详情面板能看到
    /// CPU / 内存。stats 是一次性快照，未刷新时这些字段保持「—」；
    /// 上次有快照、这次没有的容器（已停止/已删除于 stats 视图）会被重置回「—」。
    /// </summary>
    private void MergeStatsIntoContainers()
    {
        var map = new Dictionary<string, StatInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in Stats)
        {
            if (!string.IsNullOrEmpty(s.Container)) map.TryAdd(s.Container, s);
        }
        foreach (var c in Containers)
        {
            if (map.TryGetValue(c.Name, out var s))
            {
                c.Cpu = s.Cpu;
                c.Mem = s.Mem;
                c.MemPercent = s.MemPercent;
                c.NetIo = s.NetIo;
                c.Pids = s.Pids;
                c.HasStats = true;
            }
            else if (c.HasStats)
            {
                // 该容器已不在 stats 快照里 → 清掉旧数据，避免显示过期数值。
                c.Cpu = "—";
                c.Mem = "—";
                c.MemPercent = "—";
                c.NetIo = "—";
                c.Pids = "—";
                c.HasStats = false;
            }
        }
    }

    /// <summary>
    /// 刷新容器的就地合并：按 Name（大小写不敏感）复用既有实例并更新字段，
    /// 新增的追加、消失的移除。调用方随后调用 ApplyFilter 重建过滤视图。
    /// </summary>
    private void UpdateContainersInPlace(IReadOnlyList<ContainerInfo> fresh)
    {
        var existing = new Dictionary<string, ContainerInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in Containers) existing.TryAdd(c.Name, c);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in fresh)
        {
            if (string.IsNullOrEmpty(f.Name)) continue;
            seen.Add(f.Name);
            if (existing.TryGetValue(f.Name, out var cur))
            {
                // INPC 属性：值变化时会自动通知行模板与详情面板。
                cur.Id = f.Id;
                cur.Image = f.Image;
                cur.Status = f.Status;
                cur.Ports = f.Ports;
                cur.CreatedAt = f.CreatedAt;
            }
            else
            {
                Containers.Add(f);
                // 同步登记进 existing：若本次快照里出现同名重复行（CLI 输出异常），
                // 第二行会命中复用分支而不是再次 Add 造成列表重复。
                existing[f.Name] = f;
            }
        }

        // 用倒序移除，避免 Remove 时索引位移。
        for (var i = Containers.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Containers[i].Name))
                Containers.RemoveAt(i);
        }
    }

    private void RebuildContainerView()
    {
        MergeStatsIntoContainers();
        ApplyFilter();
    }

    private static string LabelOf(string column) => column switch
    {
        "Name" => "名称",
        "Image" => "镜像",
        "Status" => "状态",
        "Ports" => "端口",
        "Cpu" => "CPU",
        "CreatedAt" => "创建",
        "Repository" => "仓库",
        "Tag" => "标签",
        "Size" => "大小",
        "Driver" => "驱动",
        "Scope" => "作用域",
        "Mountpoint" => "挂载点",
        "Container" => "容器",
        "Mem" => "内存",
        "MemPercent" => "内存%",
        "NetIo" => "网络 I/O",
        "Pids" => "PID",
        _ => column,
    };

    // ---------------- 命令 ----------------

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var ct = BeginOp();
        IsBusy = true;
        Status = "刷新中…";
        try
        {
            var containers = await _client.ListContainersAsync(ct);
            // 就地合并（按 Name 复用实例），而不是整体替换集合：
            // ① SelectedContainer 实例保持有效，选中与详情面板不断档；
            // ② 行/详情绑定靠 ContainerInfo 的 INPC 实时刷新。
            UpdateContainersInPlace(containers);

            var images = await _client.ListImagesAsync(ct);
            Images = new ObservableCollection<ImageInfo>(images);

            var networks = await _client.ListNetworksAsync(ct);
            Networks = new ObservableCollection<NetworkInfo>(networks);

            var volumes = await _client.ListVolumesAsync(ct);
            Volumes = new ObservableCollection<VolumeInfo>(volumes);

            RebuildContainerView();

            Status = $"已加载 {Containers.Count} 容器 / {Images.Count} 镜像 / {Networks.Count} 网络 / {Volumes.Count} 卷";
            IsInfoBarOpen = false;
        }
        catch (System.Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (SelectedContainer is null) return;
        var name = SelectedContainer.Name;
        var ct = BeginOp();
        IsBusy = true;
        Status = $"启动 {name} …";
        try
        {
            await _client.StartAsync(name, ct);
            await RefreshAsync();
        }
        catch (System.Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (SelectedContainer is null) return;
        var name = SelectedContainer.Name;
        var ct = BeginOp();
        IsBusy = true;
        Status = $"停止 {name} …";
        try
        {
            await _client.StopAsync(name, ct);
            await RefreshAsync();
        }
        catch (System.Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task PullAsync()
    {
        var reference = PullReference.Trim();
        if (string.IsNullOrWhiteSpace(reference)) return;
        var ct = BeginOp();
        IsBusy = true;
        Status = $"拉取 {reference} …";
        try
        {
            var progress = new Progress<(string Status, long Current, long Total)>(
                p => Status = $"拉取 {p.Status} ({p.Current}/{p.Total})");
            await _client.PullImageAsync(reference, progress, ct);
            await RefreshAsync();
        }
        catch (System.Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteContainerAsync()
    {
        if (SelectedContainer is null) return;
        var name = SelectedContainer.Name;
        if (_dialogs is not null &&
            !await _dialogs.ConfirmAsync("删除容器", $"确定删除容器「{name}」吗？此操作不可撤销，相关数据将一并移除。"))
            return;

        var ct = BeginOp();
        IsBusy = true;
        Status = $"删除容器 {name} …";
        try
        {
            await _client.DeleteContainerAsync(name, ct);
            SelectedContainer = null;
            await RefreshAsync();
        }
        catch (System.Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteImageAsync()
    {
        if (SelectedImage is null) return;
        var reference = SelectedImage.Reference;
        if (_dialogs is not null &&
            !await _dialogs.ConfirmAsync("删除镜像", $"确定删除镜像「{reference}」吗？此操作不可撤销。"))
            return;

        var ct = BeginOp();
        IsBusy = true;
        Status = $"删除镜像 {reference} …";
        try
        {
            await _client.DeleteImageAsync(reference, ct);
            SelectedImage = null;
            await RefreshAsync();
        }
        catch (System.Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task PickBuildContextAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        // WinUI 3 unpackaged: the picker must be parented to our window.
        if (OwnerHandle != IntPtr.Zero)
            InitializeWithWindow.Initialize(picker, OwnerHandle);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
            BuildContext = folder.Path;
    }

    [RelayCommand]
    private async Task BuildImageAsync()
    {
        var ctx = BuildContext.Trim();
        var tag = BuildTag.Trim();
        if (string.IsNullOrWhiteSpace(ctx) || string.IsNullOrWhiteSpace(tag)) return;
        var ct = BeginOp();
        IsBusy = true;
        Status = $"构建 {tag} …";
        BuildOutput = "";
        try
        {
            var progress = new Progress<string>(line =>
            {
                BuildOutput += line + "\n";
                Status = $"构建 {tag}: {line}";
            });
            await _client.BuildImageAsync(ctx, tag, progress, ct);
            Status = $"镜像 {tag} 构建完成";
            IsInfoBarOpen = false;
        }
        catch (System.Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ShowLogsAsync()
    {
        if (SelectedContainer is null) return;
        // await 前捕获：等待期间用户切换选中项会让后续 SelectedContainer.Name
        // 指向别的容器，日志标题与内容对不上。
        var name = SelectedContainer.Name;
        var ct = BeginOp();
        IsBusy = true;
        Status = $"读取 {name} 日志 …";
        try
        {
            LogTarget = name;
            Logs = await _client.GetLogsAsync(name, ct);
            // 载入后自动展开抽屉：用户点「日志」就是想看内容，不该再点一次。
            IsLogPaneOpen = true;
            Status = $"{name} 日志已加载";
            IsInfoBarOpen = false;
        }
        catch (System.Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshStatsAsync()
    {
        // 独立取消链：只取消上一次 stats 快照，不动主刷新。
        _statsCts?.Cancel();
        _statsCts = new CancellationTokenSource();
        var ct = _statsCts.Token;
        IsBusy = true;
        Status = "读取资源统计…";
        try
        {
            // Stats 是一次性快照，刻意不并入主 RefreshCommand（避免潜在卡顿波及主刷新）。
            var stats = await _client.GetStatsAsync(ct);
            Stats = new ObservableCollection<StatInfo>(stats);
            RebuildContainerView();
            Status = $"已加载 {Stats.Count} 个容器的资源统计";
            IsInfoBarOpen = false;
        }
        catch (System.Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CreateNetworkAsync()
    {
        var name = NewNetworkName.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        var ct = BeginOp();
        IsBusy = true;
        Status = $"创建网络 {name} …";
        try
        {
            await _client.CreateNetworkAsync(name, ct);
            NewNetworkName = "";
            await RefreshNetworksAsync();
            Status = $"网络 {name} 已创建";
            IsInfoBarOpen = false;
        }
        catch (System.Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RemoveNetworkAsync()
    {
        if (SelectedNetwork is null) return;
        var name = SelectedNetwork.Name;
        if (_dialogs is not null &&
            !await _dialogs.ConfirmAsync("删除网络", $"确定删除网络「{name}」吗？此操作不可撤销。"))
            return;

        var ct = BeginOp();
        IsBusy = true;
        Status = $"删除网络 {name} …";
        try
        {
            await _client.RemoveNetworkAsync(name, ct);
            SelectedNetwork = null;
            await RefreshNetworksAsync();
        }
        catch (System.Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CreateVolumeAsync()
    {
        var name = NewVolumeName.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        var ct = BeginOp();
        IsBusy = true;
        Status = $"创建卷 {name} …";
        try
        {
            await _client.CreateVolumeAsync(name, ct);
            NewVolumeName = "";
            await RefreshVolumesAsync();
            Status = $"卷 {name} 已创建";
            IsInfoBarOpen = false;
        }
        catch (System.Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RemoveVolumeAsync()
    {
        if (SelectedVolume is null) return;
        var name = SelectedVolume.Name;
        if (_dialogs is not null &&
            !await _dialogs.ConfirmAsync("删除卷", $"确定删除卷「{name}」吗？此操作不可撤销。"))
            return;

        var ct = BeginOp();
        IsBusy = true;
        Status = $"删除卷 {name} …";
        try
        {
            await _client.RemoveVolumeAsync(name, ct);
            SelectedVolume = null;
            await RefreshVolumesAsync();
        }
        catch (System.Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshNetworksAsync()
    {
        var n = await _client.ListNetworksAsync(CurrentToken);
        Networks = new ObservableCollection<NetworkInfo>(n);
    }

    private async Task RefreshVolumesAsync()
    {
        var v = await _client.ListVolumesAsync(CurrentToken);
        Volumes = new ObservableCollection<VolumeInfo>(v);
    }
}
