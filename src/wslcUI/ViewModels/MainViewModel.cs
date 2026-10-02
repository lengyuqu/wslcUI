using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using wslcUI.Models;
using wslcUI.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace wslcUI.ViewModels;

/// <summary>左侧导航的九个页面。替换原先平铺的 Pivot。</summary>
public enum ResourcePage
{
    Containers,
    Images,
    Networks,
    Volumes,
    Stats,
    Build,
    Maintenance,
    Endpoints,
    Events,
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

    // ---------------- 维护页（磁盘占用 + 一键清理）----------------
    // 四个卡片的文案（字符串而非数值：占用是格式化后的，计数带单位"个"）。
    // 用 [ObservableProperty] 承载而不是直接算，是因为 Images/Containers 可能在
    // 其它页面被就地合并（UpdateContainersInPlace），靠派生属性通知不可靠；
    // 改为在固定时点（进维护页 / 主刷新后 / 每次清理后）统一重算。
    [ObservableProperty] public partial string ImageTotalSizeText { get; set; } = "—";
    [ObservableProperty] public partial string ImageCountText { get; set; } = "0";
    [ObservableProperty] public partial string ReclaimableContainersText { get; set; } = "0";
    [ObservableProperty] public partial string NetworkVolumeCountText { get; set; } = "0 / 0";

    /// <summary>清理操作的 CLI 原始输出累积（不解析，原样展示）。</summary>
    [ObservableProperty] public partial string CleanupOutput { get; set; } = "";
    [ObservableProperty] public partial bool HasCleanupOutput { get; set; }

    /// <summary>清理按钮可用性（忙碌时统一禁用，避免并发 prune）。</summary>
    [ObservableProperty] public partial bool CanPrune { get; set; } = true;

    // ---------------- 端点面板（已发布端口汇总）----------------
    // 对标 Docker Desktop 的 Endpoints 面板：把**所有运行中容器**已发布到宿主
    // 的端口聚成一张表，一键复制 / 打开浏览器。数据来自容器行的 Ports 字符串，
    // 经 Services/EndpointParser.cs 纯函数解析（可单测），不额外调 CLI。

    [ObservableProperty] public partial ObservableCollection<EndpointInfo> Endpoints { get; set; } = new();

    [ObservableProperty] public partial bool HasEndpoints { get; set; }

    /// <summary>端点总数文本（指标卡用）。</summary>
    public string EndpointCountText => Endpoints.Count.ToString(CultureInfo.InvariantCulture);

    /// <summary>是否有绑定到非回环地址的端点（局域网可访问）——决定是否显示提醒。</summary>
    public bool HasNonLoopbackEndpoint => Endpoints.Any(e => !e.IsLoopback);

    /// <summary>非回环端点的提示文案，无则空串。</summary>
    public string NonLoopbackHint => HasNonLoopbackEndpoint
        ? "有端点绑定在 0.0.0.0，局域网内其他设备也能访问。"
        : "";

    // ---------------- 批量操作（Select 模式）----------------
    // 竞品（WSL Container Desktop）的 Select 模式：列表进入多选后批量启停/删除。
    // 选中集合用 HashSet<string>（存**名称**而非对象引用）：列表刷新会就地
    // 合并容器实例（见 UpdateContainersInPlace），存引用会因实例替换而失效。

    [ObservableProperty] public partial bool IsSelectMode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedCountText))]
    [NotifyPropertyChangedFor(nameof(HasSelectionForBulk))]
    [NotifyPropertyChangedFor(nameof(CanBulkStart))]
    [NotifyPropertyChangedFor(nameof(CanBulkStop))]
    [NotifyPropertyChangedFor(nameof(CanBulkDelete))]
    public partial HashSet<string> SelectedNames { get; set; } = new();

    /// <summary>已选数量文本（Select 模式工具条用）。</summary>
    public string SelectedCountText => SelectedNames.Count.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedCountText))]
    [NotifyPropertyChangedFor(nameof(HasSelectionForBulk))]
    [NotifyPropertyChangedFor(nameof(CanBulkStart))]
    [NotifyPropertyChangedFor(nameof(CanBulkStop))]
    [NotifyPropertyChangedFor(nameof(CanBulkDelete))]
    public partial bool HasSelectionForBulk { get; set; }

    [ObservableProperty] public partial bool CanBulkStart { get; set; }
    [ObservableProperty] public partial bool CanBulkStop { get; set; }
    [ObservableProperty] public partial bool CanBulkDelete { get; set; }

    // ---------------- 活动流（wslc events 实时流）----------------
    // 与统计页的 3 秒轮询不同：事件流由 wslc 主动推送，容器启停/网络变更
    // 发生的瞬间就能拿到，不必等下一轮轮询。
    //
    // ⚠️ 事件流是**永不结束**的长驻进程（实测：带 --since 也不自行退出，
    // 退出码 124 = 被 timeout 杀），所以：① 绝不 ReadToEndAsync；
    // ② 离开本页必须 Stop()，否则会留下一个常驻 wslc 进程。
    // 详见 Services/EventStreamService.cs 类注释。

    [ObservableProperty] public partial ObservableCollection<ContainerEvent> Events { get; set; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActivitySummary))]
    [NotifyPropertyChangedFor(nameof(EventStreamButtonText))]
    public partial bool IsEventStreamRunning { get; set; }

    [ObservableProperty] public partial bool HasEvents { get; set; }

    /// <summary>事件总数（受 MaxEvents 环形上限约束，故为「最近 N 条」）。</summary>
    public string EventCountText => Events.Count.ToString(CultureInfo.InvariantCulture);

    /// <summary>工具条上的状态摘要。</summary>
    public string ActivitySummary => IsEventStreamRunning
        ? $"监听中 · 最近 {Events.Count} 条"
        : (Events.Count > 0 ? $"已停止 · 留存 {Events.Count} 条" : "未开始监听");

    /// <summary>开始/停止按钮的文案（随运行状态切换）。</summary>
    public string EventStreamButtonText => IsEventStreamRunning ? "停止监听" : "开始监听";

    // 事件列表的环形上限：长驻监听下无界增长会吃爆内存。
    private const int MaxEvents = 500;

    private readonly EventStreamService _eventStream = new();

    /// <summary>
    /// UI 线程的调度队列，在 <see cref="StartEventStream"/>（跑在 UI 线程上）时捕获。
    /// 事件回调来自 wslc 子进程的读取线程，那里 <c>GetForCurrentThread()</c> 返回
    /// null —— 必须提前存住 UI 队列才能把事件 marshal 回去。
    /// </summary>
    private Microsoft.UI.Dispatching.DispatcherQueue? _uiQueue;

    /// <summary>开始/停止事件流。进活动页自动开始，离开自动停止。</summary>
    [RelayCommand]
    private void ToggleEventStream()
    {
        if (_eventStream.IsRunning) StopEventStream();
        else StartEventStream();
    }

    private void StartEventStream(string? since = null)
    {
        if (_eventStream.IsRunning) return;

        // 事件回调在后台线程，必须先抓住 UI 队列（见 _uiQueue 注释）。
        _uiQueue ??= Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (_uiQueue is null)
        {
            InfoMessage = "当前环境没有 UI 调度队列，无法监听事件。";
            InfoSeverity = InfoBarSeverity.Warning;
            IsInfoBarOpen = true;
            return;
        }

        _eventStream.EventReceived += OnEventReceived;
        _eventStream.ErrorReceived += OnEventStreamError;
        _eventStream.Exited += OnEventStreamExited;
        try
        {
            _eventStream.Start(since);
            IsEventStreamRunning = true;
            Status = "正在监听容器事件…";
        }
        catch (System.Exception ex)
        {
            StopEventStream();
            ShowError(ex);
        }
    }

    private void StopEventStream()
    {
        _eventStream.EventReceived -= OnEventReceived;
        _eventStream.ErrorReceived -= OnEventStreamError;
        _eventStream.Exited -= OnEventStreamExited;
        _eventStream.Stop();
        IsEventStreamRunning = false;
        OnPropertyChanged(nameof(ActivitySummary));
    }

    /// <summary>
    /// 事件回调来自后台线程（wslc 进程的 stdout 读取线程），
    /// 必须切回 UI 线程才能动 ObservableCollection。
    /// </summary>
    private void OnEventReceived(object? sender, ContainerEvent e)
    {
        var q = _uiQueue;
        if (q is null) return;
        if (q.HasThreadAccess) AppendEvent(e);
        else q.TryEnqueue(() => AppendEvent(e));
    }

    private void AppendEvent(ContainerEvent e)
    {
        // 最新的排在最上面，与「刚发生的事最该被看到」一致。
        Events.Insert(0, e);
        while (Events.Count > MaxEvents) Events.RemoveAt(Events.Count - 1);
        HasEvents = Events.Count > 0;
        OnPropertyChanged(nameof(EventCountText));
        OnPropertyChanged(nameof(ActivitySummary));
    }

    private void OnEventStreamError(object? sender, string message)
    {
        var q = _uiQueue;
        if (q is null) return;
        void Show()
        {
            InfoMessage = $"事件流出错：{message}";
            InfoSeverity = InfoBarSeverity.Warning;
            IsInfoBarOpen = true;
        }
        if (q.HasThreadAccess) Show(); else q.TryEnqueue(Show);
    }

    private void OnEventStreamExited(object? sender, int exitCode)
    {
        var q = _uiQueue;
        if (q is null) return;
        void Show()
        {
            IsEventStreamRunning = false;
            InfoMessage = $"事件流已退出（exit {exitCode}）。可点「开始监听」重新开始。";
            InfoSeverity = InfoBarSeverity.Informational;
            IsInfoBarOpen = true;
        }
        if (q.HasThreadAccess) Show(); else q.TryEnqueue(Show);
    }

    [RelayCommand]
    private void ClearEvents()
    {
        Events.Clear();
        HasEvents = false;
        OnPropertyChanged(nameof(EventCountText));
        OnPropertyChanged(nameof(ActivitySummary));
    }

    // ---------------- 镜像仓库与出向操作（push / tag / 登录）----------------
    // 此前镜像页只能「进」（pull）不能「出」（push），闭环缺失。密码一律走
    // IWslcClient.RegistryLoginAsync 的 --password-stdin 通道，**不存盘、不进日志**。

    [ObservableProperty] public partial string RegistryServer { get; set; } = "";
    [ObservableProperty] public partial string RegistryUser { get; set; } = "";
    [ObservableProperty] public partial string TagTarget { get; set; } = "";

    [ObservableProperty] public partial bool CanPushImage { get; set; }
    [ObservableProperty] public partial bool CanTagImage { get; set; }

    /// <summary>登录结果提示（不显示密码）。</summary>
    [ObservableProperty] public partial string RegistryStatusText { get; set; } = "";

    [ObservableProperty] public partial string PushOutput { get; set; } = "";
    [ObservableProperty] public partial bool HasPushOutput { get; set; }

    [ObservableProperty] public partial string ContainerExportPath { get; set; } = "";

    /// <summary>
    /// 登录镜像仓库。密码通过回调交给 UI 层读取（PasswordBox 无法双向绑定），
    /// 拿完即丢 —— 不进 ViewModel 状态、不写盘、不进日志。
    /// </summary>
    public Func<string?>? PromptPassword { get; set; }

    [RelayCommand]
    private async Task RegistryLoginAsync()
    {
        if (_dialogs is not null && !await _dialogs.ConfirmAsync(
                "登录镜像仓库",
                $"将以用户「{(string.IsNullOrWhiteSpace(RegistryUser) ? "<空>" : RegistryUser)}」" +
                $"登录{(string.IsNullOrWhiteSpace(RegistryServer) ? "默认服务器" : RegistryServer)}。密码只经 stdin 传入，不保存。"))
            return;

        var password = PromptPassword?.Invoke();
        if (string.IsNullOrEmpty(password))
        {
            InfoMessage = "未输入密码，已取消登录。";
            InfoSeverity = InfoBarSeverity.Informational;
            IsInfoBarOpen = true;
            return;
        }

        var ct = BeginOp();
        IsBusy = true;
        try
        {
            await _client.RegistryLoginAsync(
                string.IsNullOrWhiteSpace(RegistryServer) ? null : RegistryServer.Trim(),
                string.IsNullOrWhiteSpace(RegistryUser) ? null : RegistryUser.Trim(),
                password, ct);
            password = null; // 尽早脱离本方法的局部变量
            RegistryStatusText = $"已登录 {RegistryServer}";
            Status = "仓库登录成功";
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
    private async Task RegistryLogoutAsync()
    {
        var ct = BeginOp();
        IsBusy = true;
        try
        {
            await _client.RegistryLogoutAsync(
                string.IsNullOrWhiteSpace(RegistryServer) ? null : RegistryServer.Trim(), ct);
            RegistryStatusText = "已注销";
            Status = "仓库已注销";
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
    private async Task PushImageAsync()
    {
        if (SelectedImage is null) return;
        var reference = SelectedImage.Reference;
        if (string.IsNullOrWhiteSpace(reference))
        {
            InfoMessage = "该镜像没有可用的引用（reference 为空），无法推送。";
            InfoSeverity = InfoBarSeverity.Warning;
            IsInfoBarOpen = true;
            return;
        }

        if (_dialogs is not null && !await _dialogs.ConfirmAsync(
                "推送镜像", $"将 {reference} 推送到镜像仓库。未登录的仓库会失败。"))
            return;

        var ct = BeginOp();
        IsBusy = true;
        Status = $"正在推送 {reference}…";
        PushOutput = "";
        HasPushOutput = true;
        try
        {
            var progress = new Progress<string>(line =>
                PushOutput = AppendLine(PushOutput, line));
            await _client.PushImageAsync(reference, allTags: false, quiet: false, progress, ct);
            Status = $"已推送 {reference}";
            IsInfoBarOpen = false;
        }
        catch (System.Exception ex)
        {
            PushOutput = AppendLine(PushOutput, $"失败：{ex.Message}");
            ShowError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task TagImageAsync()
    {
        if (SelectedImage is null) return;
        var target = TagTarget.Trim();
        if (string.IsNullOrEmpty(target))
        {
            InfoMessage = "请先填写目标标签（如 registry.example.com/app:v1）。";
            InfoSeverity = InfoBarSeverity.Informational;
            IsInfoBarOpen = true;
            return;
        }

        var ct = BeginOp();
        IsBusy = true;
        try
        {
            await _client.TagImageAsync(SelectedImage.Reference, target, ct);
            await RefreshImagesAsync();
            Status = $"已打标签 {target}";
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

    private static string AppendLine(string text, string line) =>
        string.IsNullOrEmpty(text) ? line : text + "\n" + line;

    [RelayCommand]
    private async Task KillContainerAsync()
    {
        if (SelectedContainer is null) return;
        var name = SelectedContainer.Name;
        if (_dialogs is not null && !await _dialogs.ConfirmAsync(
                "强制终止容器",
                $"SIGKILL 不会给 {name} 里的进程清理机会，未落盘的数据会丢失。确定继续？"))
            return;

        var ct = BeginOp();
        IsBusy = true;
        try
        {
            await _client.KillContainerAsync(name, null, ct);
            await RefreshContainersAsync();
            Status = $"已强制终止 {name}";
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
    private async Task ExportContainerAsync()
    {
        if (SelectedContainer is null) return;
        var name = SelectedContainer.Name;

        var path = PickSavePath($"wslcui-{name}.tar");
        if (string.IsNullOrEmpty(path)) return;

        var ct = BeginOp();
        IsBusy = true;
        Status = $"正在导出 {name}…";
        try
        {
            await _client.ExportContainerAsync(name, path, ct);
            ContainerExportPath = path;
            Status = $"已导出到 {path}";
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

    /// <summary>
    /// 弹出保存对话框并返回用户选中的路径；取消返回空串。
    /// 与 <c>PickContextDirectory</c> 同一套 OwnerHandle 父窗口处理。
    /// </summary>
    private string? PickSavePath(string suggestedName)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedFileName = suggestedName,
        };
        picker.FileTypeChoices.Add("Tar 归档", new List<string> { ".tar" });
        // OwnerHandle 是 Intptr（值类型），恒非 null；为 0 表示还没被 MainWindow 注入，
        // 此时跳过 Initialize，picker 会以默认（无父窗口）方式弹出。
        if (OwnerHandle != IntPtr.Zero)
            InitializeWithWindow.Initialize(picker, OwnerHandle);
        return picker.PickSaveFileAsync()?.GetAwaiter().GetResult()?.Path;
    }

    // ---------------- 统计页实时采样（sparkline）----------------
    // wslc stats 是一次性快照、不流式，所以"实时曲线"只能靠**定时轮询**自己攒历史。
    // 历史按容器名分桶、每桶最多 MaxSamples 个点（环形丢弃最旧的）。

    [ObservableProperty] public partial bool IsSampling { get; set; }
    [ObservableProperty] public partial string SparkTargetName { get; set; } = "";
    [ObservableProperty] public partial string SparkSummary { get; set; } = "";
    [ObservableProperty] public partial PointCollection CpuSparkPoints { get; set; } = new();
    [ObservableProperty] public partial PointCollection MemSparkPoints { get; set; } = new();

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
    // attach 仅对运行中容器有意义：未运行时容器内无前台进程可附加。
    [ObservableProperty] public partial bool CanAttachContainer { get; set; }

    // ---------------- 导航 / 搜索 / 排序 / 面板 ----------------

    [ObservableProperty] public partial ResourcePage CurrentPage { get; set; } = ResourcePage.Containers;
    [ObservableProperty] public partial bool IsContainersPage { get; set; } = true;
    [ObservableProperty] public partial bool IsImagesPage { get; set; }
    [ObservableProperty] public partial bool IsNetworksPage { get; set; }
    [ObservableProperty] public partial bool IsVolumesPage { get; set; }
    [ObservableProperty] public partial bool IsStatsPage { get; set; }
    [ObservableProperty] public partial bool IsBuildPage { get; set; }
    [ObservableProperty] public partial bool IsMaintenancePage { get; set; }
    [ObservableProperty] public partial bool IsEndpointsPage { get; set; }
    [ObservableProperty] public partial bool IsEventsPage { get; set; }

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
        // 曲线目标在没选中 stats 行时回退到容器列表的选中项，所以这里也要跟一次。
        RebuildSparklines();

        // 切换选中即取消上一次的 inspect 拉取，避免延迟返回把上一选中
        // 的 Mounts 覆盖到新选中的实例。
        _inspectCts?.Cancel();
        _inspectCts?.Dispose();
        _inspectCts = null;

        if (value is null) return;

        // 短路同实例重发：Repeated clicks on the same row waste a CLI roundtrip
        // once Mounts have already been loaded. ReferenceEquals is intentional —
        // wslc list -a reuses ContainerInfo instances across refreshes
        // (UpdateContainersInPlace), so an identity check is stable per session.
        if (ReferenceEquals(_lastInspectTarget, value) && value.MountsLoaded) return;
        _lastInspectTarget = value;

        _inspectCts = new CancellationTokenSource();
        // value 是引用捕获；async 续体回到 UI 线程后再按实例身份比对。
        // fire-and-forget：partial void 不支持 async，所以无法 await。
        // 失败/取消的处理（含陈旧数据丢弃）在 LoadContainerMountsAsync 内部完成。
        _ = LoadContainerMountsAsync(value, _inspectCts.Token);
    }

    /// <summary>
    /// 上一次发起 inspect 的容器实例。仅以引用相等短路，防止 ListView 双绑
    /// 同实例重发 setter 时重复拉 CLI。MountsLoaded 标志 sticky（RefreshAsync
    /// 复用实例不重置），所以同实例已查就不再触发。
    /// </summary>
    private ContainerInfo? _lastInspectTarget;

    /// <summary>
    /// 拉取选中容器的 Mounts 并回填。失败时静默（inspect 是辅助数据，
    /// 不能因它阻塞 UI 主流程），仅 Debug.WriteLine 留痕。
    /// </summary>
    private async Task LoadContainerMountsAsync(ContainerInfo target, CancellationToken ct)
    {
        // 提前查一次 list 里的"活"实例，try/catch 两条路径共用 —— 避免重复 O(N) 扫
        //（#8 微优化）。如果已被 Refresh/外部删除，本方法直接放弃，不写回任何东西。
        var live = Containers.FirstOrDefault(c => c.Name == target.Name);
        if (live is null) return;

        try
        {
            var mounts = await _client.InspectContainerAsync(target.Name, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;

            // 三道校验防陈旧数据回填：
            // ① 取消（切换选中 / dispose）——直接丢；
            // ② 选中的实例已变 ——陈旧，不写；
            // ③ 列表里该名容器已不存在（已被删除/移除）——陈旧，不写。
            if (!ReferenceEquals(SelectedContainer, target)) return;
            live.Mounts = mounts;
            live.MountsLoaded = true;

            // 容器挂载变了 → 若卷页正显示某卷，反向表可能需要刷新。
            if (CurrentPage == ResourcePage.Volumes && SelectedVolume is not null)
                RecomputeUsedBy(SelectedVolume);
        }
        catch (OperationCanceledException) { /* 切换或 dispose，忽略 */ }
        catch (System.Exception ex)
        {
            // 仅 debug 留痕（inspect 是辅助数据，不能弹 InfoBar 干扰主流程），
            // 但要让用户能区分"未挂载"与"查不到"——所以状态栏显示一句简短提示。
            System.Diagnostics.Debug.WriteLine($"[wslcUI] inspect {target.Name} 失败: {ex.Message}");
            if (!ct.IsCancellationRequested && ReferenceEquals(SelectedContainer, target))
            {
                live.MountsLoaded = true;
                Status = $"inspect {target.Name} 失败";
            }
        }
    }

    /// <summary>
    /// 选中卷变化、容器 Mounts 写入后、RefreshAsync 后、切到卷页时调用，重算 UsedBy。
    /// 提取自 OnSelectedVolumeChanged 内联段——三处触发共享同一扫描逻辑。
    /// 核心算法抽到 <see cref="VolumeReverseMapping.Match"/> 作纯函数以便单测。
    /// </summary>
    private void RecomputeUsedBy(VolumeInfo? volume)
    {
        if (volume is null) return;

        var (usedBy, notInspected) = VolumeReverseMapping.Match(Containers, volume.Name);
        volume.UsedBy = usedBy;

        // 仅在计数变化时通知 hint，避免反复切卷导致 XAML 重复拉 binding。
        if (_uninspectedContainerCountForSelectedVolume != notInspected)
        {
            _uninspectedContainerCountForSelectedVolume = notInspected;
            OnPropertyChanged(nameof(UninspectedForVolumeHint));
        }
    }
    partial void OnSelectedImageChanged(ImageInfo? value)
    {
        HasSelectedImage = value is not null;
        LogTarget = value is null ? "" : value.Reference;
        RecomputeCanStates();
    }
    partial void OnSelectedNetworkChanged(NetworkInfo? value) { HasSelectedNetwork = value is not null; RecomputeCanStates(); }
    partial void OnSelectedVolumeChanged(VolumeInfo? value)
    {
        HasSelectedVolume = value is not null;
        RecomputeCanStates();

        // 反向扫描：已 inspect 过的容器中哪些引用了此卷。
        // 按 mount.Name == volume.Name 匹配（区分大小写：wslc 自身大小写敏感）。
        // 仅覆盖**已 MountsLoaded** 的容器——未点过的容器 Mounts 仍为空、
        // 误判为零 = 把未查当无引用，混进"未使用"分类会误导用户删除。
        // 因此用空 UsedBy + 一个提示文案坦白："其他容器尚未查看"。
        RecomputeUsedBy(value);
    }

    /// <summary>
    /// 当前选中卷对应的"还有多少容器未 inspect"统计。
    /// OnSelectedVolumeChanged 每次扫描时刷新；外部 Cancel 切换时不清
    /// （让用户切回能恢复显示，提示"还有 N 个未查"是有用的提示）。
    /// </summary>
    private int _uninspectedContainerCountForSelectedVolume;

    public string UninspectedForVolumeHint =>
        _uninspectedContainerCountForSelectedVolume == 0
            ? ""
            : $"还有 {_uninspectedContainerCountForSelectedVolume} 个容器未查看（点过容器详情才会触发 inspect）。";
    partial void OnSelectedStatChanged(StatInfo? value)
    {
        HasSelectedStat = value is not null;
        LogTarget = value?.Container ?? "";
        RecomputeCanStates();
        RebuildSparklines();
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
        // 离开卷页时清零"未查容器"提示计数：旧值属于上一卷的扫描，
        // 切回去 OnSelectedVolumeChanged 会重算；但留在原地读会显示陈旧数字。
        if (_lastPage == ResourcePage.Volumes && value != ResourcePage.Volumes &&
            _uninspectedContainerCountForSelectedVolume != 0)
        {
            _uninspectedContainerCountForSelectedVolume = 0;
            OnPropertyChanged(nameof(UninspectedForVolumeHint));
        }
        _lastPage = value;

        IsContainersPage = value == ResourcePage.Containers;
        IsImagesPage = value == ResourcePage.Images;
        IsNetworksPage = value == ResourcePage.Networks;
        IsVolumesPage = value == ResourcePage.Volumes;
        IsStatsPage = value == ResourcePage.Stats;
        IsBuildPage = value == ResourcePage.Build;
        IsMaintenancePage = value == ResourcePage.Maintenance;
        IsEndpointsPage = value == ResourcePage.Endpoints;
        IsEventsPage = value == ResourcePage.Events;
        // 进维护页时重算占用/可回收计数（数字来自 Images/Containers 的当前内容，
        // 而这两个集合可能在别的页面被就地合并过，光靠属性通知不可靠）。
        if (value == ResourcePage.Maintenance) UpdateMaintenanceSummary();
        // 端点面板同样依赖容器的 Ports 列：容器在别的页面被就地合并过时
        // 面板不会自动跟着重算，进页时按当前集合重算一次。
        if (value == ResourcePage.Endpoints) RebuildEndpoints();
        // 进活动页开流、离开立刻关流：事件流是**永不退出**的长驻 wslc 进程，
        // 不关就会一直占着一个子进程 + 管道，与统计页的采样同理。
        if (value == ResourcePage.Events) StartEventStream();
        else if (IsEventStreamRunning) StopEventStream();
        // 离开统计页停采样：后台每 3 秒起一个 wslc 进程，不在该页时纯属浪费。
        if (value != ResourcePage.Stats && IsSampling) StopSampling();
        // 切页即清空搜索，避免"上页的过滤条件残留到本页"这种隐形状态。
        SearchText = "";
        OnPropertyChanged(nameof(HasSelection));

        // 切到卷页且已选某卷时，重算反向表（用户可能新点过容器）。
        if (value == ResourcePage.Volumes && SelectedVolume is not null)
            RecomputeUsedBy(SelectedVolume);
    }

    /// <summary>
    /// 上一次的 CurrentPage。仅用于在 OnCurrentPageChanged 中检测"离开卷页"
    /// 以清零提示计数（partial void 参数 value 是新页，没法直接看旧页）。
    /// </summary>
    private ResourcePage _lastPage = ResourcePage.Containers;

    private void RecomputeCanStates()
    {
        CanActOnContainer = HasSelectedContainer && !IsBusy;
        CanAttachContainer = HasSelectedContainer && !IsBusy && SelectedContainer is { IsRunning: true };
        CanActOnImage = HasSelectedImage && !IsBusy;
        CanActOnNetwork = HasSelectedNetwork && !IsBusy;
        CanActOnVolume = HasSelectedVolume && !IsBusy;
        CanCreateNetwork = !IsBusy && !string.IsNullOrWhiteSpace(NewNetworkName);
        CanCreateVolume = !IsBusy && !string.IsNullOrWhiteSpace(NewVolumeName);
        CanPull = !IsBusy && !string.IsNullOrWhiteSpace(PullReference);
        CanBuild = !IsBusy && !string.IsNullOrWhiteSpace(BuildContext) && !string.IsNullOrWhiteSpace(BuildTag);
        CanPrune = !IsBusy;
        OnPropertyChanged(nameof(HasSelection));
    }

    // ---------------- 过滤 + 排序 ----------------

    /// <summary>重建可见集合。列表绑定 Filtered*，原始集合只作数据源。</summary>
    private void ApplyFilter()
    {
        var q = (SearchText ?? "").Trim();
        var desc = SortDescending;

        FilteredContainers = new ObservableCollection<ContainerInfo>(
            SortedColumn == "MountListCell"
                ? OrderByInt(
                    Containers.Where(c => Matches(q, c.Name, c.Image, c.StatusLabel, c.Ports, c.MountListCell)),
                    c => c.MountCount, desc)
                : OrderBy(
                    Containers.Where(c => Matches(q, c.Name, c.Image, c.StatusLabel, c.Ports, c.MountListCell)),
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

    /// <summary>按 int 键排序——用于「卷」列按挂载数排，避免走字符串前缀数字解析失真（`pgdata +1` 拼字符串会被错配）。</summary>
    private static List<T> OrderByInt<T>(IEnumerable<T> source, Func<T, int> selector, bool descending)
    {
        var list = source.ToList();
        var factor = descending ? -1 : 1;
        list.Sort((a, b) => selector(a).CompareTo(selector(b)) * factor);
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

        // 卷页在当前可见且已选中某卷时，新一次 Refresh 容器列表可能
        // 改写 Mounts（同一实例 INPC，但**新增**容器可能也用了它）；
        // 重新扫一次确保反向映射不陈旧。
        if (CurrentPage == ResourcePage.Volumes && SelectedVolume is not null)
            RecomputeUsedBy(SelectedVolume);
    }

    private static string LabelOf(string column) => column switch
    {
        "Name" => "名称",
        "Image" => "镜像",
        "Status" => "状态",
        "Ports" => "端口",
        "Cpu" => "CPU",
        "CreatedAt" => "创建",
        "MountListCell" => "卷",
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
            UpdateMaintenanceSummary();
            RebuildEndpoints();
            UpdateBulkStates();

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
    private async Task RestartAsync()
    {
        if (SelectedContainer is null) return;
        var name = SelectedContainer.Name;
        var ct = BeginOp();
        IsBusy = true;
        Status = $"重启 {name} …";
        try
        {
            // wslc restart（2.9.12+）：运行中的重启、未运行的直接启动
            await _client.RestartAsync(name, ct);
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

    private async Task RefreshImagesAsync()
    {
        var i = await _client.ListImagesAsync(CurrentToken);
        Images = new ObservableCollection<ImageInfo>(i);
    }

    private async Task RefreshVolumesAsync()
    {
        var v = await _client.ListVolumesAsync(CurrentToken);
        Volumes = new ObservableCollection<VolumeInfo>(v);
    }

    // ---------------- 维护页：占用统计 + 一键清理 ----------------

    /// <summary>
    /// 重算维护页的四个数字。调用时点：进入维护页、主刷新成功后、每次清理之后。
    /// **不做成派生属性**：<see cref="Images"/> 可能在别的页面被整体替换、
    /// <see cref="Containers"/> 会被 <c>UpdateContainersInPlace</c> 就地改动内容
    /// （集合实例不变 → 属性通知不触发），派生属性会读到陈旧计数。
    /// </summary>
    private void UpdateMaintenanceSummary()
    {
        long total = 0;
        var unparsed = 0;
        foreach (var img in Images)
        {
            if (SizeParser.TryParseBytes(img.Size, out var bytes)) total += bytes;
            else unparsed++;
        }

        // 有解析不了的 SIZE（如 "N/A"）时加 "≥"，明示这是**下界**而不是精确合计。
        ImageTotalSizeText = unparsed > 0 ? "≥" + SizeParser.Format(total) : SizeParser.Format(total);
        ImageCountText = Images.Count.ToString(CultureInfo.InvariantCulture);
        ReclaimableContainersText = Containers.Count(c => !c.IsRunning).ToString(CultureInfo.InvariantCulture);
        NetworkVolumeCountText = $"{Networks.Count} / {Volumes.Count}";
    }

    /// <summary>
    /// 重建端点面板：遍历**运行中**容器，把 Ports 列解析成端点行。
    /// 已停止容器的端口列本就是空，且它们的端口不再真正监听，排除掉更符合直觉。
    /// 解析是纯函数（EndpointParser），脏端口列只会被跳过，不会中断刷新。
    /// </summary>
    private void RebuildEndpoints()
    {
        var list = new List<EndpointInfo>();
        // 按容器名排序，端口再按宿主端口排 —— 否则 RefreshAsync 的集合顺序
        // 变化会让面板行乱跳（同一批端点每次刷新位置不同）。
        foreach (var c in Containers.Where(c => c.IsRunning)
                                    .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            list.AddRange(EndpointParser.Parse(c.Ports, c.Name));
        }

        var sorted = list
            .OrderBy(e => e.ContainerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.HostPort)
            .ToList();

        Endpoints = new ObservableCollection<EndpointInfo>(sorted);
        HasEndpoints = Endpoints.Count > 0;
        OnPropertyChanged(nameof(EndpointCountText));
        OnPropertyChanged(nameof(HasNonLoopbackEndpoint));
        OnPropertyChanged(nameof(NonLoopbackHint));
    }

    // ---------------- 批量操作（Select 模式）----------------

    /// <summary>进入/退出 Select 模式。退出时清空选择，避免下次进入残留上次的勾选。</summary>
    [RelayCommand]
    private void ToggleSelectMode()
    {
        IsSelectMode = !IsSelectMode;
        if (!IsSelectMode) SetBulkSelection(Array.Empty<string>());
    }

    /// <summary>
    /// 把外部（ListView 多选）给定的选中名同步成批量操作的选中集合。
    /// 唯一入口 —— 勾选框、全选、批量动作后的重算都走这里，
    /// 避免 SelectedNames 与 UI 选中状态各持一份、互相打脸。
    /// </summary>
    public void SetBulkSelection(IEnumerable<string> names)
    {
        SelectedNames = new HashSet<string>(names, StringComparer.Ordinal);
        UpdateBulkStates();
    }

    [RelayCommand]
    private void SelectAllVisible()
    {
        SetBulkSelection(FilteredContainers.Select(c => c.Name));
    }

    [RelayCommand]
    private void ClearSelection()
    {
        SetBulkSelection(Array.Empty<string>());
    }

    /// <summary>
    /// 批量动作的公共骨架：逐个跑单容器动作，**单个失败不中断其余**，
    /// 最后汇总成功/失败计数。全部失败时才弹错误条（部分成功也给，
    /// 但文案区分，避免用户以为全成了）。
    /// </summary>
    private async Task RunBulkAsync(
        string label,
        string confirmTitle,
        string confirmMessage,
        Func<string, CancellationToken, Task> op)
    {
        var targets = SelectedNames.ToList();
        if (targets.Count == 0) return;

        if (_dialogs is not null && !await _dialogs.ConfirmAsync(confirmTitle, confirmMessage))
            return;

        var ct = BeginOp();
        IsBusy = true;
        var ok = 0;
        var failed = new List<string>();
        try
        {
            foreach (var name in targets)
            {
                try
                {
                    await op(name, ct);
                    ok++;
                }
                catch (System.OperationCanceledException)
                {
                    throw; // 用户取消：立即中止，不记为失败
                }
                catch (System.Exception ex)
                {
                    // 逐项容错：一个容器失败（如已被删）不该让整批停下来。
                    failed.Add($"{name}：{ex.Message}");
                }
            }

            await RefreshContainersAsync();
            RebuildEndpoints();
            UpdateBulkStates();

            Status = failed.Count == 0
                ? $"已{label} {ok} 个容器"
                : $"已{label} {ok} 个，{failed.Count} 个失败";
            IsInfoBarOpen = false;
            if (failed.Count > 0)
            {
                InfoMessage = $"部分{label}失败：\n" + string.Join("\n", failed.Take(5));
                if (failed.Count > 5) InfoMessage += $"\n…另有 {failed.Count - 5} 个";
                InfoSeverity = InfoBarSeverity.Warning;
                IsInfoBarOpen = true;
            }
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
    private Task BulkStartAsync() => RunBulkAsync(
        "启动",
        "批量启动",
        $"确定启动选中的 {SelectedNames.Count} 个容器？",
        (name, ct) => _client.StartAsync(name, ct));

    [RelayCommand]
    private Task BulkStopAsync() => RunBulkAsync(
        "停止",
        "批量停止",
        $"确定停止选中的 {SelectedNames.Count} 个容器？",
        (name, ct) => _client.StopAsync(name, ct));

    [RelayCommand]
    private Task BulkDeleteAsync() => RunBulkAsync(
        "删除",
        "批量删除",
        $"确定**删除**选中的 {SelectedNames.Count} 个容器？此操作不可撤销。",
        (name, ct) => _client.DeleteContainerAsync(name, ct));

    /// <summary>
    /// 勾选变化后重算按钮可用性。启动要求「选中里至少有一个已停止的」，
    /// 停止要求「至少有一个运行中」—— 没符合条件的项就禁用，避免点了必然报错。
    /// </summary>
    private void UpdateBulkStates()
    {
        var picked = Containers.Where(c => SelectedNames.Contains(c.Name)).ToList();
        HasSelectionForBulk = picked.Count > 0;
        CanBulkStart = picked.Any(c => !c.IsRunning);
        CanBulkStop = picked.Any(c => c.IsRunning);
        CanBulkDelete = picked.Count > 0;
    }

    /// <summary>
    /// 端点动作的外部回调。ViewModel 不直接引用 WinRT 的 Clipboard / Launcher
    /// —— 那会让它无法单测，也把平台依赖拉进纯逻辑层。由 MainWindow 注入。
    /// </summary>
    public Action<string>? CopyText { get; set; }

    public Action<string>? OpenUrl { get; set; }

    /// <summary>把选中端点的地址复制到剪贴板（端点行右键/按钮）。</summary>
    [RelayCommand]
    private void CopyEndpoint(EndpointInfo? endpoint)
    {
        if (endpoint is null) return;
        var target = endpoint.Url.Length > 0 ? endpoint.Url : endpoint.Summary;
        CopyText?.Invoke(target);
        Status = $"已复制 {target}";
    }

    /// <summary>用系统默认浏览器打开端点。UDP 端点没有可打开的 URL，直接拒绝并说明。</summary>
    [RelayCommand]
    private void OpenEndpoint(EndpointInfo? endpoint)
    {
        if (endpoint is null) return;
        if (endpoint.Url.Length == 0)
        {
            InfoMessage = $"{endpoint.Summary} 是 UDP 端口，没有可打开的地址。";
            InfoSeverity = InfoBarSeverity.Informational;
            IsInfoBarOpen = true;
            return;
        }
        OpenUrl?.Invoke(endpoint.Url);
        Status = $"已在浏览器打开 {endpoint.Url}";
    }

    /// <summary>把宿主的 IP:端口 部分复制出来（不含协议，方便拼到别的工具里）。</summary>
    [RelayCommand]
    private void CopyEndpointAddress(EndpointInfo? endpoint)
    {
        if (endpoint is null) return;
        var target = $"{endpoint.HostIp}:{endpoint.HostPort}";
        CopyText?.Invoke(target);
        Status = $"已复制 {target}";
    }

    /// <summary>容器单表刷新（prune 后用，不必拉全套）。</summary>
    private async Task RefreshContainersAsync()
    {
        var c = await _client.ListContainersAsync(CurrentToken);
        UpdateContainersInPlace(c);
        RebuildContainerView();
        RebuildEndpoints();
        UpdateBulkStates();
    }

    /// <summary>最新的清理输出放在最上面：输出区不滚动时也能看到本次结果。</summary>
    private void AppendCleanupOutput(string text)
    {
        CleanupOutput = CleanupOutput.Length == 0 ? text : text + "\n" + CleanupOutput;
        HasCleanupOutput = true;
    }

    /// <summary>
    /// 清理命令的共用骨架：确认 → 执行 → 原样记录 CLI 输出 → 刷新相关列表 → 重算占用。
    /// 输出**不做解析**（回收量文案随版本/语言变化），原样透传，永不过期。
    /// </summary>
    private async Task RunPruneAsync(
        string label,
        string confirmTitle,
        string confirmMessage,
        System.Func<CancellationToken, Task<string>> op,
        System.Func<Task>? refresh)
    {
        if (_dialogs is not null && !await _dialogs.ConfirmAsync(confirmTitle, confirmMessage))
            return;

        var ct = BeginOp();
        IsBusy = true;
        Status = $"清理{label}…";
        try
        {
            var output = await op(ct);
            var body = string.IsNullOrWhiteSpace(output) ? "（无输出）" : output.TrimEnd();
            AppendCleanupOutput($"[{System.DateTime.Now:HH:mm:ss}] 清理{label}\n{body}\n");
            if (refresh is not null) await refresh();
            UpdateMaintenanceSummary();
            Status = $"已清理{label}";
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
    private Task PruneContainersAsync() => RunPruneAsync(
        "已停止的容器",
        "清理已停止的容器",
        "将删除所有**已停止**的容器及其可写层 —— 落在容器可写层里的数据会丢失。\n" +
        "挂载在卷或绑定目录中的数据不受影响，镜像也不会被删除。\n\n确定继续吗？",
        ct => _client.PruneContainersAsync(ct),
        RefreshContainersAsync);

    [RelayCommand]
    private Task PruneDanglingImagesAsync() => RunPruneAsync(
        "悬空镜像",
        "清理悬空镜像",
        "将删除所有**悬空镜像**（没有标签、也没有任何容器引用的中间层）。\n" +
        "带标签的镜像不会被删除。\n\n确定继续吗？",
        ct => _client.PruneImagesAsync(false, ct),
        RefreshImagesAsync);

    [RelayCommand]
    private Task PruneUnusedImagesAsync() => RunPruneAsync(
        $"未使用的镜像（共 {ImageCountText} 个）",
        "清理所有未使用的镜像",
        "⚠️ 这是高风险操作：会删除**所有没有被容器引用**的镜像，**包括带标签的**。\n" +
        "之后需要重新 pull 或重新 build 才能再用。\n\n确定继续吗？",
        ct => _client.PruneImagesAsync(true, ct),
        RefreshImagesAsync);

    [RelayCommand]
    private Task PruneNetworksAsync() => RunPruneAsync(
        "未使用的网络",
        "清理未使用的网络",
        "将删除所有没有容器接入的网络。\n" +
        "bridge / host / none 这三个内置网络不会被删除。\n\n确定继续吗？",
        ct => _client.PruneNetworksAsync(ct),
        RefreshNetworksAsync);

    [RelayCommand]
    private Task PruneVolumesAsync() => RunPruneAsync(
        "未使用的卷",
        "清理未使用的卷",
        "⚠️ 这是高风险操作：会删除**所有没有被任何容器使用**的卷，**卷内的数据会一并销毁**。\n" +
        "若某个卷只是「当前没有容器在跑」（例如数据库已停止），它同样会被删除。\n\n" +
        "确定继续吗？",
        ct => _client.PruneVolumesAsync(ct),
        RefreshVolumesAsync);

    // ---------------- 统计页实时采样（sparkline）----------------
    // wslc stats 是**一次性快照、不流式**，所以"实时曲线"只能定时轮询自己攒历史。
    // 历史按容器名分桶，每桶最多 MaxSamples 个点，超出丢最旧的。

    private const int MaxSamples = 60;

    /// <summary>
    /// 曲线绘制区尺寸。必须与 XAML 里 <c>Polyline</c> 所在 Border 的内宽一致
    /// （详情面板 340 − StackPanel Padding 16×2 − Border 描边 ≈ 308，留 12px 余量）。
    /// Border 高 44、曲线画 36 留上下各 4 的内边距。
    /// </summary>
    private const double SparkWidth = 296;
    private const double SparkHeight = 36;

    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(3);

    private readonly Dictionary<string, List<double>> _cpuHistory = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<double>> _memHistory = new(StringComparer.Ordinal);
    private DispatcherQueueTimer? _sampler;
    private bool _samplingStarted; // 首帧立即出点，不留 3 秒空白

    [RelayCommand]
    private void ToggleSampling()
    {
        if (IsSampling) { StopSampling(); return; }
        StartSampling();
    }

    private void StartSampling()
    {
        // DispatcherQueueTimer 必须在有 UI 调度队列的线程上创建；没有就明确报错，
        // 不要假装在采样（否则用户会以为图停住了是数据问题）。
        var queue = DispatcherQueue.GetForCurrentThread();
        if (queue is null)
        {
            InfoMessage = "当前环境没有 UI 调度队列，无法启动实时采样。";
            InfoSeverity = InfoBarSeverity.Warning;
            IsInfoBarOpen = true;
            return;
        }

        if (!_samplingStarted)
        {
            _sampler = queue.CreateTimer();
            _sampler.Interval = SampleInterval;
            _sampler.IsRepeating = true;
            _sampler.Tick += async (_, _) => await SampleOnceAsync();
            _samplingStarted = true;
        }

        _sampler!.Start();
        IsSampling = true;
        Status = $"实时采样中（每 {SampleInterval.TotalSeconds:0} 秒一次）";
        _ = SampleOnceAsync(); // 立即取一次，不等第一个 tick
    }

    private void StopSampling()
    {
        _sampler?.Stop();
        IsSampling = false;
        Status = "已停止采样（已采到的历史保留在曲线上）";
    }

    private async Task SampleOnceAsync()
    {
        if (!IsSampling) return;
        try
        {
            var snapshot = await _client.GetStatsSnapshotAsync(CancellationToken.None);
            foreach (var s in snapshot)
            {
                // 表格路径来的 StatInfo 没有数值（HasNumbers=false），跳过而不是记 0 ——
                // 记 0 会在曲线上画出一个假的"归零"。
                if (!s.HasNumbers) continue;
                Append(_cpuHistory, s.Container, s.CpuPercent);
                Append(_memHistory, s.Container, s.MemUsedBytes / 1024d / 1024d); // 统一成 MB
            }
            RebuildSparklines();
        }
        catch (Exception ex)
        {
            // 采样是后台行为，失败不该反复弹 InfoBar：报一次 + 自动停采样。
            ShowError(ex);
            StopSampling();
        }
    }

    private static void Append(Dictionary<string, List<double>> bucket, string key, double value)
    {
        if (!bucket.TryGetValue(key, out var list))
        {
            list = new List<double>(MaxSamples);
            bucket[key] = list;
        }
        list.Add(value);
        if (list.Count > MaxSamples) list.RemoveAt(0);
    }

    /// <summary>
    /// 重画曲线。目标容器：优先统计页选中的行，回退到容器列表的选中项。
    /// 两条曲线都**自动按本批最大值缩放**，因此图上的绝对高度本身没有意义 ——
    /// 峰值数字放在 <see cref="SparkSummary"/> 里，避免读图误判。
    /// </summary>
    private void RebuildSparklines()
    {
        var target = SelectedStat?.Container;
        if (string.IsNullOrEmpty(target)) target = SelectedContainer?.Name;
        target ??= "";
        SparkTargetName = target.Length == 0 ? "（未选中容器）" : target;

        if (target.Length == 0 ||
            !_cpuHistory.TryGetValue(target, out var cpu) || cpu.Count == 0 ||
            !_memHistory.TryGetValue(target, out var mem) || mem.Count == 0)
        {
            CpuSparkPoints = new PointCollection();
            MemSparkPoints = new PointCollection();
            SparkSummary = target.Length == 0
                ? "选中一个容器后开始采样。"
                : "尚无采样数据 —— 点「开始采样」。";
            return;
        }

        CpuSparkPoints = ToPoints(SparklineGeometry.Build(cpu, SparkWidth, SparkHeight));
        MemSparkPoints = ToPoints(SparklineGeometry.Build(mem, SparkWidth, SparkHeight));
        SparkSummary = $"CPU 峰值 {cpu.Max():0.0}% · 内存峰值 {SizeParser.Format((long)(mem.Max() * 1024 * 1024))} · 样本 {cpu.Count}";
    }

    private static PointCollection ToPoints(IReadOnlyList<(double X, double Y)> points)
    {
        var collection = new PointCollection();
        foreach (var (x, y) in points)
            collection.Add(new Windows.Foundation.Point(x, y));
        return collection;
    }
}
