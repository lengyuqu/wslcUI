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
}
