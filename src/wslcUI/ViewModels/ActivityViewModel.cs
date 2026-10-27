using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using wslcUI.Models;
using wslcUI.Services;

namespace wslcUI.ViewModels;

/// <summary>
/// 活动页（<c>wslc events</c> 实时流）的页面级 ViewModel。
///
/// <para>
/// 第一个从 <see cref="MainViewModel"/> 拆出来的子 VM —— 它只依赖
/// <see cref="EventStreamService"/> 与一个 UI 调度队列，状态自洽，
/// 不需要知道容器 / 镜像 / 卷的任何事。
/// </para>
///
/// <para>
/// ⚠️ 事件流是**永不结束**的长驻进程（实测：带 --since 也不自行退出，
/// 退出码 124 = 被 timeout 杀），所以：① 绝不 ReadToEndAsync；
/// ② 离开本页必须 <see cref="Stop"/>，否则会留下一个常驻 wslc 进程。
/// 详见 <see cref="EventStreamService"/> 类注释。
/// </para>
///
/// <para>
/// 与 <see cref="MainViewModel"/> 的耦合只剩「弹提示」「写状态栏」「报错」三件事，
/// 全部走事件上抛，由外壳决定怎么呈现 —— 子 VM 不碰 InfoBar 的具体状态。
/// </para>
/// </summary>
public sealed partial class ActivityViewModel : ObservableObject
{
    /// <summary>请求弹 InfoBar（消息 + 严重度）。</summary>
    public event EventHandler<(string Message, InfoBarSeverity Severity)>? NotifyRequested;

    /// <summary>请求写状态栏文本。</summary>
    public event EventHandler<string>? StatusRequested;

    /// <summary>请求按统一规则报错（异常格式化到 InfoBar）。</summary>
    public event EventHandler<Exception>? ErrorRaised;

    [ObservableProperty] public partial ObservableCollection<ContainerEvent> Events { get; set; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActivitySummary))]
    [NotifyPropertyChangedFor(nameof(EventStreamButtonText))]
    public partial bool IsEventStreamRunning { get; set; }

    [ObservableProperty] public partial bool HasEvents { get; set; }

    /// <summary>事件总数（受环形上限约束，故为「最近 N 条」）。</summary>
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
    /// UI 线程的调度队列，在 <see cref="Start"/> 时捕获。
    /// 事件回调来自 wslc 子进程的读取线程，那里 <c>GetForCurrentThread()</c> 返回
    /// null —— 必须提前存住 UI 队列才能把事件 marshal 回去。
    /// </summary>
    private Microsoft.UI.Dispatching.DispatcherQueue? _uiQueue;

    /// <summary>开始/停止事件流。进活动页自动开始，离开自动停止。</summary>
    [RelayCommand]
    private void ToggleEventStream()
    {
        if (_eventStream.IsRunning) Stop();
        else Start();
    }

    /// <summary>启动事件流（幂等）。由外壳在进入活动页时调用。</summary>
    public void Start(string? since = null)
    {
        if (_eventStream.IsRunning) return;

        // 事件回调在后台线程，必须先抓住 UI 队列（见 _uiQueue 注释）。
        _uiQueue ??= Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (_uiQueue is null)
        {
            NotifyRequested?.Invoke(this,
                ("当前环境没有 UI 调度队列，无法监听事件。", InfoBarSeverity.Warning));
            return;
        }

        // 先解绑再绑定：意外退出路径（OnExited）只把 IsEventStreamRunning 置 false、
        // 并不解绑，这里若直接 += 就给同一个事件挂了第二个处理器 ——
        // 活动流每行重复出现，而且每重启一次再多泄漏一个订阅。
        _eventStream.EventReceived -= OnEventReceived;
        _eventStream.ErrorReceived -= OnStreamError;
        _eventStream.Exited -= OnExited;
        _eventStream.EventReceived += OnEventReceived;
        _eventStream.ErrorReceived += OnStreamError;
        _eventStream.Exited += OnExited;
        try
        {
            _eventStream.Start(since);
            IsEventStreamRunning = true;
            StatusRequested?.Invoke(this, "正在监听容器事件…");
        }
        catch (Exception ex)
        {
            Stop();
            ErrorRaised?.Invoke(this, ex);
        }
    }

    /// <summary>停止事件流并解绑（幂等，重复调用安全）。</summary>
    public void Stop()
    {
        _eventStream.EventReceived -= OnEventReceived;
        _eventStream.ErrorReceived -= OnStreamError;
        _eventStream.Exited -= OnExited;
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

    private void OnStreamError(object? sender, string message)
    {
        var q = _uiQueue;
        if (q is null) return;
        void Show() => NotifyRequested?.Invoke(this,
            ($"事件流出错：{message}", InfoBarSeverity.Warning));
        if (q.HasThreadAccess) Show(); else q.TryEnqueue(Show);
    }

    private void OnExited(object? sender, int exitCode)
    {
        var q = _uiQueue;
        if (q is null) return;
        void Show()
        {
            IsEventStreamRunning = false;
            NotifyRequested?.Invoke(this,
                ($"事件流已退出（exit {exitCode}）。可点「开始监听」重新开始。",
                 InfoBarSeverity.Informational));
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
}
