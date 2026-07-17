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

    public Task<string> GetLogsAsync(string name, CancellationToken ct = default) =>
        Task.FromResult($"[fake logs] container {name} is running…");
}
