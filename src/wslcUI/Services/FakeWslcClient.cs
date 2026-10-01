using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using wslcUI.Models;

namespace wslcUI.Services;

/// <summary>
/// In-memory backend for UI development without WSL installed. Swap it in
/// App.xaml.cs to design the XAML/ViewModel flow offline.
/// </summary>
public sealed class FakeWslcClient : IWslcClient
{
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(true);

    public Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ContainerInfo>>(new List<ContainerInfo>
        {
            new() { Name = "web", Image = "nginx:latest", Status = "Running" },
            new() { Name = "db", Image = "redis:latest", Status = "Stopped" },
        });

    public Task<IReadOnlyList<ImageInfo>> ListImagesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ImageInfo>>(new List<ImageInfo>
        {
            new() { Repository = "nginx", Tag = "latest", Size = "187 MB" },
            new() { Repository = "alpine", Tag = "latest", Size = "7.3 MB" },
        });

    public Task PullImageAsync(
        string reference,
        IProgress<(string Status, long Current, long Total)>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report((reference, 100, 100));
        return Task.CompletedTask;
    }

    public Task<string> RunAndCaptureAsync(string image, string[] command, CancellationToken ct = default) =>
        Task.FromResult($"fake output from {image}: {string.Join(' ', command)}");

    public Task StartAsync(string name, CancellationToken ct = default) => Task.CompletedTask;

    public Task StopAsync(string name, CancellationToken ct = default) => Task.CompletedTask;

    public Task RestartAsync(string name, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteContainerAsync(string name, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task DeleteImageAsync(string reference, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task BuildImageAsync(
        string contextDir, string tag, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report($"[fake] 从 {contextDir} 构建镜像 {tag} …");
        progress?.Report("[fake] STEP 1/5 : 解析 Dockerfile");
        progress?.Report("[fake] STEP 3/5 : 拉取基础镜像");
        progress?.Report($"[fake] STEP 5/5 : 成功标记 {tag}");
        return Task.CompletedTask;
    }

    public Task<string> GetLogsAsync(string name, CancellationToken ct = default) =>
        Task.FromResult($"[fake logs] container {name} is running…");

    public Task<IReadOnlyList<StatInfo>> GetStatsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StatInfo>>(new List<StatInfo>
        {
            new() { Container = "web", Cpu = "0.52%", Mem = "14.2MiB / 2GiB", MemPercent = "0.69%", NetIo = "1.2kB / 0B", BlockIo = "0B / 0B", Pids = "12" },
            new() { Container = "db", Cpu = "2.31%", Mem = "48.7MiB / 2GiB", MemPercent = "2.38%", NetIo = "3.4kB / 1.1kB", BlockIo = "2.0MB / 0B", Pids = "21" },
        });

    public Task<IReadOnlyList<StatInfo>> GetStatsSnapshotAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StatInfo>>(new List<StatInfo>
        {
            new() { Container = "web", Cpu = "0.52%", Mem = "14.2MiB / 2GiB", MemPercent = "0.69%",
                    NetIo = "1.2kB / 0B", BlockIo = "0B / 0B", Pids = "12",
                    CpuPercent = 0.52, MemUsedBytes = 14889779, HasNumbers = true },
            new() { Container = "db", Cpu = "2.31%", Mem = "48.7MiB / 2GiB", MemPercent = "2.38%",
                    NetIo = "3.4kB / 1.1kB", BlockIo = "2.0MB / 0B", Pids = "21",
                    CpuPercent = 2.31, MemUsedBytes = 51060327, HasNumbers = true },
        });

    public Task<IReadOnlyList<NetworkInfo>> ListNetworksAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<NetworkInfo>>(new List<NetworkInfo>
        {
            new() { Name = "bridge", Driver = "bridge", Scope = "local" },
            new() { Name = "wslcUI-net", Driver = "bridge", Scope = "local" },
        });

    public Task CreateNetworkAsync(string name, CancellationToken ct = default) => Task.CompletedTask;

    public Task RemoveNetworkAsync(string name, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<VolumeInfo>> ListVolumesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<VolumeInfo>>(new List<VolumeInfo>
        {
            new() { Name = "data-vol", Driver = "guest" },
            new() { Name = "cache-vol", Driver = "vhd" },
        });

    public Task CreateVolumeAsync(string name, CancellationToken ct = default) => Task.CompletedTask;

    public Task RemoveVolumeAsync(string name, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<ContainerMount>> InspectContainerAsync(string name, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ContainerMount>>(name switch
        {
            "web" => new List<ContainerMount>
            {
                new() { Name = "web-data", Destination = "/usr/share/nginx/html",
                        Source = "web-data", Type = "volume", ReadWrite = true },
            },
            "db" => new List<ContainerMount>
            {
                // 双卷覆盖多挂载显示 + RO 徽章
                new() { Name = "redis-data", Destination = "/data",
                        Source = "redis-data", Type = "volume", ReadWrite = true },
                new() { Name = "", Destination = "/etc/redis/conf.d",
                        Source = "/Users/me/cfg/redis", Type = "bind", ReadWrite = false },
            },
            _ => Array.Empty<ContainerMount>(),
        });

    // ---- prune（离线开发用：回显一段与真实 CLI 同形状的输出）----
    public Task<string> PruneContainersAsync(CancellationToken ct = default) =>
        Task.FromResult("Deleted Containers:\n0f1e2d3c4b5a\n\nTotal reclaimed space: 0B\n");

    public Task<string> PruneImagesAsync(bool all, CancellationToken ct = default) =>
        Task.FromResult(all
            ? "Deleted Images:\nuntagged: nginx:latest\n\nTotal reclaimed space: 187MB\n"
            : "Total reclaimed space: 0B\n");

    public Task<string> PruneNetworksAsync(CancellationToken ct = default) =>
        Task.FromResult("Deleted Networks:\nwslcUI-net\n");

    public Task<string> PruneVolumesAsync(CancellationToken ct = default) =>
        Task.FromResult("Deleted Volumes:\ncache-vol\n");

    // ---- 容器内文件系统（离线开发用假树）----
    public Task<IReadOnlyList<ContainerFileEntry>> ListDirectoryAsync(
        string container, string path, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ContainerFileEntry>>(path switch
        {
            "/" => new List<ContainerFileEntry>
            {
                new() { Name = "etc", Kind = ContainerFileKind.Directory, Permissions = "drwxr-xr-x",
                        Owner = "root", Group = "root", SizeBytes = 4096, Modified = "Jun 13 16:38" },
                new() { Name = "usr", Kind = ContainerFileKind.Directory, Permissions = "drwxr-xr-x",
                        Owner = "root", Group = "root", SizeBytes = 4096, Modified = "Jun 13 16:38" },
                new() { Name = "etc-link", Kind = ContainerFileKind.Link, Permissions = "lrwxrwxrwx",
                        Owner = "root", Group = "root", SizeBytes = 4, Modified = "Oct 1 16:44",
                        LinkTarget = "/etc" },
                new() { Name = "name with space.txt", Kind = ContainerFileKind.File,
                        Permissions = "-rw-r--r--", Owner = "root", Group = "root",
                        SizeBytes = 3, Modified = "Oct 1 16:44" },
            },
            _ => new List<ContainerFileEntry>
            {
                new() { Name = "alpine-release", Kind = ContainerFileKind.File,
                        Permissions = "-rw-r--r--", Owner = "root", Group = "root",
                        SizeBytes = 7, Modified = "Jun 13 15:17" },
                new() { Name = "apk", Kind = ContainerFileKind.Directory, Permissions = "drwxr-xr-x",
                        Owner = "root", Group = "root", SizeBytes = 4096, Modified = "Jun 13 16:38" },
            },
        });

    public Task CopyFromContainerAsync(
        string container, string containerPath, string localPath, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task CopyToContainerAsync(
        string container, string localPath, string containerPath, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task DeletePathAsync(string container, string path, CancellationToken ct = default) =>
        Task.CompletedTask;
}
