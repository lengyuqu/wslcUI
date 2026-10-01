namespace wslcUI.Models;

/// <summary>
/// A point-in-time resource-usage snapshot for one container. Mirrors the
/// <c>wslc stats</c> table columns
/// <c>容器 ID | 名称 | CPU 百分比 | 最大用量/限制 | 内存百分比 | 网络 I/O | 块 I/O | PIDS</c>
/// (Chinese-locale header on wslc 2.9.9.0). <c>wslc stats</c> is one-shot
/// by default — it does not accept docker's <c>--no-stream</c> flag.
/// Values are kept as display strings (parsing precision differs per wslc
/// build).
/// </summary>
public class StatInfo
{
    public string Container { get; set; } = "";
    public string Cpu { get; set; } = "";
    public string Mem { get; set; } = "";        // e.g. "12.3MiB / 2GiB"
    public string MemPercent { get; set; } = "";
    public string NetIo { get; set; } = "";
    public string BlockIo { get; set; } = "";
    public string Pids { get; set; } = "";

    // ---- 数值形态（仅 JSON 路径填充；表格路径保持字符串，见 WslcCli.ParseStats）----
    // 曲线只需要这两个数，所以不做全套数值化：CPU% 与内存用量。
    // HasNumbers 为 false 时曲线上不应出现这个点（表格路径就是这种情况）。

    /// <summary>CPU 占用百分数（`0.00` / `2.20`）。</summary>
    public double CpuPercent { get; set; }

    /// <summary>内存用量（字节）。用于曲线；`Mem` 仍是给人看的字符串。</summary>
    public long MemUsedBytes { get; set; }

    /// <summary>是否来自 JSON 路径（数值可信、可入曲线）。</summary>
    public bool HasNumbers { get; set; }
}
