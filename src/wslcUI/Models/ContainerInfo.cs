namespace wslcUI.Models;

using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

/// <summary>容器状态的语义分类。UI 据此选择徽章配色，避免把状态文案直接当颜色依据。</summary>
public enum ContainerStatusKind
{
    Unknown,
    Running,
    Stopped,
    Error,
}

/// <summary>
/// UI-facing projection of a wslc container.
/// 实现 INPC：stats 回填（MergeStatsIntoContainers）会就地改写实例字段，
/// 详情面板的 <c>SelectedContainer.Cpu</c> 等嵌套 OneWay 绑定依赖 INPC 才能实时刷新。
/// </summary>
public class ContainerInfo : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ---- 标识字段构造后不变，auto-prop 即可 ----
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    private string _image = "";
    public string Image { get => _image; set { if (_image == value) return; _image = value; Raise(); } }

    private string _status = "";
    /// <summary>原始状态文案（来自 `wslc list -a`，中英文 locale 都可能）。变化时联动全部派生状态。</summary>
    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            Raise();
            Raise(nameof(StatusKind));
            Raise(nameof(IsRunning));
            Raise(nameof(IsStopped));
            Raise(nameof(IsError));
            Raise(nameof(IsUnknownStatus));
            Raise(nameof(StatusLabel));
        }
    }

    private string _ports = "—";
    /// <summary>端口映射。来自 `wslc list -a` 的「端口 / PORTS」列，CLI 未提供时为 "—"。</summary>
    public string Ports { get => _ports; set { if (_ports == value) return; _ports = value; Raise(); } }

    private string _createdAt = "—";
    /// <summary>创建时间。来自 `wslc list -a` 的「已创建 / CREATED」列。</summary>
    public string CreatedAt { get => _createdAt; set { if (_createdAt == value) return; _createdAt = value; Raise(); } }

    // ---- 以下字段由 `wslc stats` 快照按名称关联回填（stats 是一次性快照，可能为空）----
    private string _cpu = "—";
    public string Cpu { get => _cpu; set { if (_cpu == value) return; _cpu = value; Raise(); Raise(nameof(CpuValue)); } }

    private string _mem = "—";
    public string Mem { get => _mem; set { if (_mem == value) return; _mem = value; Raise(); } }

    private string _memPercent = "—";
    public string MemPercent { get => _memPercent; set { if (_memPercent == value) return; _memPercent = value; Raise(); Raise(nameof(MemPercentValue)); } }

    private string _netIo = "—";
    public string NetIo { get => _netIo; set { if (_netIo == value) return; _netIo = value; Raise(); } }

    private string _pids = "—";
    public string Pids { get => _pids; set { if (_pids == value) return; _pids = value; Raise(); } }

    private bool _hasStats;
    /// <summary>资源占用是否已有快照数据（无数据显示「—」，不用 0 冒充）。</summary>
    public bool HasStats { get => _hasStats; set { if (_hasStats == value) return; _hasStats = value; Raise(); } }

    // ---- 挂载点（来自 `wslc inspect <name>`，按需异步回填，未查时为空）----

    private IReadOnlyList<ContainerMount> _mounts = System.Array.Empty<ContainerMount>();

    /// <summary>
    /// 容器挂载列表。默认空列表；选中容器后由 MainViewModel 异步拉取并写回。
    /// 不与 CLI 的 <c>wslc list -a</c> 同源——后者只返容器元数据，Mounts 必须
    /// 单独 inspect，因此挂在选中触发而非 Refresh 一并拉（避免 N+1）。
    /// </summary>
    public IReadOnlyList<ContainerMount> Mounts
    {
        get => _mounts;
        set
        {
            var v = value ?? System.Array.Empty<ContainerMount>();
            if (ReferenceEquals(_mounts, v)) return;
            _mounts = v;
            Raise();
            Raise(nameof(HasMounts));
            Raise(nameof(MountCount));
            Raise(nameof(MountListCell));   // 派生属性依赖 _mounts，必须显式通知（OneWay 列表行卷列）
            Raise(nameof(MountsKvCell));
            Raise(nameof(MountSummary));
        }
    }

    /// <summary>是否有挂载数据。挂载数为 0 也算"已查过"——与未查区分给 UI 空态文案用。</summary>
    public bool HasMounts => _mounts.Count > 0;
    public int MountCount => _mounts.Count;

    /// <summary>状态栏摘要。空时返回空串，调用方判定 "未查" / "无挂载" 显示不同文案。</summary>
    public string MountSummary => _mounts.Count switch
    {
        0 => "",
        1 => "1 个挂载",
        _ => $"{_mounts.Count} 个挂载",
    };

    /// <summary>是否已完成 inspect 拉取（即便结果为空列表也算"已查"），用于隐藏 loading spinner。</summary>
    private bool _mountsLoaded;
    public bool MountsLoaded
    {
        get => _mountsLoaded;
        set { if (_mountsLoaded == value) return; _mountsLoaded = value; Raise(); }
    }

    /// <summary>已选中但 inspect 未返回（≤ 2 次 SP 间）——给 UI 显示"读取中…"。</summary>
    public bool IsMountLoading => !_mountsLoaded;

    /// <summary>已查但无挂载——给 UI 显示"未挂载…"空态文案。</summary>
    public bool IsMountEmpty => _mountsLoaded && _mounts.Count == 0;

    /// <summary>
    /// 列表行单元格短文本（90px 列宽）。未查 / 已查无挂载 → 「—」；
    /// 1 个 → 用 Left（volume 取 Name，否则取 Source，匿名回退）
    /// "pgdata"；多个 → "firstName +N" / "firstSource +N"。
    /// </summary>
    public string MountListCell
    {
        get
        {
            if (!_mountsLoaded || _mounts.Count == 0) return "—";
            var head = _mounts[0].Head;   // 单字符 "∅" 兜底，92px 列里不挤
            return _mounts.Count == 1 ? head : $"{head} +{_mounts.Count - 1}";
        }
    }

    private static string FirstLabel(ContainerMount m) => m.Head;

    /// <summary>
    /// 详情面板运行信息区挂载行单元格（与端口并列，"镜像 / 端口 / 卷 / 创建 / ID / 状态原文"）。
    /// 同上：未查 → "—"，已查空 → "—"，否则按 MountListCell 压缩逻辑。
    /// </summary>
    public string MountsKvCell => MountListCell;

    // ---- 派生状态：供 XAML 直接绑定（WinUI 3 无 DataTrigger，靠 Visibility 切换）----

    public ContainerStatusKind StatusKind => ClassifyStatus(Status);

    public bool IsRunning => StatusKind == ContainerStatusKind.Running;
    public bool IsStopped => StatusKind == ContainerStatusKind.Stopped;
    public bool IsError => StatusKind == ContainerStatusKind.Error;
    public bool IsUnknownStatus => StatusKind == ContainerStatusKind.Unknown;

    /// <summary>徽章文案。统一为中文，与 CLI locale 解耦。</summary>
    public string StatusLabel => StatusKind switch
    {
        ContainerStatusKind.Running => "运行中",
        ContainerStatusKind.Stopped => "已停止",
        ContainerStatusKind.Error => "异常退出",
        _ => string.IsNullOrWhiteSpace(Status) ? "未知" : Status,
    };

    /// <summary>CPU / 内存百分比数值（进度条用）。无快照时为 0，界面配合 HasStats 显示「—」。</summary>
    public double CpuValue => TryLeadingNumber(Cpu) ?? 0;
    public double MemPercentValue => TryLeadingNumber(MemPercent) ?? 0;

    private static ContainerStatusKind ClassifyStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status)) return ContainerStatusKind.Unknown;

        var s = status.Trim();
        if (Contains(s, "up") || Contains(s, "running") || s.Contains("运行", System.StringComparison.Ordinal))
            return ContainerStatusKind.Running;

        if (Contains(s, "exit") || Contains(s, "stopped") ||
            s.Contains("退出", System.StringComparison.Ordinal) ||
            s.Contains("停止", System.StringComparison.Ordinal))
        {
            // "Exited (1) …" / "退出 (1)" → 非零退出码视为异常。
            return ExitedWithNonZero(s) ? ContainerStatusKind.Error : ContainerStatusKind.Stopped;
        }

        if (Contains(s, "created") || s.Contains("已创建", System.StringComparison.Ordinal))
            return ContainerStatusKind.Stopped;

        return ContainerStatusKind.Unknown;
    }

    private static bool Contains(string s, string value) =>
        s.Contains(value, System.StringComparison.OrdinalIgnoreCase);

    private static bool ExitedWithNonZero(string s)
    {
        var open = s.IndexOf('(');
        var close = open >= 0 ? s.IndexOf(')', open) : -1;
        if (open < 0 || close < 0) return false;
        var code = s[(open + 1)..close].Trim();
        return int.TryParse(code, out var n) && n != 0;
    }

    private static double? TryLeadingNumber(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var i = 0;
        while (i < s.Length && !char.IsDigit(s[i]) && s[i] != '.' && s[i] != '-') i++;
        if (i >= s.Length) return null;
        var j = i;
        while (j < s.Length && (char.IsDigit(s[j]) || s[j] == '.')) j++;
        return double.TryParse(s[i..j], System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
