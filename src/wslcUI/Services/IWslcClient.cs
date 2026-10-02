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

    // ---------------- 事件流（一次性历史回读）----------------

    /// <summary>
    /// 拉取一段历史事件（CLI bridge: <c>wslc events --since &lt;since&gt;</c>）。
    /// <paramref name="since"/> 是回看窗口（<c>5m</c> / <c>2h</c>），<c>null</c> 时默认 5m。
    ///
    /// <para>
    /// ⚠️ <b>wslc events 永不自行退出</b>（实测 2026-10-02，wslc 3.0.1.0）：即使同时给
    /// <c>--until</c>，它也只把窗口内的事件回放一遍，然后继续挂在 stdout 上等新事件。
    /// 因此这里<b>不是</b>「跑一次命令读全部 stdout」的一次性调用（那会永久挂死），
    /// 而是逐行读 + <b>空闲即止</b>：连续一小段时间没有新行即判定「回放完毕」并主动结束。
    /// </para>
    ///
    /// <para>
    /// <b>持续监听不在这里</b> —— 长驻流不进接口（接口方法应是「调一次、拿结果、返回」）。
    /// 监听用 <c>Services/EventStreamService.cs</c>，由 ViewModel 直接持有其生命周期。
    /// </para>
    /// </summary>
    Task<IReadOnlyList<ContainerEvent>> ListEventsAsync(
        string? since, CancellationToken ct = default);

    // ---------------- 镜像 push / tag / save / load / import ----------------
    // wslc 3.0.1 新增的 CLI 能力，SDK 3.0.1 无对应 C# 投影。
    // ⚠️ 参数形态与 docker 有实质差异（已逐条核实 `--help`），详见 WslcCli 实现处注释：
    //   push 只有**一个**位置参数；save 的 <c>&lt;image&gt;...</c> 必填且**无「导出全部」**形态；
    //   load 用 <c>-i</c>；import 的 tar 是**必填位置参数**且**没有 -o</c>。

    /// <summary>
    /// 推送镜像到注册表（CLI bridge: <c>wslc image push [-a] [-q] &lt;image&gt;</c>）。
    /// <paramref name="reference"/> 必须**自带仓库前缀**（如 <c>reg.example.com/app:v1</c>）——
    /// wslc 与 docker 不同，只接受**一个**位置参数，没有「源 + 目标」双参数形态。
    /// </summary>
    Task PushImageAsync(
        string reference,
        bool allTags = false,
        bool quiet = false,
        IProgress<string>? progress = null,
        CancellationToken ct = default);

    /// <summary>给镜像打标签（CLI bridge: <c>wslc image tag &lt;source&gt; &lt;target&gt;</c>）。</summary>
    Task TagImageAsync(string source, string target, CancellationToken ct = default);

    /// <summary>
    /// 导出镜像为 tar 归档（CLI bridge: <c>wslc image save -o &lt;output&gt; &lt;image&gt;...</c>）。
    /// ⚠️ <paramref name="references"/> 至少一个；wslc **没有**「导出全部镜像」的形态。
    /// </summary>
    /// <returns>CLI 原始 stdout。</returns>
    Task<string> SaveImagesAsync(
        IReadOnlyList<string> references, string outputPath, CancellationToken ct = default);

    /// <summary>从 tar 导入镜像（CLI bridge: <c>wslc image load -i &lt;input&gt; [-q]</c>）。</summary>
    /// <returns>CLI 原始 stdout（每行一个「已加载映像: name:tag」）。</returns>
    Task<string> LoadImagesAsync(
        string inputPath, bool quiet = false, CancellationToken ct = default);

    /// <summary>
    /// 从 tarball 导入并可同时重命名（CLI bridge: <c>wslc image import &lt;file&gt; [&lt;image&gt;]</c>）。
    /// tar 是**必填位置参数**，<b>没有 <c>-o</c></b>。
    /// </summary>
    /// <returns>CLI 原始 stdout（新镜像短 ID）。</returns>
    Task<string> ImportImageAsync(
        string filePath, string? reference = null, CancellationToken ct = default);

    // ---------------- registry 登录 ----------------

    /// <summary>
    /// 登录镜像仓库（CLI bridge:
    /// <c>wslc registry login -u &lt;user&gt; --password-stdin [&lt;server&gt;]</c>）。
    ///
    /// <para>
    /// ⚠️ 实现**必须**走 <c>--password-stdin</c>，**不得用 <c>-p</c>**：
    /// <c>-p</c> 会把密码暴露在进程命令行上（同机任何用户可见，且可能被日志 /
    /// 崩溃转储记录）。密码只经管道进子进程，**不落盘、不进日志、不进异常消息**。
    /// </para>
    /// <para><paramref name="server"/> 可为 null —— wslc 此时用会话定义的默认服务器。</para>
    /// </summary>
    Task RegistryLoginAsync(
        string? server, string? username, string password, CancellationToken ct = default);

    /// <summary>从镜像仓库注销（CLI bridge: <c>wslc registry logout [&lt;server&gt;]</c>）。</summary>
    Task RegistryLogoutAsync(string? server = null, CancellationToken ct = default);

    // ---------------- 容器导出 / 强杀 ----------------

    /// <summary>
    /// 导出容器文件系统为 tar（CLI bridge:
    /// <c>wslc container export -o &lt;output&gt; &lt;container&gt;</c>）。
    /// 这是**文件系统**导出（不含卷挂载与元数据），与 <c>image save</c> 不是一回事。
    /// </summary>
    Task ExportContainerAsync(
        string container, string outputPath, CancellationToken ct = default);

    /// <summary>
    /// 强杀容器（CLI bridge: <c>wslc container kill [-s &lt;signal&gt;] &lt;container&gt;</c>）。
    /// 默认信号 <c>SIGKILL</c>。与 <see cref="StopAsync"/> 的区别：<c>stop</c> 是优雅停止
    /// （SIGTERM + 等退出），容器卡死时只能靠本方法强杀。
    /// </summary>
    Task KillContainerAsync(
        string container, string? signal = null, CancellationToken ct = default);

    // ---------------- 网络接入 / 断开 ----------------
    // ⚠️ 位置参数顺序是「网络名 + 容器」，与 docker 一致，**不要传反**。
    // help 把第二个写作 container-id，但**实测容器名同样可用**。

    /// <summary>把容器接入已有网络（CLI bridge: <c>wslc network connect &lt;network&gt; &lt;container&gt;</c>）。</summary>
    Task ConnectNetworkAsync(
        string network, string container, CancellationToken ct = default);

    /// <summary>把容器从网络断开（CLI bridge: <c>wslc network disconnect &lt;network&gt; &lt;container&gt;</c>）。</summary>
    Task DisconnectNetworkAsync(
        string network, string container, CancellationToken ct = default);

    // ---------------- 系统信息 ----------------

    /// <summary>
    /// 系统与会话信息（CLI bridge: <c>wslc system info</c>）—— 客户端 / 内核 / Windows 版本、
    /// <c>settings.yaml</c> 路径、活跃会话列表。SDK 3.0.1 无对应查询投影。
    /// </summary>
    Task<SystemInfo> GetSystemInfoAsync(CancellationToken ct = default);
}
