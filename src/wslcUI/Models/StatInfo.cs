namespace wslcUI.Models;

/// <summary>
/// A point-in-time resource-usage snapshot for one container. Mirrors the
/// docker-style `wslc stats --no-stream` table columns
/// (CONTAINER ID | NAME | CPU % | MEM USAGE / LIMIT | MEM % | NET I/O | BLOCK I/O | PIDS).
/// Values are kept as display strings (parsing precision differs per wslc build).
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
