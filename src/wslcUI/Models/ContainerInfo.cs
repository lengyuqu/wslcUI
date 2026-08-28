namespace wslcUI.Models;

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
