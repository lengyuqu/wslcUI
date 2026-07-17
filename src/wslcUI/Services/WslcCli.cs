using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using wslcUI.Models;

namespace wslcUI.Services;

/// <summary>
/// Bridge to the <c>wslc</c> CLI for the operations the Microsoft.WSL.Containers
/// 2.9.4 SDK does NOT project into C#:
///   - enumerating existing containers   (no <c>Session.GetContainers()</c>)
///   - looking up a container by name  (no <c>Session.GetContainer(name)</c>)
///   - start / stop / remove a container by name
///   - remove an image, fetch container logs
///   - networks and volumes — there is NO C# projection at all for these
///     resources, so every network/volume operation is CLI-bridged.
///
/// These are bridged with <c>wslc list -a</c> / <c>wslc start</c> / <c>wslc stop</c> /
/// <c>wslc rm</c> / <c>wslc image rm</c> / <c>wslc logs</c> /
/// <c>wslc network create|ls|remove</c> / <c>wslc volume create|ls|remove</c>.
/// See AGENTS.md → "Known SDK gaps". The executable path matches the wslc skill doc.
/// </summary>
internal static class WslcCli
{
    private const string Exe = @"C:\Program Files\WSL\wslc.exe";

    public static async Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken ct)
    {
        var (exit, stdout, _) = await RunAsync(new[] { "list", "-a" }, ct).ConfigureAwait(false);
        return exit == 0 ? ParseList(stdout) : new List<ContainerInfo>();
    }

    public static async Task StartAsync(string name, CancellationToken ct)
    {
        var (exit, _, stderr) = await RunAsync(new[] { "start", name }, ct).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"wslc start 失败: {stderr.Trim()}");
    }

    public static async Task StopAsync(string name, CancellationToken ct)
    {
        var (exit, _, stderr) = await RunAsync(new[] { "stop", name }, ct).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"wslc stop 失败: {stderr.Trim()}");
    }

    public static async Task DeleteContainerAsync(string name, CancellationToken ct)
    {
        var (exit, _, stderr) = await RunAsync(new[] { "rm", name }, ct).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"wslc rm 失败: {stderr.Trim()}");
    }

    public static async Task DeleteImageAsync(string reference, CancellationToken ct)
    {
        var (exit, _, stderr) = await RunAsync(new[] { "image", "rm", reference }, ct).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"wslc image rm 失败: {stderr.Trim()}");
    }

    public static async Task<string> GetLogsAsync(string name, CancellationToken ct)
    {
        var (exit, stdout, stderr) = await RunAsync(new[] { "logs", name }, ct).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"wslc logs 失败: {stderr.Trim()}");
        return stdout;
    }

    // ---- networks (no SDK projection) ----
    public static async Task<IReadOnlyList<NetworkInfo>> ListNetworksAsync(CancellationToken ct)
    {
        var (exit, stdout, _) = await RunAsync(new[] { "network", "ls" }, ct).ConfigureAwait(false);
        return exit == 0 ? ParseNetworkList(stdout) : new List<NetworkInfo>();
    }

    public static async Task CreateNetworkAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("网络名称不能为空。", nameof(name));
        var (exit, _, stderr) = await RunAsync(new[] { "network", "create", name }, ct).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"wslc network create 失败: {stderr.Trim()}");
    }

    public static async Task RemoveNetworkAsync(string name, CancellationToken ct)
    {
        var (exit, _, stderr) = await RunAsync(new[] { "network", "remove", name }, ct).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"wslc network remove 失败: {stderr.Trim()}");
    }

    // ---- volumes (no SDK projection) ----
    public static async Task<IReadOnlyList<VolumeInfo>> ListVolumesAsync(CancellationToken ct)
    {
        var (exit, stdout, _) = await RunAsync(new[] { "volume", "ls" }, ct).ConfigureAwait(false);
        return exit == 0 ? ParseVolumeList(stdout) : new List<VolumeInfo>();
    }

    public static async Task CreateVolumeAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("卷名称不能为空。", nameof(name));
        var (exit, _, stderr) = await RunAsync(new[] { "volume", "create", name }, ct).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"wslc volume create 失败: {stderr.Trim()}");
    }

    public static async Task RemoveVolumeAsync(string name, CancellationToken ct)
    {
        var (exit, _, stderr) = await RunAsync(new[] { "volume", "remove", name }, ct).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"wslc volume remove 失败: {stderr.Trim()}");
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(
        string[] args, CancellationToken ct)
    {
        if (!File.Exists(Exe))
            throw new FileNotFoundException("未找到 wslc.exe，请先安装 WSL 容器组件 (wsl --install)。", Exe);

        var psi = new ProcessStartInfo
        {
            FileName = Exe,
            Arguments = string.Join(" ", args),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 wslc 进程。");

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return (proc.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Parses <c>wslc list -a</c> output. JSON mode is tried first (if wslc ever
    /// supports <c>--format json</c>); otherwise a defensive docker-style table
    /// parse that reliably grabs ID (col 1), IMAGE (col 2) and NAMES (last col).
    /// TODO: validate on a real wslc 2.9.4 install and tighten column mapping.
    /// </summary>
    private static IReadOnlyList<ContainerInfo> ParseList(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            return new List<ContainerInfo>();

        var trimmed = output.TrimStart();
        if (trimmed.StartsWith("[") || trimmed.StartsWith("{"))
        {
            try { return ParseJsonList(trimmed); }
            catch { /* fall through to table parse */ }
        }

        var result = new List<ContainerInfo>();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("CONTAINER ID", StringComparison.OrdinalIgnoreCase)) continue; // header
            if (line.StartsWith("---", StringComparison.Ordinal)) continue;                    // separator
            var cols = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length < 2) continue;

            var id = cols[0];
            var image = cols[1];
            var name = cols[^1];
            var status = cols.Length > 3 ? cols[3] : ""; // STATUS sits before PORTS/NAMES
            result.Add(new ContainerInfo
            {
                Id = id.Length > 12 ? id[..12] : id,
                Name = name,
                Image = image,
                Status = status,
            });
        }
        return result;
    }

    private static IReadOnlyList<ContainerInfo> ParseJsonList(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object)
            root = root.RootElement; // tolerate a wrapped object

        var result = new List<ContainerInfo>();
        if (root.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in root.EnumerateArray())
        {
            string Str(string key) =>
                item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString()! : "";

            var id = Str("Id");
            var names = Str("Names");
            var name = names.StartsWith("/") && names.Length > 1 ? names[1..] : (names.Length > 0 ? names : Str("Name"));
            var state = Str("State");
            var status = state.Length > 0 ? state : Str("Status");

            result.Add(new ContainerInfo
            {
                Id = id.Length > 12 ? id[..12] : id,
                Name = name,
                Image = Str("Image"),
                Status = status,
            });
        }
        return result;
    }

    /// <summary>
    /// Parses <c>wslc network ls</c> output. JSON mode is tried first; otherwise a
    /// docker-style table parse grabbing NAME (col 2), DRIVER (col 3) and SCOPE (last col).
    /// Column layout mirrors <c>docker network ls</c>: NETWORK ID | NAME | DRIVER | SCOPE.
    /// TODO: validate on a real wslc 2.9.4 install and tighten column mapping.
    /// </summary>
    private static IReadOnlyList<NetworkInfo> ParseNetworkList(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            return new List<NetworkInfo>();

        var trimmed = output.TrimStart();
        if (trimmed.StartsWith("[") || trimmed.StartsWith("{"))
        {
            try { return ParseJsonNetworks(trimmed); }
            catch { /* fall through to table parse */ }
        }

        var result = new List<NetworkInfo>();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("NETWORK ID", StringComparison.OrdinalIgnoreCase)) continue; // header
            if (line.StartsWith("---", StringComparison.Ordinal)) continue;                  // separator
            var cols = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length < 2) continue;

            var name = cols[1];
            var driver = cols.Length > 2 ? cols[2] : "";
            var scope = cols.Length > 3 ? cols[3] : "";
            result.Add(new NetworkInfo { Name = name, Driver = driver, Scope = scope });
        }
        return result;
    }

    private static IReadOnlyList<NetworkInfo> ParseJsonNetworks(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var result = new List<NetworkInfo>();
        if (root.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in root.EnumerateArray())
        {
            string Str(string key) =>
                item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString()! : "";
            var name = Str("Name");
            if (name.Length == 0)
                name = Str("Id");
            result.Add(new NetworkInfo
            {
                Name = name,
                Driver = Str("Driver"),
                Scope = Str("Scope"),
            });
        }
        return result;
    }

    /// <summary>
    /// Parses <c>wslc volume ls</c> output. JSON mode is tried first; otherwise a
    /// docker-style table parse grabbing DRIVER (col 1) and VOLUME NAME (col 2).
    /// Column layout mirrors <c>docker volume ls</c>: DRIVER | VOLUME NAME.
    /// TODO: validate on a real wslc 2.9.4 install and tighten column mapping.
    /// </summary>
    private static IReadOnlyList<VolumeInfo> ParseVolumeList(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            return new List<VolumeInfo>();

        var trimmed = output.TrimStart();
        if (trimmed.StartsWith("[") || trimmed.StartsWith("{"))
        {
            try { return ParseJsonVolumes(trimmed); }
            catch { /* fall through to table parse */ }
        }

        var result = new List<VolumeInfo>();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("DRIVER", StringComparison.OrdinalIgnoreCase)) continue; // header
            if (line.StartsWith("---", StringComparison.Ordinal)) continue;               // separator
            var cols = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length < 1) continue;

            var driver = cols[0];
            var name = cols.Length > 1 ? cols[1] : cols[0];
            result.Add(new VolumeInfo { Name = name, Driver = driver });
        }
        return result;
    }

    private static IReadOnlyList<VolumeInfo> ParseJsonVolumes(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var result = new List<VolumeInfo>();
        if (root.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in root.EnumerateArray())
        {
            string Str(string key) =>
                item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString()! : "";
            var name = Str("Name");
            if (name.Length == 0)
                name = Str("Mountpoint");
            result.Add(new VolumeInfo
            {
                Name = name,
                Driver = Str("Driver"),
                Mountpoint = Str("Mountpoint"),
            });
        }
        return result;
    }
}
