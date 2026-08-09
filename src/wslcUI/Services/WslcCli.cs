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
/// <c>wslc network create|ls|remove</c> / <c>wslc volume create|ls|remove</c> /
/// <c>wslc build -t &lt;tag&gt; &lt;context&gt;</c> (image build from a Dockerfile).
/// See AGENTS.md → "Known SDK gaps". The executable path (ExePath) is also
/// reused directly by the interactive terminal (TerminalWindow) for `wslc exec -it`.
/// </summary>
internal static class WslcCli
{
    private const string Exe = @"C:\Program Files\WSL\wslc.exe";

    /// <summary>Absolute path to wslc.exe (also consumed by the terminal).</summary>
    public static string ExePath => Exe;

    public static async Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken ct)
    {
        // Ask for JSON explicitly: plain `wslc list -a` emits a Chinese-locale table
        // ("容器 ID 名称 映像 …") that the docker-style table fallback cannot parse.
        var (exit, stdout, _) = await RunAsync(new[] { "list", "-a", "--format", "json" }, ct).ConfigureAwait(false);
        return exit == 0 ? ParseList(stdout) : new List<ContainerInfo>();
    }

    // ---- images (CLI bridge: Session.GetImages sees only the SDK's own store) ----
    public static async Task<IReadOnlyList<ImageInfo>> ListImagesAsync(CancellationToken ct)
    {
        var (exit, stdout, _) = await RunAsync(new[] { "images", "--format", "json" }, ct).ConfigureAwait(false);
        if (exit != 0)
            return new List<ImageInfo>();

        var trimmed = stdout.TrimStart();
        if (trimmed.StartsWith("[") || trimmed.Split('\n')[0].TrimStart().StartsWith("{"))
        {
            try { return ParseJsonImages(trimmed); }
            catch { /* fall through to table parse */ }
        }
        return ParseImageList(stdout);
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

    // ---- image build (no SDK projection for Dockerfile builds → CLI bridge) ----
    public static async Task BuildImageAsync(
        string contextDir, string tag, IProgress<string>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tag))
            throw new ArgumentException("镜像标签不能为空。", nameof(tag));
        if (!Directory.Exists(contextDir))
            throw new DirectoryNotFoundException($"构建上下文目录不存在: {contextDir}");
        if (!File.Exists(Path.Combine(contextDir, "Dockerfile")) &&
            !File.Exists(Path.Combine(contextDir, "Containerfile")))
            throw new FileNotFoundException("上下文中未找到 Dockerfile / Containerfile。", contextDir);

        var psi = new ProcessStartInfo
        {
            FileName = Exe,
            Arguments = $"build -t \"{tag}\" \"{contextDir}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 wslc 进程。");
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) progress?.Report(e.Data);
        };
        proc.BeginOutputReadLine();
        var stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"wslc build 失败 (exit {proc.ExitCode}): {stderr.Trim()}");
    }

    public static async Task<string> GetLogsAsync(string name, CancellationToken ct)
    {
        var (exit, stdout, stderr) = await RunAsync(new[] { "logs", name }, ct).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"wslc logs 失败: {stderr.Trim()}");
        return stdout;
    }

    // ---- stats (no SDK projection) ----
    public static async Task<IReadOnlyList<StatInfo>> GetStatsAsync(CancellationToken ct)
    {
        // wslc stats is ALREADY a one-shot snapshot — it does NOT accept
        // `--no-stream` (that docker flag errors out). JSON mode gives stable
        // field names; fall back to the table parse on old builds.
        var (exit, stdout, _) = await RunAsync(new[] { "stats", "--format", "json" }, ct).ConfigureAwait(false);
        if (exit != 0)
            return new List<StatInfo>();

        var trimmed = stdout.TrimStart();
        if (trimmed.StartsWith("[") || trimmed.StartsWith("{"))
        {
            try { return ParseJsonStats(trimmed); }
            catch { /* fall through to table parse */ }
        }
        return ParseStats(stdout);
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

        // Safety net: if the caller cancels, kill the process so we don't hang on a
        // slow or stuck CLI call. Generic guard for every bridged command (the CLI
        // does support `--no-stream` and the other flags we use; this is just hygiene).
        using var _reg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(); } catch { /* best effort */ }
        });

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return (proc.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Parses <c>wslc list -a</c> output. JSON mode is tried first (some wslc
    /// builds support <c>--format json</c>; if not, the table parse catches it);
    /// otherwise a defensive docker-style table parse that reliably grabs ID (col 1),
    /// IMAGE (col 2) and NAMES (last col).
    /// TODO: tighten column mapping against real wslc output.
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
            // wslc emits a Chinese-locale header ("容器 ID …"); accept both.
            if (line.StartsWith("CONTAINER ID", StringComparison.OrdinalIgnoreCase)) continue; // header
            if (line.StartsWith("容器 ID", StringComparison.Ordinal)) continue;                 // header (zh)
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
        {
            // tolerate a wrapped object: find the first array property
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Array)
                {
                    root = prop.Value;
                    break;
                }
            }
        }

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
            // wslc 2.9.4 emits State as a NUMBER enum (2=running, 3=stopped), not a
            // string — read it as int when present, fall back to Status string.
            var state = "";
            if (item.TryGetProperty("State", out var st))
            {
                state = st.ValueKind switch
                {
                    JsonValueKind.Number => st.GetInt32().ToString(),
                    JsonValueKind.String => st.GetString() ?? "",
                    _ => "",
                };
            }
            var status = state.Length > 0 ? MapContainerState(state) : Str("Status");

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

    /// <summary>Map wslc's numeric container state to a readable string.</summary>
    private static string MapContainerState(string state) => state switch
    {
        "1" => "created",
        "2" => "running",
        "3" => "stopped",
        _ => state.Length > 0 ? $"state {state}" : "",
    };

    /// <summary>
    /// Parses <c>wslc network ls</c> output. JSON mode is tried first; otherwise a
    /// docker-style table parse grabbing NAME (col 2), DRIVER (col 3) and SCOPE (last col).
    /// Column layout mirrors <c>docker network ls</c>: NETWORK ID | NAME | DRIVER | SCOPE.
    /// TODO: tighten column mapping against real wslc output.
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
    /// TODO: tighten column mapping against real wslc output.
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

    /// <summary>
    /// Parses <c>wslc stats --format json</c> output. Field names are stable:
    /// ID / Name / CPUPerc / MemUsage / MemPerc / NetIO / BlockIO / PIDs.
    /// PIDs is a JSON NUMBER, unlike the other string fields.
    /// </summary>
    private static IReadOnlyList<StatInfo> ParseJsonStats(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var result = new List<StatInfo>();
        if (root.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in root.EnumerateArray())
        {
            string Str(string key) =>
                item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString()! : "";
            var pids = "";
            if (item.TryGetProperty("PIDs", out var p) && p.ValueKind == JsonValueKind.Number)
                pids = p.GetInt64().ToString();

            result.Add(new StatInfo
            {
                Container = Str("Name"),
                Cpu = Str("CPUPerc"),
                Mem = Str("MemUsage"),
                MemPercent = Str("MemPerc"),
                NetIo = Str("NetIO"),
                BlockIo = Str("BlockIO"),
                Pids = pids,
            });
        }
        return result;
    }

    /// <summary>
    /// Parses <c>wslc stats</c> output. Table-first (no JSON variant
    /// attempted): docker-style columns
    /// CONTAINER ID | NAME | CPU % | MEM USAGE / LIMIT | MEM % | NET I/O | BLOCK I/O | PIDS.
    /// (The `--no-stream` flag is supported by the CLI; only the column order is
    /// worth verifying on a real install.)
    /// TODO: tighten column mapping against real wslc output.
    /// </summary>
    private static IReadOnlyList<StatInfo> ParseStats(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            return new List<StatInfo>();

        var result = new List<StatInfo>();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            // wslc emits a Chinese-locale header ("容器 ID 名称 CPU 百分比 …"); accept both.
            if (line.StartsWith("CONTAINER ID", StringComparison.OrdinalIgnoreCase)) continue; // header
            if (line.StartsWith("容器 ID", StringComparison.Ordinal)) continue;                 // header (zh)
            if (line.StartsWith("---", StringComparison.Ordinal)) continue;                    // separator
            var cols = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length < 2) continue;

            // NAME is col 1 (col 0 is the short ID); CPU/MEM/MEM%/NET/BLOCK/PIDS follow.
            result.Add(new StatInfo
            {
                Container = cols[1],
                Cpu = cols.Length > 2 ? cols[2] : "",
                Mem = cols.Length > 3 ? cols[3] : "",
                MemPercent = cols.Length > 4 ? cols[4] : "",
                NetIo = cols.Length > 5 ? cols[5] : "",
                BlockIo = cols.Length > 6 ? cols[6] : "",
                Pids = cols.Length > 7 ? cols[7] : "",
            });
        }
        return result;
    }

    /// <summary>
    /// Parses <c>wslc images --format json</c>. Field names: Created/Id/Repository/Size/Tag.
    /// Id includes the "sha256:" prefix; strip it so callers can compare by hex digest.
    /// </summary>
    private static IReadOnlyList<ImageInfo> ParseJsonImages(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var result = new List<ImageInfo>();
        if (root.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in root.EnumerateArray())
        {
            string Str(string key) =>
                item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString()! : "";
            var id = Str("Id");
            if (id.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                id = id[7..];

            ulong size = 0;
            if (item.TryGetProperty("Size", out var sv) && sv.ValueKind == JsonValueKind.Number)
                size = sv.GetUInt64();

            result.Add(new ImageInfo
            {
                Id = id,
                Repository = Str("Repository"),
                Tag = Str("Tag"),
                Size = FormatBytes(size),
            });
        }
        return result;
    }

    /// <summary>
    /// Parses <c>wslc images</c> table output. Column layout:
    /// REPOSITORY | TAG | IMAGE ID | CREATED | SIZE (REPOSITORY can be Namespacede, e.g. minio/minio).
    /// </summary>
    private static IReadOnlyList<ImageInfo> ParseImageList(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<ImageInfo>();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("REPOSITORY", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("---", StringComparison.Ordinal)) continue;
            var cols = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length < 3) continue;
            // cols[0]=REPOSITORY, cols[1]=TAG, cols[2]=IMAGE ID (sha256:hex)
            var id = cols[2];
            if (id.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                id = id[7..];
            result.Add(new ImageInfo
            {
                Repository = cols[0],
                Tag = cols[1],
                Id = id,
                Size = cols.Length > 4 ? cols[4] : "",
            });
        }
        return result;
    }

    private static string FormatBytes(ulong bytes)
    {
        double v = bytes;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return $"{v:0.##} {units[u]}";
    }
}
