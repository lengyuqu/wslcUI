using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using wslcUI.Models;

namespace wslcUI.Services;

/// <summary>
/// Bridge to the <c>wslc</c> CLI for the operations the Microsoft.WSL.Containers
/// 3.0.1 SDK does NOT project into C#:
///   - enumerating existing containers   (no <c>Session.GetContainers()</c>)
///   - looking up a container by name  (no <c>Session.GetContainer(name)</c>)
///   - start / stop / remove a container by name
///   - remove an image, fetch container logs
///   - networks and volumes — there is NO C# projection at all for these
///     resources, so every network/volume operation is CLI-bridged.
///   - one-shot resource snapshots       (no stats projection)
///   - image build from a Dockerfile    (no build projection)
///
/// These are bridged with <c>wslc list -a</c> / <c>wslc start</c> / <c>wslc stop</c> /
/// <c>wslc rm</c> / <c>wslc image rm</c> / <c>wslc logs</c> /
/// <c>wslc network create|ls|remove</c> / <c>wslc volume create|ls|remove</c> /
/// <c>wslc stats</c> / <c>wslc build -t &lt;tag&gt; &lt;context&gt;</c>.
/// See AGENTS.md → "Known SDK gaps". The executable path (ExePath) is also
/// reused directly by the interactive terminal (TerminalWindow) for `wslc exec -it`.
///
/// <para>
/// <b>Why we never use <c>--format json</c></b>: wslc advertises a JSON
/// format flag, but the shape is inconsistent per command (observed on 2.9.9:
/// list/images/stats emit a single object, network emits NDJSON, some emit
/// none at all). Going through JSON added path branches that silently returned
/// empty lists. Table output is uniform: a single header line followed by
/// padded data rows. We parse it with header-derived fixed-width column
/// boundaries (<see cref="SplitByColumns"/>), which round-trips for both the
/// Chinese-locale headers (<c>容器 ID / 名称 / 映像 / …</c>) and the English
/// headers (<c>NETWORK ID / NAME / DRIVER / SCOPE</c>).
/// </para>
/// </summary>
internal static class WslcCli
{
    /// <summary>wslc.exe 的默认安装位置（`wsl --install` 的落点）。</summary>
    private const string DefaultExe = @"C:\Program Files\WSL\wslc.exe";

    /// <summary>覆盖 wslc.exe 路径的环境变量（非默认安装位置 / 免安装版 / 测试用）。</summary>
    internal const string ExeEnvVar = "WSLCUI_WSLC_EXE";

    // 进程内解析一次即可（PATH/环境变量在一次会话里不会变）。
    private static readonly Lazy<string> _exePath =
        new(ResolveExePath, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Absolute path to wslc.exe (also consumed by the terminal).
    /// 解析顺序：环境变量 <see cref="ExeEnvVar"/> → 默认安装路径 → PATH 扫描。
    /// 三级全落空时返回默认路径，交由 <see cref="EnsureExe"/> 抛出带完整查找范围的错误。
    /// </summary>
    public static string ExePath => _exePath.Value;

    /// <summary>
    /// 三级路径解析（不读缓存，便于单测）。硬编码 `C:\Program Files\WSL\wslc.exe`
    /// 会让装在别的盘/别的目录的机器整组 CLI 桥接功能失效，且错误文案误导成
    /// 「请先安装 WSL」——这里补齐 PATH 回退，并允许环境变量显式指定。
    /// </summary>
    internal static string ResolveExePath()
    {
        var overridePath = Environment.GetEnvironmentVariable(ExeEnvVar);
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;

        if (File.Exists(DefaultExe)) return DefaultExe;

        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathVar))
        {
            foreach (var dir in pathVar.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    // PATH 条目可能带引号（"C:\Program Files\X"），先剥壳。
                    var candidate = Path.Combine(dir.Trim().Trim('"'), "wslc.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                    // PATH 里含非法路径字符的条目：跳过，不影响其余条目。
                }
            }
        }

        return DefaultExe;
    }

    /// <summary>
    /// 所有 CLI 调用（含 <see cref="BuildImageAsync"/>）的统一前置守卫。
    /// 不守卫的话 Process.Start 会抛裸 Win32Exception（"系统找不到指定的文件"），
    /// 既没有可操作的下一步建议，也命中不了 <see cref="TranslateCliError"/> 的映射表。
    /// </summary>
    private static void EnsureExe()
    {
        if (File.Exists(ExePath)) return;
        throw new FileNotFoundException(
            $"未找到 wslc.exe。已查找：环境变量 {ExeEnvVar}、默认路径 {DefaultExe}、PATH。" +
            "请先安装 WSL 容器组件（wsl --install），或用该环境变量指定 wslc.exe 的位置。",
            ExePath);
    }

    public static async Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken ct)
    {
        // `-a` so stopped containers show too; `--no-trunc` would widen the ID
        // column past 12 chars. We truncate to a short ID ourselves.
        var (exit, stdout, stderr) = await RunAsync(new[] { "list", "-a" }, ct).ConfigureAwait(false);
        return exit == 0 ? ParseContainerList(stdout) : throw CliFailed("wslc list", exit, stderr);
    }

    // ---- images (CLI bridge: Session.GetImages sees only the SDK's own store) ----
    public static async Task<IReadOnlyList<ImageInfo>> ListImagesAsync(CancellationToken ct)
    {
        var (exit, stdout, stderr) = await RunAsync(new[] { "images" }, ct).ConfigureAwait(false);
        return exit == 0 ? ParseImageList(stdout) : throw CliFailed("wslc images", exit, stderr);
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

    // wslc 2.9.12+ 新增 `wslc restart <name>`（#41435；未运行的容器会被直接启动）。
    // SDK 3.0.1 仍无 Restart() C# 投影，故走 CLI 桥接。
    public static async Task RestartAsync(string name, CancellationToken ct)
    {
        var (exit, _, stderr) = await RunAsync(new[] { "restart", name }, ct).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"wslc restart 失败: {stderr.Trim()}");
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
        EnsureExe();

        var psi = new ProcessStartInfo
        {
            FileName = ExePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        // ArgumentList 逐参数传递：tag / contextDir 含空格或引号时不会被
        // Arguments 字符串拼接错误拆分（见 RunAsync 同款说明）。
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(tag);
        psi.ArgumentList.Add(contextDir);

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

    // ---- stats (no SDK projection). wslc stats is ALREADY a one-shot snapshot:
    // it does not stream and does not accept docker's `--no-stream` flag (that
    // errors with "选项名称未被识别"). It also offers a `--format json` flag but
    // we deliberately skip it — see class doc. ----
    public static async Task<IReadOnlyList<StatInfo>> GetStatsAsync(CancellationToken ct)
    {
        var (exit, stdout, stderr) = await RunAsync(new[] { "stats" }, ct).ConfigureAwait(false);
        return exit == 0 ? ParseStats(stdout) : throw CliFailed("wslc stats", exit, stderr);
    }

    // ---- networks (no SDK projection) ----
    public static async Task<IReadOnlyList<NetworkInfo>> ListNetworksAsync(CancellationToken ct)
    {
        var (exit, stdout, stderr) = await RunAsync(new[] { "network", "ls" }, ct).ConfigureAwait(false);
        return exit == 0 ? ParseNetworkList(stdout) : throw CliFailed("wslc network ls", exit, stderr);
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
        // 必须走 --format json：table 格式只有 DRIVER/VOLUME NAME 两列，
        // 挂载点（Mountpoint）只在 json 输出里存在（wslc 2.9.9 实测）。
        var (exit, stdout, stderr) = await RunAsync(new[] { "volume", "ls", "--format", "json" }, ct).ConfigureAwait(false);
        return exit == 0 ? ParseVolumeListJson(stdout) : throw CliFailed("wslc volume ls", exit, stderr);
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

    // ---- container inspect（CLI bridge：SDK 3.0.1 无 inspect 投影）
    // `wslc list -a` 只返回简表（ID/名称/映像/状态/端口），容器与卷/网络的
    // 关联关系统统不在表里。Mounts 必须单独 `wslc inspect <name>` 走 JSON。
    // 输出形态：外层是单元素数组的 JSON（wslc 2.9.9.0 实测，
    // `Cmd / Env / HostConfig / Mounts / NetworkSettings / ...`）。
    // 多个 Name 时返回多元素数组，下面按数组逐元素解析并合并 Mounts。
    public static async Task<IReadOnlyList<ContainerMount>> InspectContainerAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("容器名称不能为空。", nameof(name));
        var (exit, stdout, stderr) = await RunAsync(new[] { "inspect", name, "--format", "json" }, ct).ConfigureAwait(false);
        return exit == 0 ? ParseContainerInspect(stdout) : throw CliFailed("wslc inspect", exit, stderr);
    }

    internal static IReadOnlyList<ContainerMount> ParseContainerInspect(string output)
    {
        var result = new List<ContainerMount>();
        if (string.IsNullOrWhiteSpace(output)) return result;

        using var doc = System.Text.Json.JsonDocument.Parse(output);
        if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
            return result;

        foreach (var c in doc.RootElement.EnumerateArray())
        {
            if (c.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            if (!c.TryGetProperty("Mounts", out var mounts) ||
                mounts.ValueKind != System.Text.Json.JsonValueKind.Array)
                continue;

            foreach (var m in mounts.EnumerateArray())
            {
                if (m.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                string S(string p) => m.TryGetProperty(p, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                    ? v.GetString() ?? "" : "";
                var dest = S("Destination");
                // 三种类型（volume / bind / tmpfs）至少要有 Destination；空则跳过，避免显示" → "。
                if (dest.Length == 0) continue;
                var rw = m.TryGetProperty("ReadWrite", out var rv) &&
                         rv.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
                    ? rv.GetBoolean()
                    : true;
                result.Add(new ContainerMount
                {
                    Name = S("Name"),
                    Destination = dest,
                    Source = S("Source"),
                    Type = S("Type"),
                    ReadWrite = rw,
                });
            }
        }
        return result;
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(
        string[] args, CancellationToken ct)
    {
        EnsureExe();

        var psi = new ProcessStartInfo
        {
            FileName = ExePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // 必须显式 UTF-8：GUI 进程没有控制台，.NET 默认按系统 ANSI 代码页
            // （中文系统 = GBK）解码，中文表头会整体乱码导致 FindColumn 全部
            // 失配、列表静默变空。wslc 重定向输出是 UTF-8。
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        // ArgumentList 逐参数传递，绕过 Arguments 字符串拼接的引号/空格
        // 解析：含空格或引号的容器名/网络名/卷名不会被拆成多个参数，
        // 也无法借引号给 wslc 注入额外参数。
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 wslc 进程。");

        // Safety net: if the caller cancels, kill the process so we don't hang
        // on a slow or stuck CLI call. Generic guard for every bridged command;
        // the canceller is the only signal that breaks a hung call — wslc has
        // no `--timeout` we can pass.
        using var _reg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(); } catch { /* best effort */ }
        });

        // 两个流必须**并发**排空：串行 await（先 stdout 再 stderr）时，子进程若在
        // stdout 结束前写满 stderr 的管道缓冲（默认 4 KB）就会阻塞在写侧，stdout
        // 随之永不 EOF —— 双向等待，永久挂起。wslc 报错时 stderr 常带完整错误码与
        // 上下文（WSL 未安装的引导文案尤其长），超过 4 KB 完全可能。
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return (proc.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    /// <summary>
    /// 列举类命令失败时构造异常。此前非零退出码会静默返回空列表，用户无法区分
    /// 「没有资源」和「查询失败」（如 WSL 未运行）——UI 显示 0 容器 0 镜像。
    /// </summary>
    private static InvalidOperationException CliFailed(string command, int exit, string stderr) =>
        new($"{command} 失败 (exit {exit}): {stderr.Trim()}");

    // ====================================================================
    //  错误码 → 中文建议映射
    //
    //  wslc 的 stderr 尾部带「错误代码: XXXX」机器码（如 WSLC_E_CONTAINER_
    //  NOT_FOUND，真机 verify V3/V4 已观测）。直接把原文抛给 InfoBar 对普通
    //  用户不可读，也没有「下一步该做什么」。
    //
    //  码名来源：官方 wslcsdk.h 的 WSLC_E_* 常量（0x8004_0601..060F，经
    //  docs.rs/wslc-sys 与 wsl.dev C# API 参考 Error 枚举双重核对）。未命中
    //  返回 null，调用方保留原文兜底；未命中码会 Debug.WriteLine 留痕（H2
    //  随用随补的信号源）。
    // ====================================================================

    private static readonly Dictionary<string, string> ErrorHints = new()
    {
        // --- 官方 WSLC_E_*（wslcsdk.h 0x8004_0601..060F，按码值排序）---
        ["WSLC_E_IMAGE_NOT_FOUND"] = "找不到该镜像，可能已被删除。刷新列表后重试。",
        ["WSLC_E_CONTAINER_PREFIX_AMBIGUOUS"] = "该名称前缀匹配到多个容器。请使用完整容器名。",
        ["WSLC_E_CONTAINER_NOT_FOUND"] = "找不到该容器，可能已被删除。刷新列表后重试。",
        ["WSLC_E_VOLUME_NOT_FOUND"] = "找不到该卷，可能已被删除。刷新列表后重试。",
        ["WSLC_E_CONTAINER_NOT_RUNNING"] = "容器未运行。请先启动容器再执行该操作（附加/日志等都需要运行态）。",
        ["WSLC_E_CONTAINER_IS_RUNNING"] = "容器正在运行。请先停止容器再执行该操作。",
        ["WSLC_E_SESSION_RESERVED"] = "该会话名被系统保留，请换一个名字。",
        ["WSLC_E_INVALID_SESSION_NAME"] = "会话名无效：请只使用字母、数字、连字符和下划线。",
        ["WSLC_E_NETWORK_NOT_FOUND"] = "找不到该网络，可能已被删除。刷新列表后重试。",
        ["WSLC_E_WU_SEARCH_FAILED"] = "Windows 更新检索失败：请检查网络连接后重试。",
        ["WSLC_E_SDK_UPDATE_NEEDED"] = "WSL 组件需要更新：请运行 `wsl --update` 后重试。",
        ["WSLC_E_CONTAINER_DISABLED"] = "WSL 容器功能未启用：请在「启用或关闭 Windows 功能」中开启相关组件。",
        ["WSLC_E_REGISTRY_BLOCKED_BY_POLICY"] = "注册表访问被组策略阻止：请联系管理员或检查策略设置。",
        ["WSLC_E_VOLUME_NOT_AVAILABLE"] = "卷当前不可用：可能被其他进程占用，请稍后重试。",
        ["WSLC_E_SESSION_NOT_FOUND"] = "找不到该会话，可能已被关闭。",

        // --- WinRT/COM 标准 HRESULT（平台稳定，非 wslc 专属）---
        ["E_INVALIDARG"] = "参数无效：请检查名称是否包含非法字符（空格、引号或特殊符号）。",
        ["E_NOTFOUND"] = "未找到目标资源。刷新列表后重试。",
        ["E_ACCESSDENIED"] = "访问被拒绝：请以管理员身份运行，或检查文件/目录权限。",
        ["E_FAIL"] = "操作失败（未指定原因）。可打开日志抽屉查看 wslc 原始输出定位问题。",

        // --- 惯例预置（官方清单未收录，真机观测后再校正；未命中无副作用）---
        ["WSLC_E_NETWORK_IN_USE"] = "该网络正被容器使用。请先断开使用它的容器。",
        ["WSLC_E_VOLUME_IN_USE"] = "该卷正被容器使用。请先停止使用它的容器。",
        ["WSLC_E_IMAGE_IN_USE"] = "该镜像正被容器引用。请先删除引用它的容器。",
    };

    /// <summary>
    /// 从 wslc 报错消息中提取「错误代码: XXX」并翻译成中文建议。
    /// 返回「{友好建议}（{code}：{原始摘要}）」；消息不含可识别错误码时
    /// 再按 stderr 关键词兜底（如 WSL 未安装引导），仍不命中返回 null。
    /// 纯函数，可单测。
    /// </summary>
    internal static string? TranslateCliError(string message)
    {
        if (string.IsNullOrEmpty(message)) return null;

        // wslc 的错误码出现在「错误代码: CODE」或英文「error code: CODE」后。
        var m = Regex.Match(message, @"(?:错误代码|error code)\s*[:：]\s*(\S+)", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var code = m.Groups[1].Value.TrimEnd('.', '。');
            if (ErrorHints.TryGetValue(code, out var hint))
            {
                var brief = message.Trim();
                if (brief.Length > 80) brief = brief[..80] + "…";
                return $"{hint}（{code}：{brief}）";
            }

            // H2 被动收集：未映射码留痕。真机跑出新码时在调试输出里可见，
            // 「随用随补」以此信号为准（InfoBar 显示裸码也是同一信号）。
            System.Diagnostics.Debug.WriteLine($"[wslcUI] 未映射错误码: {code}");
        }

        // 无错误码时按 stderr 关键词兜底（如 WSL 未装/未启动的引导文案）。
        if (message.Contains("wsl --install", StringComparison.OrdinalIgnoreCase) ||
            (message.Contains("WSL", StringComparison.Ordinal) &&
             message.Contains("install", StringComparison.OrdinalIgnoreCase)))
            return "WSL 未就绪：请运行 `wsl --install` 安装 WSL 组件后重试。";

        return null;
    }

    // ====================================================================
    //  Table parsers
    //
    //  Every wslc list-style command emits the same shape: one header line
    //  followed by zero or more padded data rows. Each parser below:
    //    1. Skips leading blank lines and a possible `---` separator.
    //    2. Treats the first non-empty line as the header.
    //    3. Uses SplitByColumns to get the column header positions for that
    //       line, then slices each subsequent row by the SAME positions.
    //
    //  Header cell names are then matched against the known column captions
    //  (which come in two locales: Chinese for `list` / `stats`, English for
    //  the rest). Matching by caption — instead of by fixed ordinal — keeps
    //  the parser stable across wslc build changes that add, drop or
    //  reorder columns.
    // ====================================================================

    /// <summary>
    /// Splits a fixed-width row by column boundaries derived from the header
    /// line (see <see cref="ComputeColumnBoundaries"/>). Returns the trimmed
    /// cell list, one per header column. If the row is shorter than the header
    /// (last column), the missing tail is returned as empty strings. If the row
    /// is longer (a value spanning more characters than the column was padded
    /// for), the spillover is folded back into the LAST cell — we never lose
    /// data, only perfect column alignment.
    /// </summary>
    /// <param name="boundaries">
    /// Per-column start offsets in the header line. The end of column <c>i</c>
    /// is <c>boundaries[i+1]</c> (or the header length for the last column).
    /// </param>
    /// <param name="row">The data line to slice. Leading/trailing padding of each cell is trimmed.</param>
    internal static List<string> SplitByColumns(int[] boundaries, string row)
    {
        var cells = new List<string>(boundaries.Length);
        for (int i = 0; i < boundaries.Length - 1; i++)
        {
            int s = boundaries[i];
            int e = boundaries[i + 1];
            if (s >= row.Length) { cells.Add(""); continue; }
            // Clamp the end to row length; if this is the last column AND
            // we hit the row end without reaching the header's column end,
            // the value is wider than its column and we shouldn't truncate.
            if (i == boundaries.Length - 2 && row.Length > e) e = row.Length;
            else if (e > row.Length) e = row.Length;
            cells.Add(row.Substring(s, e - s).Trim());
        }
        // Ensure count matches boundaries.Length - 1 (for short rows with no trailing data)
        while (cells.Count < boundaries.Length - 1) cells.Add("");
        return cells;
    }

    /// <summary>
    /// Extracts column start offsets from a wslc table header. The header
    /// looks like one of:
    ///   <c>容器 ID      名称      映像        已创建    状态        端口</c>
    ///   <c>NETWORK ID   NAME      DRIVER      SCOPE</c>
    /// Splitting on 2+ ASCII spaces gives the column captions; locating each
    /// caption in the original header string yields the byte offset at which
    /// that column begins. Returns a boundaries array with one extra trailing
    /// element equal to the header length (so callers can write
    /// <c>boundaries[i] .. boundaries[i+1]</c> for each column).
    /// </summary>
    internal static int[]? ComputeColumnBoundaries(string headerLine)
    {
        var cells = Regex.Split(headerLine.TrimEnd('\r', '\n'), @"[ ]{2,}");
        if (cells.Length == 0 || cells[0].Length == 0) return null;

        var boundaries = new List<int>(cells.Length + 1);
        var searchFrom = 0;
        foreach (var cell in cells)
        {
            var trimmed = cell.Trim();
            if (trimmed.Length == 0) continue;
            var idx = headerLine.IndexOf(trimmed, searchFrom, StringComparison.Ordinal);
            if (idx < 0) return null;
            boundaries.Add(idx);
            searchFrom = idx + trimmed.Length;
        }
        if (boundaries.Count == 0) return null;
        boundaries.Add(headerLine.Length);
        return boundaries.ToArray();
    }

    /// <summary>
    /// Index of a column whose trimmed caption equals <paramref name="needle"/>
    /// (case-sensitive). Returns -1 if no such column exists. Use this to make
    /// parsers resilient to column reordering.
    /// </summary>
    internal static int FindColumn(int[] boundaries, string headerLine, string needle)
    {
        for (int i = 0; i < boundaries.Length - 1; i++)
        {
            int s = boundaries[i];
            int e = boundaries[i + 1];
            if (e > headerLine.Length) e = headerLine.Length;
            var cell = headerLine.Substring(s, e - s).Trim();
            if (cell == needle) return i;
        }
        return -1;
    }

    /// <summary>
    /// Parses <c>wslc list -a</c> output. Real header observed on wslc 2.9.9.0:
    /// <c>容器 ID      名称       映像        已创建    状态        端口</c>
    /// (zh-CN locale). wslc 2.9.11 aligned the format with docker (#41375):
    /// the NAME column moved to the END and was renamed (zh: <c>NAMES</c>),
    /// a COMMAND column was inserted after IMAGE, and status text became
    /// docker-style (<c>Exited (0) 10 days ago</c>). Both shapes are supported:
    /// columns are located by caption, never by position.
    /// </summary>
    internal static IReadOnlyList<ContainerInfo> ParseContainerList(string output)
    {
        var result = new List<ContainerInfo>();
        var lines = output.Split('\n');
        int? headerIdx = null;
        int[]? boundaries = null;
        string headerLine = "";

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.StartsWith("---", StringComparison.Ordinal)) continue;
            boundaries = ComputeColumnBoundaries(line);
            if (boundaries is null || boundaries.Length < 3) continue; // skip if it doesn't look like a header
            headerLine = line;
            headerIdx = i;
            break;
        }
        if (headerIdx is null || boundaries is null) return result;

        // Locate columns by caption (id / name / image / status).
        var idIdx = FindColumn(boundaries, headerLine, "容器 ID");
        if (idIdx < 0) idIdx = FindColumn(boundaries, headerLine, "CONTAINER ID");
        var nameIdx = FindColumn(boundaries, headerLine, "名称");
        if (nameIdx < 0) nameIdx = FindColumn(boundaries, headerLine, "NAME");
        // wslc 2.9.11+（docker 对齐）：名称列改名为 NAMES 且移到行尾。
        if (nameIdx < 0) nameIdx = FindColumn(boundaries, headerLine, "NAMES");
        var imgIdx = FindColumn(boundaries, headerLine, "映像");
        if (imgIdx < 0) imgIdx = FindColumn(boundaries, headerLine, "IMAGE");
        var statusIdx = FindColumn(boundaries, headerLine, "状态");
        if (statusIdx < 0) statusIdx = FindColumn(boundaries, headerLine, "STATUS");
        var portsIdx = FindColumn(boundaries, headerLine, "端口");
        if (portsIdx < 0) portsIdx = FindColumn(boundaries, headerLine, "PORTS");
        var createdIdx = FindColumn(boundaries, headerLine, "已创建");
        if (createdIdx < 0) createdIdx = FindColumn(boundaries, headerLine, "CREATED");
        if (idIdx < 0 || nameIdx < 0 || imgIdx < 0)
            return result; // unrecognised header

        for (int i = headerIdx.Value + 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0) continue;
            var cells = SplitByColumns(boundaries, line);
            if (cells.Count <= idIdx || cells.Count <= nameIdx || cells.Count <= imgIdx) continue;
            var id = cells[idIdx];
            if (id.Length == 0) continue;
            if (id.Length > 12) id = id[..12]; // short ID, docker-style
            result.Add(new ContainerInfo
            {
                Id = id,
                Name = cells[nameIdx],
                Image = cells[imgIdx],
                Status = statusIdx >= 0 && cells.Count > statusIdx ? cells[statusIdx] : "",
                // 列缺失或单元格为空时统一降级为「—」，不用空字符串冒充有值。
                Ports = (portsIdx >= 0 && cells.Count > portsIdx && cells[portsIdx].Length > 0) ? cells[portsIdx] : "—",
                CreatedAt = (createdIdx >= 0 && cells.Count > createdIdx && cells[createdIdx].Length > 0) ? cells[createdIdx] : "—",
            });
        }
        return result;
    }

    /// <summary>
    /// Parses <c>wslc network ls</c> output. Real header on wslc 2.9.9.0:
    /// <c>NETWORK ID   NAME      DRIVER      SCOPE</c>
    /// </summary>
    internal static IReadOnlyList<NetworkInfo> ParseNetworkList(string output)
    {
        var result = new List<NetworkInfo>();
        var (headerLine, boundaries, headerIdx) = LocateHeader(output);
        if (headerLine is null) return result;

        var nameIdx = FindColumn(boundaries, headerLine, "NAME");
        var driverIdx = FindColumn(boundaries, headerLine, "DRIVER");
        var scopeIdx = FindColumn(boundaries, headerLine, "SCOPE");
        if (nameIdx < 0) return result;

        var lines = output.Split('\n');
        for (int i = headerIdx + 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0) continue;
            var cells = SplitByColumns(boundaries, line);
            if (cells.Count <= nameIdx || cells[nameIdx].Length == 0) continue;
            result.Add(new NetworkInfo
            {
                Name = cells[nameIdx],
                Driver = driverIdx >= 0 && cells.Count > driverIdx ? cells[driverIdx] : "",
                Scope = scopeIdx >= 0 && cells.Count > scopeIdx ? cells[scopeIdx] : "",
            });
        }
        return result;
    }

    /// <summary>
    /// Parses <c>wslc volume ls --format json</c> output. 实测 wslc 2.9.9.0
    /// 输出为**逐行 JSON 对象**（每行一个卷，非数组）：{"Driver":...,"Mountpoint":...,"Name":...}；
    /// 兼容数组包裹形式。table 格式没有挂载点列，挂载点只能从这里拿。
    /// </summary>
    internal static IReadOnlyList<VolumeInfo> ParseVolumeListJson(string output)
    {
        var result = new List<VolumeInfo>();
        if (string.IsNullOrWhiteSpace(output)) return result;

        if (output.TrimStart().StartsWith('['))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(output);
            foreach (var el in doc.RootElement.EnumerateArray())
                AddVolume(result, el);
            return result;
        }

        foreach (var line in output.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0 || !t.StartsWith('{')) continue;
            using var doc = System.Text.Json.JsonDocument.Parse(t);
            AddVolume(result, doc.RootElement);
        }
        return result;
    }

    private static void AddVolume(List<VolumeInfo> result, System.Text.Json.JsonElement el)
    {
        string S(string prop) =>
            el.ValueKind == System.Text.Json.JsonValueKind.Object &&
            el.TryGetProperty(prop, out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String
                ? p.GetString() ?? "" : "";
        var name = S("Name");
        if (name.Length == 0) return;
        result.Add(new VolumeInfo
        {
            Name = name,
            Driver = S("Driver"),
            Mountpoint = S("Mountpoint"),
        });
    }

    /// <summary>
    /// Parses <c>wslc stats</c> output. Real header on wslc 2.9.9.0 (zh-CN
    /// locale): <c>容器 ID   名称   CPU 百分比   最大用量/限制   内存百分比
    ///   网络 I/O   块 I/O   PIDS</c>. <c>stats</c> defaults to running
    /// containers only; if there are none, wslc prints an empty table.
    /// </summary>
    internal static IReadOnlyList<StatInfo> ParseStats(string output)
    {
        var result = new List<StatInfo>();
        var (headerLine, boundaries, headerIdx) = LocateHeader(output);
        if (headerLine is null) return result;

        // Locate columns by caption. Names that contain a space ("CPU 百分比",
        // "最大用量/限制", "内存百分比", "网络 I/O", "块 I/O") match exactly in
        // the trimmed slice (because SplitByColumns strips left/right padding
        // but preserves internal whitespace).
        int Idx(string caption) => FindColumn(boundaries, headerLine, caption);

        var nameIdx = Idx("名称"); if (nameIdx < 0) nameIdx = Idx("NAME");
        var cpuIdx = Idx("CPU 百分比"); if (cpuIdx < 0) cpuIdx = Idx("CPU %");
        var memIdx = Idx("最大用量/限制"); if (memIdx < 0) memIdx = Idx("MEM USAGE / LIMIT");
        var memPctIdx = Idx("内存百分比"); if (memPctIdx < 0) memPctIdx = Idx("MEM %");
        var netIdx = Idx("网络 I/O"); if (netIdx < 0) netIdx = Idx("NET I/O");
        var blkIdx = Idx("块 I/O"); if (blkIdx < 0) blkIdx = Idx("BLOCK I/O");
        var pidsIdx = Idx("PIDS");
        if (nameIdx < 0) return result;

        var lines = output.Split('\n');
        for (int i = headerIdx + 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0) continue;
            var cells = SplitByColumns(boundaries, line);
            if (cells.Count <= nameIdx || cells[nameIdx].Length == 0) continue;
            result.Add(new StatInfo
            {
                Container = cells[nameIdx],
                Cpu = cpuIdx >= 0 && cells.Count > cpuIdx ? cells[cpuIdx] : "",
                Mem = memIdx >= 0 && cells.Count > memIdx ? cells[memIdx] : "",
                MemPercent = memPctIdx >= 0 && cells.Count > memPctIdx ? cells[memPctIdx] : "",
                NetIo = netIdx >= 0 && cells.Count > netIdx ? cells[netIdx] : "",
                BlockIo = blkIdx >= 0 && cells.Count > blkIdx ? cells[blkIdx] : "",
                Pids = pidsIdx >= 0 && cells.Count > pidsIdx ? cells[pidsIdx] : "",
            });
        }
        return result;
    }

    /// <summary>
    /// Parses <c>wslc images</c> output. Real header on wslc 2.9.9.0:
    /// <c>REPOSITORY   TAG   IMAGE ID   CREATED   SIZE</c>. SIZE is the
    /// truncated form (column padding clips the unit at ~5 chars; e.g.
    /// "4.45" instead of "4.45MB"); if exact bytes are needed, call
    /// <c>wslc inspect &lt;id&gt;</c> per image. The CLI's
    /// <c>--format json</c> is not used here (see class doc).
    /// </summary>
    internal static IReadOnlyList<ImageInfo> ParseImageList(string output)
    {
        var result = new List<ImageInfo>();
        var (headerLine, boundaries, headerIdx) = LocateHeader(output);
        if (headerLine is null) return result;

        var repoIdx = FindColumn(boundaries, headerLine, "REPOSITORY");
        var tagIdx = FindColumn(boundaries, headerLine, "TAG");
        var idIdx = FindColumn(boundaries, headerLine, "IMAGE ID");
        var sizeIdx = FindColumn(boundaries, headerLine, "SIZE");
        if (repoIdx < 0 || tagIdx < 0 || idIdx < 0) return result;

        var lines = output.Split('\n');
        for (int i = headerIdx + 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0) continue;
            var cells = SplitByColumns(boundaries, line);
            if (cells.Count <= idIdx || cells[idIdx].Length == 0) continue;
            var id = cells[idIdx];
            if (id.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                id = id[7..];
            result.Add(new ImageInfo
            {
                Repository = cells[repoIdx],
                Tag = tagIdx >= 0 && cells.Count > tagIdx ? cells[tagIdx] : "",
                Id = id,
                Size = sizeIdx >= 0 && cells.Count > sizeIdx ? cells[sizeIdx] : "",
            });
        }
        return result;
    }

    /// <summary>
    /// Scans <paramref name="output"/> for the first non-empty, non-separator
    /// line that splits into at least two columns of header cells, and
    /// returns its text + boundaries + index. Returns (null, _, -1) if no
    /// header is found.
    /// </summary>
    internal static (string? Header, int[] Boundaries, int HeaderIndex) LocateHeader(string output)
    {
        var lines = output.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.StartsWith("---", StringComparison.Ordinal)) continue;
            var b = ComputeColumnBoundaries(line);
            if (b is null || b.Length < 3) continue;
            return (line, b, i);
        }
        return (null, Array.Empty<int>(), -1);
    }
}
