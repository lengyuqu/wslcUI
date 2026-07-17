using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.WSL.Containers;
using wslcUI.Models;

namespace wslcUI.Services;

/// <summary>
/// ⚠️ PREVIEW SDK — Microsoft.WSL.Containers 2.9.3.
/// Member names below were transcribed from the public API overview
/// (https://wsl.dev/api-reference/csharp/). The preview is subject to breaking
/// changes; reconcile the following before first build if a compile error appears:
///   - SessionSettings.MemoryMB  vs  MemorySizeInMB
///   - Session enumeration: ListContainersAsync / ListImagesAsync (exact name)
///   - Container lookup by name: GetContainer(name) or similar
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
        var missing = WslcService.GetMissingComponents();
        return Task.FromResult(missing == ComponentFlags.None);
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

    public async Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken ct = default)
    {
        // TODO: confirm enumeration API against wslc.dev/api-reference/csharp/
        var session = await GetSessionAsync(ct);
        await Task.Yield();
        return new List<ContainerInfo>();
    }

    public async Task<IReadOnlyList<ImageInfo>> ListImagesAsync(CancellationToken ct = default)
    {
        // TODO: see note in ListContainersAsync.
        var session = await GetSessionAsync(ct);
        await Task.Yield();
        return new List<ImageInfo>();
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
            pull.Progress = (op, p) => progress.Report((p.Status, p.CurrentBytes, p.TotalBytes));
        }
        await pull;
    }

    public async Task<string> RunAndCaptureAsync(string image, string[] command, CancellationToken ct = default)
    {
        var session = await GetSessionAsync(ct);

        var init = new ProcessSettings
        {
            CmdLine = new List<string>(command),
            OutputMode = ProcessOutputMode.Event,
        };

        var settings = new ContainerSettings(image)
        {
            InitProcess = init,
            EnableAutoRemove = true,
        };

        using var container = session.CreateContainer(settings);

        var sb = new StringBuilder();
        var tcs = new TaskCompletionSource<int>();
        container.InitProcess.OutputReceived += data => sb.Append(Encoding.UTF8.GetString(data));
        container.InitProcess.ErrorReceived += data => sb.Append(Encoding.UTF8.GetString(data));
        container.InitProcess.Exited += code => tcs.TrySetResult(code);

        container.Start();
        await tcs.Task.WaitAsync(ct);
        return sb.ToString();
    }

    public async Task StartAsync(string name, CancellationToken ct = default)
    {
        // TODO: session.GetContainer(name)?.Start(); (confirm API)
        var session = await GetSessionAsync(ct);
        await Task.Yield();
    }

    public async Task StopAsync(string name, CancellationToken ct = default)
    {
        // TODO: session.GetContainer(name)?.Stop(Signal.SIGTERM, TimeSpan.FromSeconds(10));
        var session = await GetSessionAsync(ct);
        await Task.Yield();
    }

    public void Dispose()
    {
        try { _session?.Terminate(); } catch { /* best effort */ }
        _session = null;
        _gate.Dispose();
    }
}
