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

    /// <summary>
    /// 数值形态的 resources 快照（CLI bridge: `wslc stats -a --format json`），供实时采样/曲线用。
    /// 与 <see cref="GetStatsAsync"/> 的区别只有路径：这个走 JSON 拿可计算的数值。
    /// </summary>
    Task<IReadOnlyList<StatInfo>> GetStatsSnapshotAsync(CancellationToken ct = default);

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

    // ---------------- 清理（维护页）----------------
    // 四个 prune 都是 CLI 桥接（SDK 3.0.1 无对应投影）。
    // 一律带 `-f`：不带会弹交互式确认并挂住子进程（wslc 已对齐 docker 语义）。
    // 返回值是 CLI 的**原始输出**，UI 直接展示而不解析——prune 的回收量文案
    // 各家版本不同，解析会随版本漂移；原样透传永不过期。

    /// <summary>清理所有已停止的容器（CLI bridge: `wslc container prune -f`）。</summary>
    Task<string> PruneContainersAsync(CancellationToken ct = default);

    /// <summary>
    /// 清理镜像（CLI bridge: `wslc image prune -f [-a]`）。
    /// <paramref name="all"/> 为 false 只删悬空镜像；为 true 删所有未被容器使用的镜像。
    /// </summary>
    Task<string> PruneImagesAsync(bool all, CancellationToken ct = default);

    /// <summary>清理所有未被使用的网络（CLI bridge: `wslc network prune -f`）。</summary>
    Task<string> PruneNetworksAsync(CancellationToken ct = default);

    /// <summary>清理所有未被使用的卷（CLI bridge: `wslc volume prune -f`）。**会销毁卷内数据**。</summary>
    Task<string> PruneVolumesAsync(CancellationToken ct = default);

    // ---------------- 容器内文件系统（文件浏览窗口）----------------
    // 全部 CLI 桥接：SDK 3.0.1 没有文件系统投影。

    /// <summary>列出容器内某个目录（CLI bridge: `wslc exec &lt;ctr&gt; ls -la &lt;path&gt;`）。</summary>
    Task<IReadOnlyList<ContainerFileEntry>> ListDirectoryAsync(
        string container, string path, CancellationToken ct = default);

    /// <summary>从容器复制文件/目录到宿主（CLI bridge: `wslc container cp &lt;ctr&gt;:&lt;path&gt; &lt;local&gt;`）。目标可不存在。</summary>
    Task CopyFromContainerAsync(
        string container, string containerPath, string localPath, CancellationToken ct = default);

    /// <summary>
    /// 从宿主复制到容器（CLI bridge: `wslc container cp &lt;local&gt; &lt;ctr&gt;:&lt;dir&gt;/`）。
    /// ⚠️ <paramref name="containerDir"/> 必须是容器内**已存在的目录**，且**不能指定目标文件名**
    /// （沿用本地文件名）—— wslc 的 cp 与 docker 语义不同，实测 2026-10-02。
    /// </summary>
    Task CopyToContainerAsync(
        string container, string localPath, string containerDir, CancellationToken ct = default);

    /// <summary>
    /// 删除容器内的一个路径（CLI bridge: `wslc exec &lt;ctr&gt; rm -rf &lt;path&gt;`）。
    /// **递归且不可撤销** —— 调用方必须先确认。
    /// </summary>
    Task DeletePathAsync(string container, string path, CancellationToken ct = default);
}
