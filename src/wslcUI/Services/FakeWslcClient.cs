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
}
