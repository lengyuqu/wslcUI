using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using wslcUI.Services;

namespace wslcUI.ViewModels;

/// <summary>
/// 统计页的实时采样与 sparkline 绘制。
///
/// <para>
/// <c>wslc stats</c> 是**一次性快照、不流式**，所以「实时曲线」只能定时轮询自己攒历史。
/// 历史按容器名分桶、每桶最多 <see cref="MaxSamples"/> 个点（环形丢弃最旧的）。
/// </para>
///
/// <para>
/// 从 <see cref="MainViewModel"/> 拆出来的第五个子 VM：它只依赖一条
/// <c>GetStatsSnapshotAsync</c> 和一个 UI 调度队列，全部状态（历史桶、计时器、
/// 曲线点集）自我闭环。与壳的耦合只有两件事：状态栏文案、错误上报；
/// 外加一个入口 <see cref="SetTarget"/> 告诉它「现在该画谁」。
/// </para>
/// </summary>
public sealed partial class StatsSamplingViewModel : ObservableObject
{
    private readonly IWslcClient _client;

    /// <summary>请求写状态栏文本。</summary>
    public event EventHandler<string>? StatusRequested;

    /// <summary>请求弹提示条（如「没有 UI 调度队列」这种环境限制，属提示而非报错）。</summary>
    public event EventHandler<(string Message, Microsoft.UI.Xaml.Controls.InfoBarSeverity Severity)>? NotifyRequested;

    /// <summary>请求按统一规则报错。</summary>
    public event EventHandler<Exception>? ErrorRaised;

    [ObservableProperty] public partial bool IsSampling { get; set; }
    [ObservableProperty] public partial string SparkTargetName { get; set; } = "";
    [ObservableProperty] public partial string SparkSummary { get; set; } = "";
    [ObservableProperty] public partial PointCollection CpuSparkPoints { get; set; } = new();
    [ObservableProperty] public partial PointCollection MemSparkPoints { get; set; } = new();

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

    /// <summary>当前要画的容器名（由壳在选中项变化时设置）。</summary>
    private string _target = "";

    public StatsSamplingViewModel(IWslcClient client)
    {
        _client = client;
        // 先画一次空态：否则 SparkTargetName / SparkSummary 会停在空串，
        // 详情面板在没有选中项时显示一片空白（拆分前由构造后的首帧 RebuildSparklines 兜住）。
        Rebuild();
    }

    /// <summary>
    /// 指定曲线目标。壳在「统计页选中行」或「容器列表选中项」变化时调用；
    /// 传 null/空串表示当前没有目标（曲线清空并提示）。
    /// </summary>
    public void SetTarget(string? containerName)
    {
        var next = containerName ?? "";
        if (next == _target) return;
        _target = next;
        Rebuild();
    }

    [RelayCommand]
    private void ToggleSampling()
    {
        if (IsSampling) { Stop(); return; }
        Start();
    }

    private void Start()
    {
        // DispatcherQueueTimer 必须在有 UI 调度队列的线程上创建；没有就明确报错，
        // 不要假装在采样（否则用户会以为图停住了是数据问题）。
        var queue = DispatcherQueue.GetForCurrentThread();
        if (queue is null)
        {
            // 环境限制而非故障：用提示条（Warning）而不是错误条，与原实现一致。
            NotifyRequested?.Invoke(this,
                ("当前环境没有 UI 调度队列，无法启动实时采样。",
                 Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning));
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
        StatusRequested?.Invoke(this, $"实时采样中（每 {SampleInterval.TotalSeconds:0} 秒一次）");
        _ = SampleOnceAsync(); // 立即取一次，不等第一个 tick
    }

    /// <summary>停止采样（幂等）。离开统计页与 Dispose 都会调。</summary>
    public void Stop()
    {
        _sampler?.Stop();
        IsSampling = false;
        StatusRequested?.Invoke(this, "已停止采样（已采到的历史保留在曲线上）");
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
                // 记 0 会在曲线上画出一个假的「归零」。
                if (!s.HasNumbers) continue;
                Append(_cpuHistory, s.Container, s.CpuPercent);
                Append(_memHistory, s.Container, s.MemUsedBytes / 1024d / 1024d); // 统一成 MB
            }
            Rebuild();
        }
        catch (Exception ex)
        {
            // 采样是后台行为，失败不该反复弹 InfoBar：报一次 + 自动停采样。
            ErrorRaised?.Invoke(this, ex);
            Stop();
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
    /// 重画曲线。两条曲线都**自动按本批最大值缩放**，因此图上的绝对高度本身没有意义
    /// —— 峰值数字放在 <see cref="SparkSummary"/> 里，避免读图误判。
    /// </summary>
    private void Rebuild()
    {
        var target = _target;
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
        SparkSummary = $"CPU 峰值 {cpu.Max():0.0}% · " +
                       $"内存峰值 {SizeParser.Format((long)(mem.Max() * 1024 * 1024))} · " +
                       $"样本 {cpu.Count}";
    }

    private static PointCollection ToPoints(IReadOnlyList<(double X, double Y)> points)
    {
        var collection = new PointCollection();
        foreach (var (x, y) in points)
            collection.Add(new Windows.Foundation.Point(x, y));
        return collection;
    }
}
