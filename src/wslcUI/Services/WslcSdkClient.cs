using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.WSL.Containers;
using wslcUI.Models;

// Disambiguate the SDK's ImageInfo from wslcUI.Models.ImageInfo.
using UiImageInfo = wslcUI.Models.ImageInfo;

namespace wslcUI.Services;

/// <summary>
/// Real backend driving wslc via the Microsoft.WSL.Containers 2.9.3 SDK.
///
/// Coverage (verified against https://wsl.dev/api-reference/csharp/):
///   - IsReadyAsync        ← WslcService.GetMissingComponents()
///   - ListImagesAsync     ← Session.GetImages()               (pure SDK)
///   - PullImageAsync      ← Session.PullImageAsync()         (pure SDK)
///   - RunAndCaptureAsync  ← Session.CreateContainer() + Start (pure SDK)
///
/// SDK GAPS in 2.9.3 (the C# projection does not expose these, see
/// wsl.dev/api-reference/csharp/known-gaps/): there is no
/// Session.GetContainers() and no Session.GetContainer(name). Those three
/// operations are bridged through the `wslc` CLI in WslcCli.cs:
///   - ListContainersAsync  ← `wslc list -a`
///   - StartAsync(name)      ← `wslc start &lt;name&gt;`
///   - StopAsync(name)       ← `wslc stop &lt;name&gt;`
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

            Directory.CreateDirectory(_storagePath);
            var settings = new SessionSettings("wslcUI", _storagePath)
            {
                CpuCount = 2,
                MemorySizeInMB = 2048,
            };

            var session = new Session(settings);
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
        var session = await GetSessionAsync(ct);
        var list = new List<UiImageInfo>();
        foreach (var img in session.GetImages())
        {
            var (repo, tag) = SplitName(img.Name);
            list.Add(new UiImageInfo
            {
                Id = ToHex(img.Sha256),
                Repository = repo,
                Tag = tag,
                Size = FormatBytes(img.Size),
            });
        }
        return list;
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
        container.InitProcess.OutputReceived += data => sb.Append(Encoding.UTF8.GetString(data));
        container.InitProcess.ErrorReceived  += data => sb.Append(Encoding.UTF8.GetString(data));
        container.InitProcess.Exited        += code => tcs.TrySetResult(code);

        container.Start();
        await tcs.Task.WaitAsync(ct);
        container.Delete(DeleteContainerOption.None);
        return sb.ToString();
    }

    // SDK gap → CLI bridge (see WslcCli.cs).
    public Task StartAsync(string name, CancellationToken ct = default) =>
        WslcCli.StartAsync(name, ct);

    public Task StopAsync(string name, CancellationToken ct = default) =>
        WslcCli.StopAsync(name, ct);

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
