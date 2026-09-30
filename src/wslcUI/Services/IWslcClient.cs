using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using wslcUI.Models;

namespace wslcUI.Services;

/// <summary>
/// Backend abstraction for the GUI. The ViewModel depends only on this
/// interface, so the real wslc SDK client and a fake/dev client are swappable
/// without touching the UI (see App.xaml.cs).
/// </summary>
public interface IWslcClient
{
    /// <summary>True when the required WSL components are installed.</summary>
    Task<bool> IsReadyAsync(CancellationToken ct = default);

    Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken ct = default);

    Task<IReadOnlyList<ImageInfo>> ListImagesAsync(CancellationToken ct = default);

    Task PullImageAsync(
        string reference,
        IProgress<(string Status, long Current, long Total)>? progress = null,
        CancellationToken ct = default);

    /// <summary>Runs a command inside a throwaway container and returns combined output.</summary>
    Task<string> RunAndCaptureAsync(string image, string[] command, CancellationToken ct = default);

    Task StartAsync(string name, CancellationToken ct = default);

    Task StopAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// 重启容器（CLI bridge: <c>wslc restart &lt;name&gt;</c>，wslc 2.9.12+）。
    /// 未运行的容器会被直接启动。SDK 3.0.1 仍无 Restart() C# 投影，只能走 CLI。
    /// </summary>
    Task RestartAsync(string name, CancellationToken ct = default);

    /// <summary>Removes a container by name (CLI bridge: `wslc rm`).</summary>
    Task DeleteContainerAsync(string name, CancellationToken ct = default);

    /// <summary>Removes an image by reference (CLI bridge: `wslc image rm`).</summary>
    Task DeleteImageAsync(string reference, CancellationToken ct = default);

    /// <summary>Builds an image from a Dockerfile in <paramref name="contextDir"/> (CLI bridge: `wslc build -t`).</summary>
    Task BuildImageAsync(
        string contextDir,
        string tag,
        IProgress<string>? progress = null,
        CancellationToken ct = default);

    /// <summary>Returns captured logs for a container (CLI bridge: `wslc logs`).</summary>
    Task<string> GetLogsAsync(string name, CancellationToken ct = default);

    /// <summary>Point-in-time resource usage snapshot (CLI bridge: `wslc stats`). wslc is one-shot by default; it rejects docker's `--no-stream` flag.</summary>
    Task<IReadOnlyList<StatInfo>> GetStatsAsync(CancellationToken ct = default);

    /// <summary>Lists networks (CLI bridge: `wslc network list`).</summary>
    Task<IReadOnlyList<NetworkInfo>> ListNetworksAsync(CancellationToken ct = default);

    /// <summary>Creates a network by name (CLI bridge: `wslc network create`).</summary>
    Task CreateNetworkAsync(string name, CancellationToken ct = default);

    /// <summary>Removes a network by name (CLI bridge: `wslc network remove`).</summary>
    Task RemoveNetworkAsync(string name, CancellationToken ct = default);

    /// <summary>Lists volumes (CLI bridge: `wslc volume list`).</summary>
    Task<IReadOnlyList<VolumeInfo>> ListVolumesAsync(CancellationToken ct = default);

    /// <summary>Creates a volume by name (CLI bridge: `wslc volume create`).</summary>
    Task CreateVolumeAsync(string name, CancellationToken ct = default);

    /// <summary>Removes a volume by name (CLI bridge: `wslc volume remove`).</summary>
    Task RemoveVolumeAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// 读取容器的完整 inspect 元数据，仅取 <c>Mounts</c> 数组。
    /// CLI bridge：<c>wslc inspect &lt;name&gt; --format json</c>。
    /// SDK 3.0.1 仍无 inspect 投影，且 <c>wslc list -a</c> 不返回挂载关联——
    /// 调用方（详情面板）选中容器时按需拉取，避免 Refresh 主路径 N+1。
    /// </summary>
    Task<IReadOnlyList<ContainerMount>> InspectContainerAsync(string name, CancellationToken ct = default);
}
