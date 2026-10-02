using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
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
///   - image push/tag/save/load/import, registry login/logout,
///     container export/kill, network connect/disconnect, `system info`
///     — wslc 3.0.1 新增的 CLI 能力，C# 投影里同样没有对应成员。
///
/// These are bridged with <c>wslc list -a</c> / <c>wslc start</c> / <c>wslc stop</c> /
/// <c>wslc rm</c> / <c>wslc image rm</c> / <c>wslc logs</c> /
/// <c>wslc network create|ls|remove</c> / <c>wslc volume create|ls|remove</c> /
/// <c>wslc stats</c> / <c>wslc build -t &lt;tag&gt; &lt;context&gt;</c> /
/// <c>wslc image push|tag|save|load|import</c> / <c>wslc registry login|logout</c> /
/// <c>wslc container export|kill</c> / <c>wslc network connect|disconnect</c> /
/// <c>wslc system info</c>.
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
    /// 所有 CLI 调用（含 <see cref="BuildImageAsync"/>、事件流服务）的统一前置守卫。
    /// 不守卫的话 Process.Start 会抛裸 Win32Exception（"系统找不到指定的文件"），
    /// 既没有可操作的下一步建议，也命中不了 <see cref="TranslateCliError"/> 的映射表。
    /// <para>
    /// <c>internal</c> 而非 <c>private</c>：<see cref="EventStreamService"/> 在
    /// <c>WslcCli</c> 之外单独起进程，必须复用同一守卫与同一份错误文案。
    /// </para>
    /// </summary>
    internal static void EnsureExe()
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

    // ====================================================================
    //  镜像 push / tag / save / load / import
    //
    //  ⚠️ 参数形态全部经真机 `wslc xxx --help` 核实（wslc 3.0.1 GA，2026-10-02），
    //  与 docker 有几处**实质差异**，不要凭 docker 习惯写：
    //   • `push` **只接受一个**位置参数 <image>。docker 的
    //     `docker push <image> <target>` 双位置形态在 wslc 上直接报
    //     「在未预期的情况下找到位置参数」——目标仓库必须写进 image 引用本身
    //     （如 `myreg.io/app:latest`）。
    //   • `tag` 是 `<source> <target>`，**两端都是 image-name[:tag]**，无 -f/-a。
    //   • `save` 的输出路径是 `-o/--output`，且 `<image>...` **必填**（1..N 个）——
    //     不给镜像名会报「未提供所需参数:"image"」，没有「导出全部」的形态。
    //   • `load` 是 `-i/--input`（**不是** docker 的位置参数），且无位置参数。
    //   • `import` 与 docker 不同：tar 路径是**必填位置参数** `<file>`，
    //     可选第二位置参数 `[<image>]` 是重命名，**没有 -o**；
    //     输出是新镜像 ID（stdout 单行，实测 `5444d31e953a`）。
    // ====================================================================

    /// <summary>
    /// 推送镜像到注册表（<c>wslc image push [-a|--all-tags] [-q|--quiet] &lt;image&gt;</c>）。
    ///
    /// <para>
    /// <paramref name="reference"/> 必须**自带仓库前缀**（<c>registry.example.com/app:v1</c>）——
    /// wslc 与 docker 不同，只接受**一个**位置参数，没有「源 + 目标」双参数形态。
    /// </para>
    /// <para>
    /// <paramref name="allTags"/> 对应 <c>-a/--all-tags</c>（推送该镜像的全部标签），
    /// <paramref name="quiet"/> 对应 <c>-q/--quiet</c>（抑制进度输出）。
    /// 推送是长耗时操作，逐行进度经 <paramref name="progress"/> 流式回传。
    /// </para>
    /// </summary>
    public static async Task PushImageAsync(
        string reference, bool allTags, bool quiet,
        IProgress<string>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reference))
            throw new ArgumentException("镜像引用不能为空。", nameof(reference));

        var args = new List<string> { "image", "push" };
        if (allTags) args.Add("-a");
        if (quiet) args.Add("-q");
        args.Add(reference);

        await RunStreamingAsync(args.ToArray(), progress, "wslc image push", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 给镜像打标签（<c>wslc image tag &lt;source&gt; &lt;target&gt;</c>）。
    /// 两端都是 <c>image-name[:tag]</c> 形态；无 <c>-f</c>/<c>-a</c> 之类选项。
    /// 成功时 stdout 无输出（真机实测），失败走 <see cref="TranslateCliError"/> 映射。
    /// </summary>
    public static async Task TagImageAsync(string source, string target, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("源镜像引用不能为空。", nameof(source));
        if (string.IsNullOrWhiteSpace(target))
            throw new ArgumentException("目标镜像引用不能为空。", nameof(target));

        var (exit, _, stderr) = await RunAsync(new[] { "image", "tag", source, target }, ct).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"wslc image tag 失败: {stderr.Trim()}");
    }

    /// <summary>
    /// 把一个或多个镜像导出为 tar 归档
    /// （<c>wslc image save -o &lt;output&gt; &lt;image&gt;...</c>）。
    ///
    /// <para>
    /// ⚠️ <c>&lt;image&gt;...</c> **必填**（1..N 个位置参数），wslc **没有**「导出全部镜像」
    /// 的形态——不传镜像名会报「未提供所需参数:"image"」。输出路径走 <c>-o/--output</c>，
    /// 省略时 tar 会直接写到 stdout（二进制流，不适合 GUI 场景，故本方法强制要求路径）。
    /// </para>
    /// </summary>
    /// <returns>CLI 原始 stdout（成功时通常为空或一行统计）。</returns>
    public static async Task<string> SaveImagesAsync(
        IReadOnlyList<string> references, string outputPath, CancellationToken ct)
    {
        if (references is null || references.Count == 0)
            throw new ArgumentException("至少需要一个镜像引用。", nameof(references));
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("输出路径不能为空。", nameof(outputPath));
        EnsureWritableTarget(outputPath);

        var args = new List<string> { "image", "save", "-o", outputPath };
        args.AddRange(references);

        var (exit, stdout, stderr) = await RunAsync(args.ToArray(), ct).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"wslc image save 失败: {stderr.Trim()}");
        return stdout;
    }

    /// <summary>
    /// 从 tar 归档导入镜像（<c>wslc image load -i &lt;input&gt; [-q]</c>）。
    ///
    /// <para>
    /// ⚠️ 与 docker 不同：输入路径是 <c>-i/--input</c> 选项而**不是位置参数**
    /// （docker 是 <c>docker load -i</c> 但 wslc 同样用 -i；区别在于 wslc **没有**
    /// <c>load &lt;file&gt;</c> 位置参数形态），且 <paramref name="quiet"/> 对应
    /// <c>-q/--quiet</c>（加载期间抑制进度输出）。
    /// </para>
    /// <para>
    /// 成功后 stdout 每行一个「已加载映像: name:tag」（真机实测）。
    /// </para>
    /// </summary>
    public static async Task<string> LoadImagesAsync(
        string inputPath, bool quiet, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
            throw new ArgumentException("归档路径不能为空。", nameof(inputPath));
        if (!File.Exists(inputPath))
            throw new FileNotFoundException($"找不到 tar 归档文件: {inputPath}", inputPath);

        var args = new List<string> { "image", "load", "-i", inputPath };
        if (quiet) args.Add("-q");

        var (exit, stdout, stderr) = await RunAsync(args.ToArray(), ct).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"wslc image load 失败: {stderr.Trim()}");
        return stdout;
    }

    /// <summary>
    /// 从 tarball（docker save 产物等）导入并可同时重命名
    /// （<c>wslc image import &lt;file&gt; [&lt;image&gt;]</c>）。
    ///
    /// <para>
    /// ⚠️ 与 docker 的关键差异：tar 路径是**必填位置参数** <c>&lt;file&gt;</c>，
    /// <b>没有 <c>-o/--output</c></b>；可选第二位置参数 <c>[&lt;image&gt;]</c> 是
    /// 「导入后叫什么」，不是输出路径。
    /// </para>
    /// <para>
    /// 成功时 stdout 是**新镜像的短 ID**（真机实测单行 <c>5444d31e953a</c>），
    /// 不带重命名时该镜像的 REPOSITORY/TAG 为 <c>&lt;none&gt;</c>。
    /// </para>
    /// </summary>
    /// <returns>CLI 原始 stdout（镜像短 ID）。</returns>
    public static async Task<string> ImportImageAsync(
        string filePath, string? reference, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("tarball 路径不能为空。", nameof(filePath));
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"找不到 tarball 文件: {filePath}", filePath);

        var args = new List<string> { "image", "import", filePath };
        if (!string.IsNullOrWhiteSpace(reference)) args.Add(reference);

        var (exit, stdout, stderr) = await RunAsync(args.ToArray(), ct).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"wslc image import 失败: {stderr.Trim()}");
        return stdout;
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
    // errors with "选项名称未被识别"). ----
    //
    // ⚠️ 必须带 `-a`（2026-10-02 实测发现）：`wslc stats` **不带参数时只返回一个容器**
    // （最近的那个），统计页此前因此永远只显示 1 行 —— 是个存量 bug。
    // `stats -a` 才返回全部容器（含已停止的，其值为 0B/0.00%，属真实读数）。
    // 另注：位置参数只认**容器名**，短 ID 与完整 ID 都会报 WSLC_E_CONTAINER_NOT_FOUND。
    public static async Task<IReadOnlyList<StatInfo>> GetStatsAsync(CancellationToken ct)
    {
        var (exit, stdout, stderr) = await RunAsync(new[] { "stats", "-a" }, ct).ConfigureAwait(false);
        return exit == 0 ? ParseStats(stdout) : throw CliFailed("wslc stats", exit, stderr);
    }

    /// <summary>
    /// 数值形态的 stats 快照，供实时采样 / 曲线用（`wslc stats -a --format json`）。
    ///
    /// <para>
    /// 这里破例用 <c>--format json</c>：表格里的 <c>2.715MiB / 30.96GiB</c>、<c>0.00%</c>
    /// 都是给人看的字符串，画曲线要么解析这些（脆弱）要么走 JSON 的同一批字段。
    /// 表格**主路径仍然不用 JSON**（见类注释）。
    /// </para>
    ///
    /// <para>
    /// 形态：**NDJSON，一行一个容器**（多容器实测；单容器时看起来像"单对象"，
    /// 这正是本仓库早期误判"stats 是单对象"的原因）。数组包裹形态也一并兼容。
    /// </para>
    /// </summary>
    public static async Task<IReadOnlyList<StatInfo>> GetStatsSnapshotAsync(CancellationToken ct)
    {
        var (exit, stdout, stderr) = await RunAsync(
            new[] { "stats", "-a", "--format", "json" }, ct).ConfigureAwait(false);
        return exit == 0
            ? ParseStatsJson(stdout)
            : throw CliFailed("wslc stats --format json", exit, stderr);
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

    // ---- prune（维护页）----
    // 四条都**必须**带 `-f`：wslc 自 2.9.12 起 prune 对齐 docker 语义，默认弹交互式
    // 确认提示。非交互调用不加 -f 会让子进程挂住等 stdin（我们的 RunAsync 不喂 stdin，
    // 于是永久等待 → UI 卡在"清理中"）。这里没有例外，也不提供"不带 -f"的入口。
    //
    // 返回 CLI **原始输出**：prune 的回收量文案（"Total reclaimed space: …"）
    // 随版本/语言变化，解析会漂移；原样透传由 UI 直接展示，永不过期。

    public static async Task<string> PruneContainersAsync(CancellationToken ct)
    {
        var (exit, stdout, stderr) = await RunAsync(new[] { "container", "prune", "-f" }, ct).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"wslc container prune 失败: {stderr.Trim()}");
        return stdout;
    }

    public static async Task<string> PruneImagesAsync(bool all, CancellationToken ct)
    {
        var args = all
            ? new[] { "image", "prune", "-f", "-a" }
            : new[] { "image", "prune", "-f" };
        var (exit, stdout, stderr) = await RunAsync(args, ct).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"wslc image prune 失败: {stderr.Trim()}");
        return stdout;
    }

    public static async Task<string> PruneNetworksAsync(CancellationToken ct)
    {
        var (exit, stdout, stderr) = await RunAsync(new[] { "network", "prune", "-f" }, ct).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"wslc network prune 失败: {stderr.Trim()}");
        return stdout;
    }

    public static async Task<string> PruneVolumesAsync(CancellationToken ct)
    {
        var (exit, stdout, stderr) = await RunAsync(new[] { "volume", "prune", "-f" }, ct).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"wslc volume prune 失败: {stderr.Trim()}");
        return stdout;
    }

    // ====================================================================
    //  registry login / logout  ——  ⚠️ 密码只走 stdin
    //
    //  真实参数形态（真机 `wslc registry login --help`，wslc 3.0.1 GA，2026-10-02）：
    //      wslc registry login  [选项] [<server>]
    //      -u, --username <username>     用户名
    //      -p, --password <password>     密码 / PAT        ← **绝对不用**
    //          --password-stdin          从 stdin 读取密码 / PAT   ← 只用这个
    //  server 是**可选位置参数**，省略时由会话定义（因此本方法允许 server 为 null）。
    //
    //  ⚠️ 为什么必须 --password-stdin：`-p` 会把密码**暴露在进程命令行**上，
    //  同机任何用户用 `wslc` 的进程列表 / 任务管理器 / ProcMon 都能看到，
    //  且可能被日志 / 崩溃 dumps 记录。--password-stdin 让密码只经管道进子进程。
    //  因此本文件实现了唯一一条能向子进程 stdin 写数据的执行路径
    //  （RunWithStdinAsync），密码**不落盘、不进日志、不进异常消息**。
    // ====================================================================

    /// <summary>
    /// 登录到镜像仓库（<c>wslc registry login -u &lt;user&gt; --password-stdin [&lt;server&gt;]</c>）。
    ///
    /// <para>
    /// 密码经 <c>--password-stdin</c> 从子进程 stdin 管道写入，**不走 <c>-p</c>**：
    /// <c>-p</c> 会让密码出现在进程命令行里（同机可见、可能被日志留存）。
    /// 密码只存在于本方法的局部变量与管道缓冲中，**不写磁盘、不写日志、
    /// 不进异常消息**（异常里只放 stderr 的错误码与提示）。
    /// </para>
    /// <para>
    /// <paramref name="server"/> 可为 null/空 —— wslc 此时用会话定义的默认服务器。
    /// </para>
    /// </summary>
    public static async Task RegistryLoginAsync(
        string? server, string? username, string password, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("密码不能为空。", nameof(password));

        var args = new List<string> { "registry", "login", "--password-stdin" };
        if (!string.IsNullOrWhiteSpace(username)) { args.Add("-u"); args.Add(username); }
        if (!string.IsNullOrWhiteSpace(server)) args.Add(server);

        var (exit, _, stderr) = await RunWithStdinAsync(args.ToArray(), password, ct).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"wslc registry login 失败: {stderr.Trim()}");
    }

    /// <summary>
    /// 从镜像仓库注销（<c>wslc registry logout [&lt;server&gt;]</c>）。
    /// <paramref name="server"/> 可省略（用会话定义的默认服务器）；无任何选项。
    /// </summary>
    public static async Task RegistryLogoutAsync(string? server, CancellationToken ct)
    {
        var args = new List<string> { "registry", "logout" };
        if (!string.IsNullOrWhiteSpace(server)) args.Add(server);

        var (exit, _, stderr) = await RunAsync(args.ToArray(), ct).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"wslc registry logout 失败: {stderr.Trim()}");
    }

    // ====================================================================
    //  container export / kill
    //
    //  真实参数形态（真机 --help，wslc 3.0.1 GA，2026-10-02）：
    //      wslc container export [选项] <container-id>
    //      -o, --output <file>    写入文件，而不是 STDOUT
    //      wslc container kill [选项] <container-id>...
    //      -s, --signal <signal>  发送到容器的信号（默认：SIGKILL）
    //
    //  ⚠️ help 把位置参数写作 <container-id>，但**实测名字与短 ID 都能用**
    //  （2026-10-02：`wslc export wslc-pg -o …` 与 `wslc kill 20f2ed8f095c`
    //  都成功命中同一容器），故本仓库统一传**容器名**——UI 侧持有的就是名字，
    // 而 `wslc list -a` 的名字列可能重名，调用方需保证名字能唯一命中。
    //  对比：`wslc stats` 的位置参数**只认名字**（短 ID 报 WSLC_E_CONTAINER_NOT_FOUND）。
    // ====================================================================

    /// <summary>
    /// 把容器文件系统导出为 tar 归档
    /// （<c>wslc container export -o &lt;output&gt; &lt;container&gt;</c>）。
    ///
    /// <para>
    /// <c>-o/--output</c> 省略时 tar 走 **STDOUT**（二进制流，GUI 场景不可用），
    /// 故本方法强制要求输出路径。注意这是**文件系统导出**（不含卷挂载与元数据），
    /// 与 <c>image save</c> 不是一回事。
    /// </para>
    /// </summary>
    public static async Task ExportContainerAsync(
        string container, string outputPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(container))
            throw new ArgumentException("容器名不能为空。", nameof(container));
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("输出路径不能为空。", nameof(outputPath));
        EnsureWritableTarget(outputPath);

        var (exit, _, stderr) = await RunAsync(
            new[] { "container", "export", "-o", outputPath, container }, ct).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"wslc container export 失败: {stderr.Trim()}");
    }

    /// <summary>
    /// 强杀容器（<c>wslc container kill [-s &lt;signal&gt;] &lt;container&gt;</c>）。
    ///
    /// <para>
    /// 默认信号是 <c>SIGKILL</c>（不可捕获、不给清理机会）；传
    /// <paramref name="signal"/>（如 <c>SIGTERM</c>、<c>SIGINT</c>）可换信号。
    /// 对已停止的容器会报 <c>WSLC_E_CONTAINER_NOT_RUNNING</c>。
    /// </para>
    /// <para>
    /// 与 <see cref="StopAsync"/> 的区别：<c>stop</c> 是优雅停止（SIGTERM + 等退出），
    /// 容器卡死时只能靠本方法强杀。
    /// </para>
    /// </summary>
    public static async Task KillContainerAsync(
        string container, string? signal, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(container))
            throw new ArgumentException("容器名不能为空。", nameof(container));

        var args = new List<string> { "container", "kill" };
        if (!string.IsNullOrWhiteSpace(signal)) { args.Add("-s"); args.Add(signal); }
        args.Add(container);

        var (exit, _, stderr) = await RunAsync(args.ToArray(), ct).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"wslc container kill 失败: {stderr.Trim()}");
    }

    // ====================================================================
    //  network connect / disconnect
    //
    //  真实参数形态（真机 --help，wslc 3.0.1 GA，2026-10-02）：
    //      wslc network connect    <network-name> <container-id>  [--ip|--network-alias|--link|--driver-opt|--link-local-ip]
    //      wslc network disconnect <network-name> <container-id>
    //
    //  ⚠️ **位置参数顺序是「网络名 + 容器」，与 docker 一致**，不要传反；
    //  help 把第二个写作 <container-id>，但实测**容器名同样可用**
    //  （2026-10-02：`wslc network connect bridge wslc-pg` 成功，随后 disconnect
    //  也成功；重复 disconnect 报 E_FAIL「is not connected to the network」），
    //  因此本仓库统一传容器名（同 KillContainerAsync 的说明）。
    //  ⚠️ 与 `wslc stats` 不同——那里位置参数只认名字、短 ID 会报
    //  WSLC_E_CONTAINER_NOT_FOUND。这里名字/ID 都行，不要据 stats 的结论推断。
    // ====================================================================

    /// <summary>
    /// 把容器接入已有网络（<c>wslc network connect &lt;network&gt; &lt;container&gt;</c>）。
    /// 重复接入会报错（<c>E_FAIL</c>「is already connected to the network」）。
    /// </summary>
    public static async Task ConnectNetworkAsync(
        string network, string container, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(network))
            throw new ArgumentException("网络名称不能为空。", nameof(network));
        if (string.IsNullOrWhiteSpace(container))
            throw new ArgumentException("容器名不能为空。", nameof(container));

        var (exit, _, stderr) = await RunAsync(
            new[] { "network", "connect", network, container }, ct).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"wslc network connect 失败: {stderr.Trim()}");
    }

    /// <summary>
    /// 把容器从网络断开（<c>wslc network disconnect &lt;network&gt; &lt;container&gt;</c>）。
    /// 该容器当前未接入时，报 <c>E_FAIL</c>「is not connected to the network」。
    /// </summary>
    public static async Task DisconnectNetworkAsync(
        string network, string container, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(network))
            throw new ArgumentException("网络名称不能为空。", nameof(network));
        if (string.IsNullOrWhiteSpace(container))
            throw new ArgumentException("容器名不能为空。", nameof(container));

        var (exit, _, stderr) = await RunAsync(
            new[] { "network", "disconnect", network, container }, ct).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"wslc network disconnect 失败: {stderr.Trim()}");
    }

    // ---- 事件流（`wslc events`）----
    //
    // ⚠️ **实测 2026-10-02（wslc 3.0.1.0）：`wslc events` 永不自行退出。**
    // 即使同时给 `--since 5m` 和 `--until <现在>`，它也只是把窗口内的事件**回放一遍**，
    // 然后继续挂在 stdout 上等新事件 —— 进程永不 EOF。
    // 因此下面这个「一次性历史回读」**不能走 RunAsync**（那里是 ReadToEndAsync +
    // WaitForExitAsync，会永久挂死，UI 卡在「加载中」）。
    // 实测：`--since 5m` 回放 3 条历史后仍在流；`--until <过去>` 会报
    // 「`since` 时间不能晚于 `until` 时间 (E_INVALIDARG)」。
    //
    // 解法是**空闲即止**：逐行异步读，一旦连续 `idleTimeout` 没有新行就认为
    // 「历史回放完毕」，主动杀进程返回。长驻监听交给 EventStreamService。

    /// <summary>
    /// 一次性拉取历史事件（<c>wslc events --since &lt;since&gt;</c>）。
    /// 回放到「连续 <paramref name="idleTimeout"/> 无新行」为止后主动结束。
    /// </summary>
    /// <param name="since">回看窗口，如 <c>5m</c> / <c>2h</c>；<c>null</c> 或空白时默认 <c>5m</c>。</param>
    /// <param name="ct">取消令牌：取消即杀进程（与 <see cref="RunAsync"/> 的安全网同构）。</param>
    public static Task<IReadOnlyList<ContainerEvent>> ListEventsAsync(
        string? since, CancellationToken ct) =>
        ListEventsAsync(since, DefaultHistoryIdle, ct);

    /// <summary>历史回读的默认空闲判定期。实测历史是「一次性倾泻」，几百毫秒足够。</summary>
    private static readonly TimeSpan DefaultHistoryIdle = TimeSpan.FromMilliseconds(400);

    /// <summary>解析一段 <c>wslc events</c> 输出为事件列表（纯函数，单测直接喂真机原文）。</summary>
    internal static IReadOnlyList<ContainerEvent> ParseEventLines(string? output)
    {
        var result = new List<ContainerEvent>();
        if (string.IsNullOrWhiteSpace(output)) return result;
        foreach (var raw in output.Split('\n'))
        {
            var ev = EventLineParser.Parse(raw.TrimEnd('\r'));
            if (ev is not null) result.Add(ev);
        }
        return result;
    }

    /// <summary>
    /// 逐行异步读 + 空闲即止地拉取历史事件。
    /// </summary>
    /// <remarks>
    /// 长驻流（不传 <paramref name="since"/> 的持续监听）请用
    /// <see cref="EventStreamService"/>，别用这个。
    /// </remarks>
    internal static async Task<IReadOnlyList<ContainerEvent>> ListEventsAsync(
        string? since, TimeSpan idleTimeout, CancellationToken ct)
    {
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
        psi.ArgumentList.Add("events");
        // 空 since 传下去会让 wslc 报参数错误，替换成默认窗口。
        psi.ArgumentList.Add("--since");
        psi.ArgumentList.Add(string.IsNullOrWhiteSpace(since) ? "5m" : since);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 wslc 进程。");

        var collected = new List<ContainerEvent>();
        var sawAny = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lastLineAt = DateTime.UtcNow;
        var gate = new object();

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (gate)
            {
                var ev = EventLineParser.Parse(e.Data);
                if (ev is not null) collected.Add(ev);
                lastLineAt = DateTime.UtcNow;
            }
            sawAny.TrySetResult(true);
        };
        proc.BeginOutputReadLine();
        // stderr 同样要排空，否则 wslc 报错写满 4 KB 管道缓冲会把自己堵死。
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        // 等第一行（有界）：进程可能根本无输出（窗口内没事件），不能死等。
        try
        {
            await sawAny.Task.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 5 秒内没有任何输出：视为「该窗口无事件」，正常返回空列表。
        }

        // 空闲即止：历史是一口气倾泻完的，等一小段静默就可以收工。
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            bool idle;
            lock (gate) idle = (DateTime.UtcNow - lastLineAt) >= idleTimeout;
            if (idle || proc.HasExited) break;
            await Task.Delay(25, ct).ConfigureAwait(false);
        }

        string stderr;
        try { stderr = await stderrTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { stderr = ""; }

        if (!proc.HasExited)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }
        try { await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
        catch { /* best effort */ }

        // 有事件就不算失败（窗口内确实发生过事）；一条都没有且 wslc 报错才抛，
        // 否则「最近 5 分钟无事件」会被误报成错误。
        if (collected.Count == 0 && proc.ExitCode != 0)
            throw CliFailed("wslc events", proc.ExitCode, stderr);

        return collected;
    }

    // ---- 容器内文件系统（文件浏览窗口）----
    // 全部走 CLI：SDK 3.0.1 只有「往容器里跑进程 / 拿进程 stdout」的抽象，
    // 没有文件系统投影；列目录靠 `exec ls`，搬文件靠 `container cp`。
    // 路径经 ArgumentList 逐参数传递（见 RunAsync），含空格/引号不会被拆开。

    /// <summary>
    /// 列出容器内某个目录。用 <c>-a</c>（不是 <c>-A</c>）拿到 <c>.</c>/<c>..</c> 以便
    /// 在解析层统一丢弃，两种实现（busybox / GNU）行为一致。
    /// </summary>
    public static async Task<IReadOnlyList<ContainerFileEntry>> ListDirectoryAsync(
        string container, string path, CancellationToken ct)
    {
        var (exit, stdout, stderr) = await RunAsync(
            new[] { "exec", container, "ls", "-la", path }, ct).ConfigureAwait(false);
        if (exit != 0)
        {
            // `ls` 的错误在 stderr（"ls: /nope: No such file or directory"）；
            // 少数情况下 exec 自身的报错只出现在 stdout，兜底取其一。
            var detail = stderr.Trim().Length > 0 ? stderr.Trim() : stdout.Trim();
            throw new InvalidOperationException($"读取容器目录失败: {detail}");
        }
        return ParseDirectoryListing(stdout);
    }

    /// <summary>
    /// 容器 → 宿主（`wslc container cp &lt;ctr&gt;:&lt;path&gt; &lt;local&gt;`）。
    /// 目标**可以不存在**，按 <paramref name="localPath"/> 的名字创建（实测 2026-10-02）。
    /// </summary>
    public static async Task CopyFromContainerAsync(
        string container, string containerPath, string localPath, CancellationToken ct)
    {
        var (exit, _, stderr) = await RunAsync(
            new[] { "container", "cp", $"{container}:{containerPath}", localPath }, ct).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"从容器复制失败: {stderr.Trim()}");
    }

    /// <summary>
    /// 宿主 → 容器（`wslc container cp &lt;local&gt; &lt;ctr&gt;:&lt;dir&gt;/`）。
    ///
    /// <para>
    /// ⚠️ **wslc 的 cp 与 docker 语义不同，实测 2026-10-02（wslc 3.0.1）**：
    /// 目标**必须是容器内已存在的目录**，且**不能指定目标文件名**（名字沿用本地文件名）。
    /// </para>
    /// <list type="bullet">
    ///   <item><c>cp ./a.txt ctr:/tmp/a.txt</c>（目标文件不存在）→ <c>ERROR_PATH_NOT_FOUND</c>「Could not find the file /tmp/a.txt in container」</item>
    ///   <item><c>cp ./a.txt ctr:/tmp/a.txt</c>（目标文件已存在）→ <c>E_FAIL</c>「extraction point is not a directory」</item>
    ///   <item>✅ <c>cp ./a.txt ctr:/tmp/</c>（已存在的目录）→ 成功，落在 <c>/tmp/a.txt</c></item>
    ///   <item>❌ stdin 形态 <c>cp - ctr:/tmp/x.txt</c> 同样报 PATH_NOT_FOUND —— 帮助里写的「标准输入到容器」在 3.0.1 上对不存在的目标不可用</item>
    /// </list>
    /// 因此 <paramref name="containerDir"/> 是**目录**（末尾斜杠由本方法补齐），
    /// 不做「另存为」这种交互——那不是 wslc 能表达的语义。
    /// </summary>
    public static async Task CopyToContainerAsync(
        string container, string localPath, string containerDir, CancellationToken ct)
    {
        var dir = containerDir.EndsWith('/') ? containerDir : containerDir + "/";
        var (exit, _, stderr) = await RunAsync(
            new[] { "container", "cp", localPath, $"{container}:{dir}" }, ct).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException(
                $"复制到容器失败: {stderr.Trim()}（注意 wslc 要求目标目录必须已存在，且不能指定目标文件名）");
    }

    /// <summary>
    /// 删除容器内一个路径（`exec &lt;ctr&gt; rm -rf &lt;path&gt;`）。
    /// **递归且不可撤销** —— 调用方必须先确认；这里不做二次询问。
    /// </summary>
    public static async Task DeletePathAsync(string container, string path, CancellationToken ct)
    {
        var (exit, stdout, stderr) = await RunAsync(
            new[] { "exec", container, "rm", "-rf", path }, ct).ConfigureAwait(false);
        if (exit != 0)
        {
            var detail = stderr.Trim().Length > 0 ? stderr.Trim() : stdout.Trim();
            throw new InvalidOperationException($"删除失败: {detail}");
        }
    }

    /// <summary>`ls -la` 的列分隔符（空格 + 制表符）。</summary>
    private static readonly char[] ListingSeparators = { ' ', '\t' };

    /// <summary>
    /// 解析 `ls -la` 输出。真实形态见 <see cref="ContainerFileEntry"/> 的类注释；
    /// 首行 <c>total 16</c> 靠"第 1 段不是权限串"自然丢弃，不依赖它的本地化文案。
    ///
    /// <para>
    /// 用 <c>Split(separators, 9, RemoveEmptyEntries)</c> 而不是正则：前 8 列都是
    /// 无空格的单 token，第 9 段就是文件名整体（**保留内部空格**，见真机夹具
    /// <c>name with space.txt</c>）。要求恰好 9 段 + 第 1 段是权限串 + 第 5 段是数字，
    /// 三重校验把 <c>total</c>、告警行、空行全部挡掉。
    /// </para>
    ///
    /// <para>
    /// 已知取舍：文件名**以空格开头**时前导空格会被吃掉（`ls` 自身的列填充与之
    /// 本就无法区分）；<c>.</c> 与 <c>..</c> 被丢弃，上层导航由 UI 的「上级目录」负责。
    /// </para>
    /// </summary>
    internal static IReadOnlyList<ContainerFileEntry> ParseDirectoryListing(string output)
    {
        var result = new List<ContainerFileEntry>();
        if (string.IsNullOrEmpty(output)) return result;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;

            var parts = line.Split(ListingSeparators, 9, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 9) continue;
            if (!IsPermissionString(parts[0])) continue;
            if (!long.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var size)) continue;

            var kind = parts[0][0] switch
            {
                'd' => ContainerFileKind.Directory,
                'l' => ContainerFileKind.Link,
                '-' => ContainerFileKind.File,
                _ => ContainerFileKind.Other,
            };

            var name = parts[8];
            var linkTarget = "";
            // 只在权限位是 l 时才切 " -> "：普通文件名里含这三个字符不会误判成链接。
            if (kind == ContainerFileKind.Link)
            {
                var arrow = name.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrow >= 0)
                {
                    linkTarget = name[(arrow + 4)..];
                    name = name[..arrow];
                }
            }

            if (name.Length == 0 || name == "." || name == "..") continue;

            result.Add(new ContainerFileEntry
            {
                Name = name,
                Permissions = parts[0],
                Owner = parts[2],
                Group = parts[3],
                SizeBytes = size,
                // 第 8 列可能是时刻（16:44）也可能是年份（2026），两者都是单 token，
                // 拼起来原样展示，不做日期归一。
                Modified = $"{parts[5]} {parts[6]} {parts[7]}",
                LinkTarget = linkTarget,
                Kind = kind,
            });
        }
        return result;
    }

    /// <summary>
    /// 10 字符权限串校验（首字符类型 + 9 个 rwx 位）。
    /// 允许第 11 个字符存在（SELinux 的 `.`、ACL 的 `+`、扩展属性的 `@`）。
    /// </summary>
    private static bool IsPermissionString(string s)
    {
        if (s.Length < 10) return false;
        if ("-dlbcps".IndexOf(s[0]) < 0) return false;
        for (var i = 1; i < 10; i++)
            if ("rwxsStT-".IndexOf(s[i]) < 0) return false;
        return true;
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
        string[] args, CancellationToken ct) =>
        await RunCoreAsync(args, stdin: null, ct).ConfigureAwait(false);

    /// <summary>
    /// 唯一一条能向子进程 <b>stdin</b> 写数据的执行路径。
    ///
    /// <para>
    /// 目前只有 <see cref="RegistryLoginAsync"/> 用它（<c>--password-stdin</c>）。
    /// 写成独立方法而不是给 <see cref="RunAsync"/> 加可选参数，是因为「喂 stdin」
    /// 是一类**有安全含义**的调用：它只该服务于「凭据 / 大体积二进制」这类
    /// 不能走命令行的输入，签名上单独暴露便于审计。
    /// </para>
    /// </summary>
    /// <param name="stdin">要写入 stdin 的文本；写入后立即<b>关闭</b> stdin（发 EOF）。
    /// wslc 读到 EOF 才认为输入结束，否则会一直等 → 永久挂起。</param>
    private static async Task<(int Exit, string Stdout, string Stderr)> RunWithStdinAsync(
        string[] args, string stdin, CancellationToken ct) =>
        await RunCoreAsync(args, stdin, ct).ConfigureAwait(false);

    /// <summary>
    /// 执行 wslc 并收集输出。<paramref name="stdin"/> 非 null 时重定向并写入 stdin。
    ///
    /// <para>
    /// ⚠️ <b>密码等敏感输入绝不进 <paramref name="args"/></b>：命令行对同机所有
    /// 进程可见。凭据只能走 <paramref name="stdin"/>。
    /// </para>
    /// </summary>
    private static async Task<(int Exit, string Stdout, string Stderr)> RunCoreAsync(
        string[] args, string? stdin, CancellationToken ct)
    {
        EnsureExe();

        var psi = new ProcessStartInfo
        {
            FileName = ExePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
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

        // 先把 stdin 写完并关闭，再排空输出流。
        // 顺序很关键：写 stdin 时进程可能已经在往 stdout 灌数据
        // （login 失败时 stdout/stderr 都有内容），若先把 stdout 排空、
        // 再写 stdin，进程却在等 stdin 就会互锁。
        // 写完立刻 Close() 发 EOF —— 否则 wslc 会一直等更多输入而永不退出。
        if (stdin is not null)
        {
            await proc.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
            proc.StandardInput.Flush();
            proc.StandardInput.Close();
        }

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
    /// 逐行流式执行（长耗时命令：push / build 这类）。
    /// stdout 走 <paramref name="progress"/> 回调，stderr 收集到异常消息里。
    /// </summary>
    private static async Task RunStreamingAsync(
        string[] args, IProgress<string>? progress, string command, CancellationToken ct)
    {
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
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 wslc 进程。");
        using var _reg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(); } catch { /* best effort */ }
        });

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) progress?.Report(e.Data);
        };
        proc.BeginOutputReadLine();
        // stderr 并发排空（同 RunCoreAsync 的死锁理由）。
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"{command} 失败 (exit {proc.ExitCode}): {stderr.Trim()}");
    }

    /// <summary>
    /// 导出类命令（save / export）的前置检查：目标文件所在目录必须已存在。
    /// 提前抛出可读异常，而不是让 wslc 报一个含糊的路径错误。
    /// </summary>
    private static void EnsureWritableTarget(string outputPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            throw new DirectoryNotFoundException($"输出目录不存在: {dir}");
    }

    // ---- system info（SDK 3.0.1 无「版本/会话」查询投影 → CLI 桥接）----
    // 真实形态（真机 wslc 3.0.1 GA，2026-10-02）见 SystemInfo 类注释。
    // `system info` 支持 `--format json`，但 AGENTS.md 已记录 wslc 各子命令的
    // JSON 形态不一致；本命令的 table 形态极稳（两段键值 + 一张三列表），
    // 且键名需要保留本地化原文给用户看，所以走文本解析。
    public static async Task<SystemInfo> GetSystemInfoAsync(CancellationToken ct)
    {
        var (exit, stdout, stderr) = await RunAsync(new[] { "system", "info" }, ct).ConfigureAwait(false);
        return exit == 0 ? ParseSystemInfo(stdout) : throw CliFailed("wslc system info", exit, stderr);
    }

    /// <summary>
    /// 解析 <c>wslc system info</c> 的两段式输出。真实形态见 <see cref="SystemInfo"/>。
    ///
    /// <para>
    /// 结构：<c>客户端:</c> / <c>服务器:</c> 两个小节标题（**中文冒号**，行尾无空格），
    /// 下面各跟着 <c>键: 值</c> 行；<c>会话: N</c> 之后是三列表
    /// <c>ID / 创建者 PID / 显示名称</c>。三个字段全是中文，且英文 locale 下会变，
    /// 因此键名匹配**同时接受中英文**（见下方各 <c>case</c> 分支），匹配时按
    /// 「键名去掉冒号、去空白」<b>全等</b>比较，<b>不用</b> <c>StartsWith</c>
    /// —— <c>会话</c> 是 <c>会话管理器版本</c> 的前缀，前缀匹配会把版本号误当会话数。
    /// </para>
    ///
    /// <para>
    /// 表头 <c>ID   创建者 PID   显示名称</c> 的 <c>创建者 PID</c> 列名**含一个空格**，
    /// 但按「2+ 空格」切列仍得到 3 个 caption（<c>ID</c>/<c>创建者 PID</c>/<c>显示名称</c>）。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>数据行不按表头列位切片</b>，而是按「2+ 空格切最多 3 段」
    /// （见 <see cref="SplitSessionRow"/>）：表头列位是从<b>表头自己</b>推出来的，
    /// 一旦某行的 PID 比表头 caption 窄（例如 3 位的 <c>abc</c> 撞上 8 字符宽的
    /// <c>创建者 PID</c>），整行相对列位就会左移，按列位切片会把显示名称
    /// <b>啃掉开头几个字母</b>（实测 <c>some-session</c> → <c>me-session</c>）。
    /// 按分隔符切则与列宽完全无关。
    /// </para>
    ///
    /// <para>
    /// 解析器对残缺输入**宽容**：不认识的行跳过、缺字段留空、
    /// <see cref="SystemInfo.SessionCount"/> 读不到时为 <c>null</c>（不谎报 0）。
    /// </para>
    /// </summary>
    internal static SystemInfo ParseSystemInfo(string output)
    {
        var info = new SystemInfo();
        if (string.IsNullOrWhiteSpace(output)) return info;

        var sessions = new List<WslcSessionInfo>();
        // null = 还没进「服务器:」小节；进入后为 true，用于把会话表归到正确小节。
        bool? inServer = null;
        // 会话表表头首次出现的位置 + 其列边界（数据行据此判定"是不是会话行"）。
        int[]? sessionBounds = null;
        int sessionHeaderIdx = -1;

        var lines = output.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0) continue;

            // 小节标题：以「客户端:」/「服务器:」结尾（中文或英文冒号）。
            if (line == "客户端:" || line == "客户端：" || line == "Client:")
            { inServer = false; continue; }
            if (line == "服务器:" || line == "服务器：" || line == "Server:")
            { inServer = true; continue; }

            // 会话表。数据行按「2+ 空格切成最多 3 段」解析，
            // 而不是按表头列位切片（见 ReadSessionDisplayName 的注释）。
            if (inServer == true && sessionBounds is not null && i > sessionHeaderIdx)
            {
                var cells = SplitSessionRow(line);
                if (cells is not null)
                {
                    _ = int.TryParse(cells.Value.CreatorPid, NumberStyles.None, CultureInfo.InvariantCulture, out var pid);
                    sessions.Add(new WslcSessionInfo
                    {
                        Id = cells.Value.Id,
                        CreatorPid = pid,
                        DisplayName = cells.Value.DisplayName,
                    });
                    continue;
                }
            }
            // 尚未见到表头时，先尝试把本行当表头（表头在数据行之前）。
            if (inServer == true && sessionBounds is null && IsSessionHeader(line))
            {
                sessionBounds = ComputeColumnBoundaries(line);
                sessionHeaderIdx = i;
                continue;
            }

            // 键值行：「键: 值」。设置文件的值是路径（含 Windows 盘符冒号 C:\…），
            // 所以只在**第一个**冒号处切分，值里的冒号必须原样保留。
            var colon = line.IndexOf(':');
            if (colon < 0) colon = line.IndexOf('：');
            if (colon <= 0) continue;

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (key.Length == 0 || value.Length == 0) continue;

            switch (key)
            {
                case InfoKeys.WslVersion:
                case InfoKeys.WslVersionEn: info.WslVersion = value; break;
                case InfoKeys.KernelVersion:
                case InfoKeys.KernelVersionEn: info.KernelVersion = value; break;
                case InfoKeys.Direct3DVersion:
                case InfoKeys.Direct3DVersionEn: info.Direct3DVersion = value; break;
                case InfoKeys.DxCoreVersion:
                case InfoKeys.DxCoreVersionEn: info.DxCoreVersion = value; break;
                case InfoKeys.WindowsVersion:
                case InfoKeys.WindowsVersionEn: info.WindowsVersion = value; break;
                case InfoKeys.SettingsFile:
                case InfoKeys.SettingsFileEn: info.SettingsFile = value; break;
                case InfoKeys.SessionManagerVersion:
                case InfoKeys.SessionManagerVersionEn: info.SessionManagerVersion = value; break;
                case InfoKeys.SessionCount:
                case InfoKeys.SessionCountEn:
                    if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                        info.SessionCount = n;
                    break;
            }
        }

        if (sessions.Count > 0) info.Sessions = sessions;
        return info;
    }

    /// <summary>
    /// 判定一行是否就是会话表的表头（三列且首列是 ID）。
    /// 只在「服务器:」小节内调用。
    /// </summary>
    private static bool IsSessionHeader(string line)
    {
        var b = ComputeColumnBoundaries(line);
        // 3 列（ID / 创建者 PID / 显示名称）。
        if (b is null || b.Length < 4) return false;
        // 首列必须是 ID 的各种写法；只认首列即可 —— 数据行首列是数字或短 ID，
        // 不会恰好等于这些 caption。
        return FindColumn(b, line, "ID") >= 0
            || FindColumn(b, line, "标识") >= 0
            || FindColumn(b, line, "会话 ID") >= 0;
    }

    /// <summary>
    /// 会话表数据行 → 三个字段（ID / 创建者 PID / 显示名称）。
    /// 段数不足（非数据行）时返回 null。
    /// </summary>
    private static (string Id, string CreatorPid, string DisplayName)? SplitSessionRow(string line)
    {
        // 按「2+ 空格」切成**最多 3 段**：显示名称是最后一列且可能含空格，
        // 切到 3 段后它整体保留。手工切分（而不是 Regex.Split）是为了避开
        // 各重载在 (count) / (options) / (matchTimeout) 之间的歧义。
        var parts = SplitByWideGap(line.Trim(), 3);
        if (parts.Count < 3) return null;
        var id = parts[0].Trim();
        if (id.Length == 0) return null;
        return (id, parts[1].Trim(), parts[2].Trim());
    }

    /// <summary>
    /// 按「连续 2 个及以上的空格 / 制表符」切分，最多产出 <paramref name="maxParts"/> 段
    /// （最后一段保留其余全部内容，含空格）。
    /// </summary>
    private static List<string> SplitByWideGap(string line, int maxParts)
    {
        var result = new List<string>(maxParts);
        var start = 0;
        var i = 0;
        while (i < line.Length)
        {
            // 定位一段连续空白。
            if (line[i] != ' ' && line[i] != '\t') { i++; continue; }
            var wsStart = i;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            var gap = i - wsStart;

            // 只有 1 个空白不算列分隔（那是字段内部的空格）。
            if (gap < 2) continue;

            // 最后一段：把剩余全部（含后续空格）并进来。
            if (result.Count == maxParts - 1)
            {
                result.Add(line[start..]);
                return result;
            }
            result.Add(line[start..wsStart]);
            start = i;
        }
        result.Add(start <= line.Length ? line[start..] : "");
        return result;
    }

    /// <summary>
    /// <c>wslc system info</c> 的键名（中英双语全等匹配用）。
    /// 抽成常量是因为 <see cref="ParseSystemInfo"/> 的 switch 需要编译期常量 case。
    /// </summary>
    private static class InfoKeys
    {
        public const string WslVersion = "WSL 版本";
        public const string WslVersionEn = "WSL version";
        public const string KernelVersion = "内核版本";
        public const string KernelVersionEn = "Kernel version";
        public const string Direct3DVersion = "Direct3D 版本";
        public const string Direct3DVersionEn = "Direct3D version";
        public const string DxCoreVersion = "DXCore 版本";
        public const string DxCoreVersionEn = "DXCore version";
        public const string WindowsVersion = "Windows 版本";
        public const string WindowsVersionEn = "Windows version";
        public const string SettingsFile = "设置文件";
        public const string SettingsFileEn = "Settings file";
        public const string SessionManagerVersion = "会话管理器版本";
        public const string SessionManagerVersionEn = "Session manager version";
        public const string SessionCount = "会话";
        public const string SessionCountEn = "Sessions";
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
    /// <summary>
    /// 解析 `wslc stats -a --format json`（**NDJSON：一行一个容器**，多容器实测确认；
    /// 数组包裹形态也兼容）。字段名与 docker 对齐：
    /// <c>Name / CPUPerc("2.20%") / MemUsage("3.719MiB / 30.96GiB") / MemPerc / NetIO / BlockIO / PIDs / ID</c>。
    ///
    /// <para>
    /// 只做两件事：填给人看的字符串（与表格路径一致）+ 抽两个数（<see cref="StatInfo.CpuPercent"/>、
    /// <see cref="StatInfo.MemUsedBytes"/>）供曲线用。数值解析失败时该条 <c>HasNumbers=false</c>，
    /// 曲线上跳过该点而不是画成 0（画成 0 会被误读成"CPU 归零"）。
    /// </para>
    /// </summary>
    internal static IReadOnlyList<StatInfo> ParseStatsJson(string output)
    {
        var result = new List<StatInfo>();
        if (string.IsNullOrWhiteSpace(output)) return result;

        // 数组包裹形态（单行 [ {...}, {...} ]）
        var trimmed = output.TrimStart();
        if (trimmed.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(output);
                foreach (var el in doc.RootElement.EnumerateArray())
                    AddStatFromJson(result, el);
            }
            catch (JsonException) { /* 畸形 JSON：返回已解析到的部分 */ }
            return result;
        }

        // NDJSON：逐行独立解析，单行坏掉不影响其余
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] != '{') continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                AddStatFromJson(result, doc.RootElement);
            }
            catch (JsonException) { /* 跳过坏行 */ }
        }
        return result;
    }

    private static void AddStatFromJson(List<StatInfo> sink, JsonElement el)
    {
        var name = GetJsonString(el, "Name");
        if (name.Length == 0) return;

        var cpuText = GetJsonString(el, "CPUPerc");
        var memText = GetJsonString(el, "MemUsage");

        var stat = new StatInfo
        {
            Container = name,
            Cpu = cpuText,
            Mem = memText,
            MemPercent = GetJsonString(el, "MemPerc"),
            NetIo = GetJsonString(el, "NetIO"),
            BlockIo = GetJsonString(el, "BlockIO"),
            Pids = el.TryGetProperty("PIDs", out var pids) ? pids.ToString() : "",
        };

        // CPU："2.20%" → 2.20
        var cpuOk = TryParsePercent(cpuText, out var cpuPercent);
        stat.CpuPercent = cpuPercent;

        // 内存："3.719MiB / 30.96GiB" → 取斜杠前那一段
        var memOk = false;
        var parts = memText.Split('/');
        if (parts.Length > 0 && SizeParser.TryParseBytes(parts[0].Trim(), out var memUsed))
        {
            stat.MemUsedBytes = memUsed;
            memOk = true;
        }

        stat.HasNumbers = cpuOk && memOk;
        sink.Add(stat);
    }

    private static string GetJsonString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    /// <summary>解析 `"2.20%"`。百分号可有可无，但**空串/非数字返回 false**。</summary>
    internal static bool TryParsePercent(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().TrimEnd('%').Trim();
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

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
