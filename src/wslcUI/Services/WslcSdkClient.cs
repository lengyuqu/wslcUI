using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.WSL.Containers;
using wslcUI.Models;

// Disambiguate the SDK's ImageInfo from wslcUI.Models.ImageInfo.
using UiImageInfo = wslcUI.Models.ImageInfo;

namespace wslcUI.Services;

/// <summary>
/// Real backend driving wslc via the Microsoft.WSL.Containers 2.9.9 SDK.
///
/// Coverage (verified against https://wsl.dev/api-reference/csharp/):
///   - IsReadyAsync        ← WslcService.GetMissingComponents()
///   - ListImagesAsync     ← Session.GetImages()               (pure SDK)
///   - PullImageAsync      ← Session.PullImageAsync()         (pure SDK)
///   - RunAndCaptureAsync  ← Session.CreateContainer() + Start (pure SDK)
///
/// SDK GAPS in 2.9.9 (the C# projection does not expose these, see
/// wsl.dev/api-reference/csharp/known-gaps/): there is no
/// Session.GetContainers() and no Session.GetContainer(name). Those and a
/// few more operations are bridged through the `wslc` CLI in WslcCli.cs:
///   - ListContainersAsync  ← `wslc list -a`
///   - StartAsync(name)      ← `wslc start &lt;name&gt;`
///   - StopAsync(name)       ← `wslc stop &lt;name&gt;`
///   - DeleteContainerAsync  ← `wslc rm &lt;name&gt;`
///   - DeleteImageAsync      ← Session.DeleteImage (session images, 2.9.9) / `wslc image rm` (CLI images)
///   - BuildImageAsync       ← `wslc build -t &lt;tag&gt; &lt;context&gt;`
///   - GetLogsAsync(name)   ← `wslc logs &lt;name&gt;`
///
/// Networks and volumes have NO C# projection at all, so every operation is
/// CLI-bridged (see WslcCli.cs):
///   - ListNetworksAsync    ← `wslc network ls`
///   - CreateNetworkAsync   ← `wslc network create &lt;name&gt;`
///   - RemoveNetworkAsync   ← `wslc network remove &lt;name&gt;`
///   - ListVolumesAsync     ← `wslc volume ls`
///   - CreateVolumeAsync    ← `wslc volume create &lt;name&gt;`
///   - RemoveVolumeAsync    ← `wslc volume remove &lt;name&gt;`
/// </summary>
public sealed class WslcSdkClient : IWslcClient, IDisposable
{
    private readonly string _storagePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "wslcUI", "session");

    private Session? _session;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<bool> IsReadyAsync(CancellationToken ct = default)
    {
        // GetMissingComponents() returns IReadOnlyList<Component>; empty == ready.
        var missing = WslcService.GetMissingComponents();
        return Task.FromResult(missing.Count == 0);
    }

    private async Task<Session> GetSessionAsync(CancellationToken ct)
    {
        if (_session is not null)
            return _session;

        await _gate.WaitAsync(ct);
        try
        {
            if (_session is not null)
                return _session;

            // Session stores its own image namespace here (separate from the
            // wslc CLI's WSL2 rootfs). ListImagesAsync bridges via the CLI
            // AND merges with Session.GetImages() so the UI sees both.
            Directory.CreateDirectory(_storagePath);
            var settings = new SessionSettings("wslcUI", _storagePath)
            {
                CpuCount = 2U,
                MemorySizeInMB = 2048U,
            };

            var session = new Session(settings);
            // 2.9.9: surface session-level lifecycle events instead of
            // swallowing them silently.
            session.Terminated += reason =>
                System.Diagnostics.Debug.WriteLine($"[wslcUI] session terminated: {reason}");
            session.ProcessCrashed += info =>
                System.Diagnostics.Debug.WriteLine($"[wslcUI] process crashed: {info.ProcessName} (pid {info.Pid})");
            session.Start();
            _session = session;
            return session;
        }
        finally
        {
            _gate.Release();
        }
    }

    // SDK gap → CLI bridge (see WslcCli.cs).
    public Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken ct = default) =>
        WslcCli.ListContainersAsync(ct);

    public async Task<IReadOnlyList<UiImageInfo>> ListImagesAsync(CancellationToken ct = default)
    {
        // Session.GetImages() sees only the Session's own storagePath (e.g. images
        // pulled by the SDK). `wslc images` sees the CLI's WSL2 rootfs. Merge both
        // so the UI shows the union, deduped by Id.
        var session = await GetSessionAsync(ct);
        var sdkImages = new List<UiImageInfo>();
        foreach (var img in session.GetImages())
        {
            var (repo, tag) = SplitName(img.Name);
            var hex = ToHex(img.Sha256);
            sdkImages.Add(new UiImageInfo
            {
                Id = hex,
                Repository = repo,
                Tag = tag,
                Size = FormatBytes(img.Size),
                // SDK 侧能拿到完整 sha256；CLI 侧只有短 IMAGE ID，Digest 留空。
                Digest = string.IsNullOrEmpty(hex) ? "" : "sha256:" + hex,
            });
        }

        var cliImages = await WslcCli.ListImagesAsync(ct);

        var byId = new Dictionary<string, UiImageInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in sdkImages) byId[i.Id] = i;
        foreach (var i in cliImages) byId.TryAdd(i.Id, i);
        return byId.Values.ToList();
    }

    public async Task PullImageAsync(
        string reference,
        IProgress<(string Status, long Current, long Total)>? progress = null,
        CancellationToken ct = default)
    {
        var session = await GetSessionAsync(ct);
        var pull = session.PullImageAsync(new PullImageOptions(reference));
        if (progress is not null)
        {
            pull.Progress = (_, p) => progress!.Report((p.Status.ToString(), (long)p.CurrentBytes, (long)p.TotalBytes));
        }
        await pull;
    }

    public async Task<string> RunAndCaptureAsync(string image, string[] command, CancellationToken ct = default)
    {
        var session = await GetSessionAsync(ct);

        var init = new ProcessSettings
        {
            CommandLine = new List<string>(command),
            OutputMode = ProcessOutputMode.Event,
        };

        var settings = new ContainerSettings(image) { InitProcess = init };

        using var container = session.CreateContainer(settings);

        var sb = new StringBuilder();
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        container.InitProcess.OutputReceived += data => sb.Append(Encoding.UTF8.GetString(data.ToArray()));
        container.InitProcess.ErrorReceived  += data => sb.Append(Encoding.UTF8.GetString(data.ToArray()));
        container.InitProcess.Exited        += code => tcs.TrySetResult(code);

        container.Start();
        try
        {
            await tcs.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // 取消时不能只抛异常：容器已经 Start，放着不管会在后台继续运行。
            // Stop 后不能 Start（2.9.9 限制），但 Delete(Force) 仍可用。
            try { container.Stop(Signal.SIGTERM, TimeSpan.FromSeconds(5)); } catch { /* best effort */ }
            container.Delete(DeleteContainerOption.Force);
            throw;
        }
        container.Delete(DeleteContainerOption.Force);
        return sb.ToString();
    }

    // SDK gap → CLI bridge (see WslcCli.cs).
    public Task StartAsync(string name, CancellationToken ct = default) =>
        WslcCli.StartAsync(name, ct);

    public Task StopAsync(string name, CancellationToken ct = default) =>
        WslcCli.StopAsync(name, ct);

    // SDK gap → CLI bridge (see WslcCli.cs).
    public Task DeleteContainerAsync(string name, CancellationToken ct = default) =>
        WslcCli.DeleteContainerAsync(name, ct);

    /// <summary>
    /// Removes an image. Namespace-aware since SDK 2.9.9: images pulled through
    /// the SDK session live in the session's own storage (invisible to `wslc image
    /// rm`) and are deleted via <c>Session.DeleteImage</c>; everything else still
    /// goes through the CLI bridge.
    /// </summary>
    public async Task DeleteImageAsync(string reference, CancellationToken ct = default)
    {
        Session? session = null;
        try { session = await GetSessionAsync(ct); }
        catch { /* session unavailable; fall through to the CLI bridge */ }

        if (session is not null)
        {
            var sdkImage = session.GetImages().FirstOrDefault(i =>
                string.Equals(i.Name, reference, StringComparison.OrdinalIgnoreCase));
            if (sdkImage is not null)
            {
                try
                {
                    session.DeleteImage(reference);
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
                {
                    // e.g. 0x80070020: the image is referenced by a running container.
                    throw new InvalidOperationException($"删除会话镜像失败: {ex.Message}", ex);
                }
                return;
            }
        }

        await WslcCli.DeleteImageAsync(reference, ct);
    }

    // ---- image build (no SDK projection for Dockerfile builds → CLI bridge) ----
    public Task BuildImageAsync(
        string contextDir, string tag, IProgress<string>? progress = null, CancellationToken ct = default) =>
        WslcCli.BuildImageAsync(contextDir, tag, progress, ct);

    public Task<string> GetLogsAsync(string name, CancellationToken ct = default) =>
        WslcCli.GetLogsAsync(name, ct);

    // ---- stats (no SDK projection → CLI bridge, see WslcCli.cs) ----
    public Task<IReadOnlyList<StatInfo>> GetStatsAsync(CancellationToken ct = default) =>
        WslcCli.GetStatsAsync(ct);

    // ---- networks (no SDK projection → CLI bridge, see WslcCli.cs) ----
    public Task<IReadOnlyList<NetworkInfo>> ListNetworksAsync(CancellationToken ct = default) =>
        WslcCli.ListNetworksAsync(ct);
    public Task CreateNetworkAsync(string name, CancellationToken ct = default) =>
        WslcCli.CreateNetworkAsync(name, ct);
    public Task RemoveNetworkAsync(string name, CancellationToken ct = default) =>
        WslcCli.RemoveNetworkAsync(name, ct);

    // ---- volumes (no SDK projection → CLI bridge, see WslcCli.cs) ----
    public Task<IReadOnlyList<VolumeInfo>> ListVolumesAsync(CancellationToken ct = default) =>
        WslcCli.ListVolumesAsync(ct);
    public Task CreateVolumeAsync(string name, CancellationToken ct = default) =>
        WslcCli.CreateVolumeAsync(name, ct);
    public Task RemoveVolumeAsync(string name, CancellationToken ct = default) =>
        WslcCli.RemoveVolumeAsync(name, ct);

    public void Dispose()
    {
        try { _session?.Terminate(); } catch { /* best effort */ }
        _session = null;
        _gate.Dispose();
    }

    // ---- helpers ----

    private static (string Repo, string Tag) SplitName(string name)
    {
        var i = name.LastIndexOf(':');
        return i < 0 ? (name, "latest") : (name[..i], name[(i + 1)..]);
    }

    private static string FormatBytes(ulong bytes)
    {
        double v = bytes;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return $"{v:0.##} {units[u]}";
    }

    private static string ToHex(Windows.Storage.Streams.IBuffer? buffer)
    {
        if (buffer is null) return "";
        var bytes = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(buffer);
        return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    }
}
