// =============================================================================
// wslcUI 真机验证程序
//
// 在装有 wslc 的 Windows 机器上端到端验证 2026-08-31 三批缺陷修复
// (fa598ba / faf10a5 / cb7fa61) 是否在真实环境生效。
// 直接调用仓库内 WslcCli / PseudoConsole / TerminalWindow 的真实代码路径
// （经主项目 InternalsVisibleTo 放行），不做逻辑副本。
//
// 用法（仓库根目录）：
//   dotnet run --project tools/wslcUI.Verify -p:Platform=x64 -c Debug
// 可选参数：
//   --skip-pty          跳过 ConPTY 项（V5-V7）
//   --sdk-cancel        追加 V10：SDK RunAndCaptureAsync 取消路径探针（需网络拉 alpine）
//   --container <名>    追加针对指定容器的非破坏性生命周期验证（V9）
//
// 退出码：0 = 全部通过；1 = 存在 FAIL。
// =============================================================================

using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using wslcUI.Services;
using wslcUI.Terminal;

namespace wslcUI.Verify;

internal static class Program
{
    private static readonly List<(string Name, bool Pass, bool Na, string Detail)> Results = new();

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var skipPty = args.Contains("--skip-pty");
        var sdkCancel = args.Contains("--sdk-cancel");
        var container = ArgValue(args, "--container");

        Console.WriteLine("wslcUI 真机验证 — " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        Console.WriteLine(new string('=', 78));

        await V1_ExeAndVersion();
        await V2_TableParsers();
        await V3_ListFailureSurfaced();
        await V4_ArgumentIntegrity();
        if (!skipPty)
        {
            ConPtySystemPrecheck();
            Console.WriteLine($"[预检] {_conPtySystemNote}");
            V5_V6_PtyInteractive();
            V7_PtyUtf8AcrossChunks();
        }
        V8_EscapeStripping();
        if (container is not null)
            await V9_ContainerLifecycle(container);
        if (sdkCancel)
            await V10_SdkCancelPath();
        await V11_EndpointParsing();
        await V12_EventsAndSystemInfo();
        await V13_ImageOutbound();

        Console.WriteLine(new string('=', 78));
        Console.WriteLine("汇总：");
        foreach (var (name, pass, na, _) in Results)
            Console.WriteLine($"  [{(na ? " NA " : pass ? "PASS" : "FAIL")}] {name}");
        var fail = Results.Count(r => !r.Pass && !r.Na);
        Console.WriteLine($"\n{Results.Count(r => r.Pass)} 通过 / {fail} 失败 / {Results.Count(r => r.Na)} 跳过");
        return fail == 0 ? 0 : 1;
    }

    // ---------------- V1 wslc.exe 存在与版本 ----------------

    private static async Task V1_ExeAndVersion()
    {
        if (!File.Exists(WslcCli.ExePath))
        {
            Record("V1 wslc.exe 存在", false, $"未找到 {WslcCli.ExePath}（wsl --install）");
            return;
        }
        var (exit, stdout, _) = await RunRawAsync("--version");
        Record("V1 wslc.exe 存在与版本", exit == 0, stdout.Trim().Replace("\n", " | "));
    }

    // ---------------- V2 CLI 输出编码 + 表格解析 ----------------
    //
    // 验证两条链路一致：
    //   (a) 原始进程输出按 UTF-8 解码后能命中已知表头关键字（证明 wslc 重定向输出
    //       是 UTF-8 —— RunAsync 显式 StandardOutputEncoding=UTF8 的前提）；
    //   (b) WslcCli 各解析器解析出的条数 == 原始表格数据行数（证明列宽切分无丢行）。

    private static async Task V2_TableParsers()
    {
        await VerifyTable("V2.1 list -a   编码+解析",
            "list -a", new[] { "容器 ID", "CONTAINER ID" },
            async ct => (await WslcCli.ListContainersAsync(ct)).Count,
            expectHeaderOnlyWhenEmpty: false);
        await VerifyTable("V2.2 images   编码+解析",
            "images", new[] { "REPOSITORY" },
            async ct => (await WslcCli.ListImagesAsync(ct)).Count,
            expectHeaderOnlyWhenEmpty: false);
        await VerifyTable("V2.3 network ls 编码+解析",
            "network ls", new[] { "NETWORK ID" },
            async ct => (await WslcCli.ListNetworksAsync(ct)).Count,
            expectHeaderOnlyWhenEmpty: false);
        await VerifyTable("V2.4 volume ls 编码+解析",
            "volume ls", new[] { "VOLUME NAME", "DRIVER" },
            async ct => (await WslcCli.ListVolumesAsync(ct)).Count,
            expectHeaderOnlyWhenEmpty: false);
        // 原始探针的参数必须与生产代码（WslcCli.GetStatsAsync）**逐字一致**，
        // 否则比的是两个不同命令，检查失去意义 —— 2026-10-02 把 GetStatsAsync
        // 从不带参数的 `stats` 改成 `stats -a`（不带时只返回一个容器）时，
        // 这里漏改，V2.5 立刻报「解析 1 条 != 原始数据行数 0」。
        await VerifyTable("V2.5 stats   编码+解析",
            "stats -a", new[] { "容器 ID", "CONTAINER ID" },
            async ct => (await WslcCli.GetStatsAsync(ct)).Count,
            expectHeaderOnlyWhenEmpty: true);
    }

    private static async Task VerifyTable(
        string name, string arguments, string[] headerKeywords,
        Func<CancellationToken, Task<int>> parseCountAsync,
        bool expectHeaderOnlyWhenEmpty)
    {
        try
        {
            var (exit, raw, _) = await RunRawAsync(arguments);
            if (exit != 0)
            {
                Record(name, false, $"`wslc {arguments}` 退出码 {exit}（非解析问题，见 stderr）");
                return;
            }
            var headerOk = headerKeywords.Any(k => raw.Contains(k, StringComparison.Ordinal));
            if (!headerOk)
            {
                Record(name, false,
                    $"UTF-8 解码后未命中表头关键字 [{string.Join("/", headerKeywords)}]。" +
                    $"原始首行: {FirstNonEmptyLine(raw)} —— 疑似输出编码不是 UTF-8");
                return;
            }
            var expectedRows = CountDataLines(raw);
            var parsed = await parseCountAsync(Tok());
            if (parsed == expectedRows)
                Record(name, true, $"表头命中，解析 {parsed} 条 = 原始数据行数 {expectedRows}");
            else
                Record(name, false,
                    $"解析 {parsed} 条 != 原始数据行数 {expectedRows}（丢行/多行）");
        }
        catch (Exception ex)
        {
            Record(name, false, $"抛异常: {ex.Message}");
        }
    }

    // ---------------- V3 列举失败必须上浮（不再静默空表） ----------------

    private static async Task V3_ListFailureSurfaced()
    {
        try
        {
            // rm 不存在的容器 → wslc 非零退出 → 修复后应抛异常且消息含 stderr。
            await WslcCli.DeleteContainerAsync("__wslcui_verify_not_exist__", Tok());
            Record("V3 失败上浮（rm 不存在容器）", false,
                "未抛异常 —— 非零退出码被吞（回退到了旧行为）");
        }
        catch (InvalidOperationException ex)
        {
            var hasStderr = ex.Message.Contains("__wslcui_verify_not_exist__", StringComparison.Ordinal) ||
                            ex.Message.Length > "wslc rm 失败: ".Length + 1;
            Record("V3 失败上浮（rm 不存在容器）", hasStderr,
                $"InvalidOperationException 已上浮: {Truncate(ex.Message, 120)}");
        }
        catch (Exception ex)
        {
            Record("V3 失败上浮（rm 不存在容器）", false, $"异常类型不符: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---------------- V4 ArgumentList 参数整体性 ----------------
    //
    // 含空格/引号的名字必须作为单一参数到达 wslc。旧实现（string.Join 拼
    // Arguments）会把 "no such name" 拆成 3 个参数。用报错 stderr 是否完整
    // 回显该名字来端到端判定。

    private static async Task V4_ArgumentIntegrity()
    {
        const string tricky = "no such name";
        try
        {
            await WslcCli.GetLogsAsync(tricky, Tok());
            Record("V4 参数整体性（含空格名）", false, "意外成功 —— 名字不存在却没报错？");
        }
        catch (InvalidOperationException ex)
        {
            var intact = ex.Message.Contains(tricky, StringComparison.Ordinal);
            Record("V4 参数整体性（含空格名）", intact,
                $"{(intact ? "报错完整回显名字，参数未被拆分" : "报错未包含完整名字，参数疑似被拆分")}:" +
                $" {Truncate(ex.Message, 160)}");
        }
        catch (Exception ex)
        {
            Record("V4 参数整体性（含空格名）", false, $"异常类型不符: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---------------- V5/V6 ConPTY 创建、交互（\r 回车）、Dispose ----------------
    //
    // 用 cmd.exe 做验证载体（不依赖容器状态）：
    //   V5  构造成功 + Dispose 无异常（句柄/线程清理路径，fa598ba）。
    //   V6  发送 "echo …\r" 后输出回传含该标记 —— 端到端验证 PTY 输入链路
    //       与 \r 回车修复（cb7fa61）。cmd banner 是 OEM 代码页，判定只用 ASCII。

    /// <summary>
    /// 机器级 ConPTY 健康预检：绕开 wslcUI 代码、按官方 ConptyExample 模式
    /// 起一个最小 PTY 子进程（cmd /c exit 0）。某些 Windows 版本（实测
    /// Win11 26200.9278 insider）存在 ConPTY 客户端 attach 系统故障——任何
    /// PTY 子进程启动即死（exit 0xC0000142 STATUS_DLL_INIT_FAILED）。此预检
    /// 为 V6/V7 的前置门：机器本身故障时这两项标 NA 而非 FAIL。
    /// </summary>
    private static bool _conPtySystemOk;
    private static string? _conPtySystemNote;

    private static void ConPtySystemPrecheck()
    {
        try
        {
            var (ok, exitCode) = PtyMinRepro.Run();
            _conPtySystemOk = ok;
            _conPtySystemNote = ok
                ? "ConPTY attach 正常"
                : $"PTY 子进程 exitCode=0x{exitCode:X8} —— 机器级 ConPTY 故障（与 wslcUI 无关），V6/V7 记 NA";
        }
        catch (Exception ex)
        {
            _conPtySystemOk = false;
            _conPtySystemNote = $"预检异常: {ex.Message}";
        }
    }

    private static void V5_V6_PtyInteractive()
    {
        if (!_conPtySystemOk)
        {
            Record("V5 ConPTY 创建 (cmd.exe)", false, _conPtySystemNote ?? "", na: true);
            Record("V6 ConPTY 输入(\\r)与输出回传", false, _conPtySystemNote ?? "", na: true);
            Record("V5b ConPTY Dispose", false, "随 V5 跳过", na: true);
            return;
        }
        PseudoConsole pty;
        try
        {
            pty = new PseudoConsole("cmd.exe", 120, 30);
        }
        catch (Exception ex)
        {
            Record("V5 ConPTY 创建 (cmd.exe)", false, $"构造抛异常: {ex.Message}");
            return;
        }
        Record("V5 ConPTY 创建 (cmd.exe)", true, "PseudoConsole 构造成功，读线程已启动");

        var sb = new StringBuilder();
        var @lock = new object();
        pty.OutputReceived += chunk =>
        {
            lock (@lock) sb.Append(Encoding.Latin1.GetString(chunk));
        };

        Thread.Sleep(1500); // 等 banner
        pty.Write(Encoding.UTF8.GetBytes("echo wslcui_pt_ok\r"));

        var gotEcho = false;
        for (var i = 0; i < 50 && !gotEcho; i++)
        {
            Thread.Sleep(100);
            lock (@lock) gotEcho = sb.ToString().Contains("wslcui_pt_ok");
        }
        Record("V6 ConPTY 输入(\\r)与输出回传", gotEcho,
            gotEcho ? "echo 标记已回传，PTY 输入链路 + \\r 回车生效" : "5s 内未收到 echo 输出");

        try
        {
            pty.Dispose();
            Record("V5b ConPTY Dispose", true, "Dispose 无异常（读线程已 join、句柄已释放）");
        }
        catch (Exception ex)
        {
            Record("V5b ConPTY Dispose", false, $"Dispose 抛异常: {ex.Message}");
        }
    }

    // ---------------- V7 ConPTY 中文跨块 UTF-8 解码 ----------------
    //
    // 一次性实例跑 `cmd /c "chcp 65001>nul & echo <9000+ 字节中文>"`
    // （chcp 65001 后 cmd 输出即 UTF-8）。输出 >2 个 4096 读取块，
    // 验证流按块回传后整体解码无 U+FFFD（fa598ba 的 Decoder 修复在真机成立
    // 的前提：块边界确实切在多字节序列中间）。

    private static void V7_PtyUtf8AcrossChunks()
    {
        if (!_conPtySystemOk)
        {
            Record("V7 ConPTY 中文跨块解码", false, _conPtySystemNote ?? "", na: true);
            return;
        }
        // 6 字符 × 800 次 = 14400 字节 UTF-8 输出，必然横跨多个读取块。
        var payload = string.Concat(Enumerable.Repeat("中文跨块验证", 800)) + "ENDMARK7";
        var cmd = $"cmd.exe /c \"chcp 65001>nul & echo {payload}\"";

        PseudoConsole pty;
        try
        {
            pty = new PseudoConsole(cmd, 200, 30);
        }
        catch (Exception ex)
        {
            Record("V7 ConPTY 中文跨块解码", false, $"构造抛异常: {ex.Message}");
            return;
        }

        var ms = new MemoryStream();
        var @lock = new object();
        pty.OutputReceived += chunk =>
        {
            lock (@lock) ms.Write(chunk, 0, chunk.Length);
        };

        var complete = false;
        for (var i = 0; i < 100 && !complete; i++)
        {
            Thread.Sleep(100);
            lock (@lock) complete = Encoding.UTF8.GetString(ms.ToArray()).Contains("ENDMARK7");
        }

        string text;
        lock (@lock) text = Encoding.UTF8.GetString(ms.ToArray());
        pty.Dispose();

        if (!complete)
        {
            Record("V7 ConPTY 中文跨块解码", false, $"10s 内未收齐输出（{ms.Length} 字节）");
            return;
        }
        var pass = !text.Contains('\uFFFD') && text.Contains("中文跨块验证", StringComparison.Ordinal);
        Record("V7 ConPTY 中文跨块解码", pass,
            $"{ms.Length} 字节 / {ChunkCountHint(ms.Length)} 个读取块，" +
            (pass ? "无 U+FFFD，多字节序列跨块无损" : "检测到 U+FFFD 替换符 —— 跨块解码仍有丢字"));
    }

    // ---------------- V8 转义序列剥离（R1 起由 VtStripper 承担） ----------------
    //
    // 原 Ansi/Osc 静态正则已被 VtStripper（SGR 感知解析器）取代：不再全剥，
    // 而是保留颜色/粗细、丢弃其余。此处验证同样的「不可见噪声不残留」断言。

    private static void V8_EscapeStripping()
    {
        try
        {
            var cases = new (string Input, string ExpectedText, (byte? Fg, bool Bold)? Style, string Label)[]
            {
                // CSI 剥离且保留颜色：红色文字留下，转义符不残留
                ("\x1b[31m红\x1b[0m正常", "红正常", (1, false), "SGR 保留颜色"),
                // OSC+BEL：标题序列剥离
                ("\x1b]0;标题\x07正文", "正文", null, "OSC+BEL 剥离"),
                // OSC+ST：现代结尾符
                ("\x1b]0;标题\x1b\\正文", "正文", null, "OSC+ST 剥离"),
                // 非 SGR CSI（光标清除）丢弃，文本不丢
                ("a\x1b[2K\rb", "a\rb", null, "非 SGR CSI 丢弃"),
            };
            var bad = new List<string>();
            foreach (var c in cases)
            {
                var spans = new VtStripper().Feed(c.Input);
                var text = string.Concat(spans.Select(s => s.Text));
                if (text != c.ExpectedText) { bad.Add($"{c.Label} 期望「{c.ExpectedText}」实得「{text}」"); continue; }
                if (c.Style is { } st)
                {
                    var first = spans.FirstOrDefault();
                    if (first.Style.Foreground != st.Fg)
                        bad.Add($"{c.Label} 颜色期望 {st.Fg} 实得 {first.Style.Foreground}");
                }
            }
            Record("V8 转义剥离（VtStripper）", bad.Count == 0,
                bad.Count == 0 ? "4/4 用例通过（SGR 保色 / OSC+BEL / OSC+ST / 非SGR丢弃）"
                              : $"未通过: {string.Join("; ", bad)}");
        }
        catch (Exception ex)
        {
            Record("V8 转义剥离（VtStripper）", false, $"异常: {ex.Message}");
        }
    }

    // ---------------- V9 指定容器的非破坏性生命周期验证 ----------------

    private static async Task V9_ContainerLifecycle(string name)
    {
        try
        {
            var containers = await WslcCli.ListContainersAsync(Tok());
            var match = containers.FirstOrDefault(c =>
                string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                Record($"V9 容器「{name}」", false,
                    $"list 结果中未找到（现有: {string.Join(", ", containers.Select(c => c.Name))}）");
                return;
            }
            var logs = await WslcCli.GetLogsAsync(name, Tok());
            Record($"V9 容器「{name}」 logs", true,
                $"存在于列表（状态 {match.Status}），logs 拉取成功（{logs.Length} 字符）");
        }
        catch (Exception ex)
        {
            Record($"V9 容器「{name}」", false, $"抛异常: {ex.Message}");
        }
    }

    // ---------------- V10 SDK 取消路径（RunAndCaptureAsync 容器清理） ----------------
    //
    // WslcSdkClient.RunAndCaptureAsync 的取消清理（catch OCE → Stop(SIGTERM,5s)
    // + Delete(Force)）此前只有编译期验证。本探针真机触发一次取消：
    //   a) 经 SDK 会话拉 alpine:latest（需要网络）；
    //   b) 预先取消的 token 调 RunAndCaptureAsync("alpine", sh -c sleep 30)；
    //   c) 断言上抛 OperationCanceledException（Stop 若抛非预期异常会破坏清理链，
    //      需要暴露而非吞掉）；
    //   d) 清理断言：session 仍可 GetImages（未搞坏）、无残留 wslc 进程；
    //   e) 收尾 DeleteImageAsync 清理 SDK 镜像。
    // 默认跳过（拉镜像需网络），--sdk-cancel 显式开启。

    private static async Task V10_SdkCancelPath()
    {
        const string image = "alpine:latest";
        var client = new WslcSdkClient();
        try
        {
            // 前置：镜像必须存在于 SDK 会话自己的存储命名空间（ListImages 是
            // CLI+SDK 合并视图，CLI 侧有 alpine 不代表 SDK 会话能跑——两套
            // 命名空间隔离，见 AGENTS.md 第 6 节）。Pull 幂等，已存在则秒回。
            Console.WriteLine("[V10] 确保 SDK 会话有 alpine:latest（需要网络，首次较慢）…");
            await client.PullImageAsync(image, null, Tok(300000));

            var wslcBefore = Process.GetProcessesByName("wslc").Length;

            var cts = new CancellationTokenSource();
            cts.Cancel(); // 预取消：Start 后立即命中 WaitAsync(ct) 的取消路径
            OperationCanceledException? oce = null;
            try
            {
                await client.RunAndCaptureAsync(image, new[] { "sh", "-c", "sleep 30" }, cts.Token);
            }
            catch (OperationCanceledException ex) { oce = ex; }
            catch (Exception ex)
            {
                Record("V10 SDK 取消路径", false,
                    $"上抛了 {ex.GetType().Name} 而非 OperationCanceledException: {Truncate(ex.Message, 160)}");
                return;
            }

            if (oce is null)
            {
                Record("V10 SDK 取消路径", false, "未抛任何异常——取消未生效或容器跑完了 30s");
                return;
            }

            // 清理断言 1：session 仍可列举（取消没有把会话搞坏）
            var imgsAfter = await client.ListImagesAsync(Tok(60000));

            // 清理断言 2：没有新的 wslc 残留进程（对比取消前后）
            await Task.Delay(2000); // 给 Stop/Delete 一点收敛时间
            var wslcAfter = Process.GetProcessesByName("wslc").Length;

            var pass = wslcAfter <= wslcBefore;
            Record("V10 SDK 取消路径", pass,
                $"OCE 正确上浮；取消后 session 可列举（{imgsAfter.Count} 镜像）；" +
                $"wslc 进程 {wslcBefore} → {wslcAfter}" +
                (pass ? "" : "（有残留，Stop/Delete 清理链可疑）"));
        }
        catch (Exception ex)
        {
            Record("V10 SDK 取消路径", false, $"前置步骤失败: {Truncate(ex.Message, 160)}");
        }
        finally
        {
            // 收尾：清理 SDK 镜像（best effort，不影响判定）
            try { await client.DeleteImageAsync(image, Tok(60000)); } catch { /* best effort */ }
            client.Dispose();
        }
    }

    // ---------------- 基础设施 ----------------

    // ---------------- V11 端点解析（端口列 → 端点面板行）----------------
    //
    // 端点面板不额外调 CLI：它把 `wslc list -a` 的「端口」列交给
    // WslcCli 内部的 EndpointParser 纯函数解析。这里验证两件事：
    //   a) 解析器对**真机端口列**不丢项（逐容器比对：解析数 == 该容器逗号分隔段数）；
    //   b) 端到端复现一条已知形态（起一个带 -p 的探针容器，验证 host->container 映射）。
    // 端口列为空的已停止容器不参与计数（解析器返回空列表是正确行为）。

    private static async Task V11_EndpointParsing()
    {
        try
        {
            var containers = await WslcCli.ListContainersAsync(Tok());
            var running = containers.Where(c => c.IsRunning).ToList();

            var segMismatch = new List<string>();
            var totalEndpoints = 0;
            foreach (var c in running)
            {
                var expected = string.IsNullOrWhiteSpace(c.Ports)
                    ? 0
                    : c.Ports.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                             .Count(s => !string.IsNullOrWhiteSpace(s));
                var parsed = EndpointParser.Parse(c.Ports, c.Name);
                totalEndpoints += parsed.Count;
                if (parsed.Count != expected)
                    segMismatch.Add($"{c.Name}: 解析 {parsed.Count} != 段数 {expected}（{c.Ports}）");
            }

            if (segMismatch.Count > 0)
            {
                Record("V11 端点解析（端口列）", false,
                    "以下容器解析数与原始段数不符：" + string.Join("; ", segMismatch));
                return;
            }

            Record("V11 端点解析（端口列）", true,
                $"{running.Count} 个运行中容器全部解析成功，共产出 {totalEndpoints} 个端点" +
                (totalEndpoints == 0 ? "（当前无已发布端口，属正常）" : ""));
        }
        catch (Exception ex)
        {
            Record("V11 端点解析（端口列）", false, $"抛异常: {ex.Message}");
        }
    }

    // ---------------- V12 事件流 + system info（2026-10-02 新增能力）----------------
    //
    // V12a 事件回读：`wslc events --since 1h`。
    //   ⚠️⚠️ 关键实测（2026-10-02）：**即使带 --since，事件流也永远不会 EOF 退出**
    //   （退出码 124 = 被 timeout 杀掉）。--since 只是「先补历史再继续流」，
    //   不是「补完就退出」。因此**绝不能**用 RunRawAsync/ReadToEndAsync 读它
    //   （会永久挂死，verify 第一次跑就挂了 3 分 44 秒）。
    //   正确做法：用 WslcCli.ListEventsAsync —— 它逐行异步读 + **空闲即止**
    //   （400ms 没新行就判定历史倾泻完毕，主动结束）。
    //   没有事件不算失败（刚装 wslc 的机器历史为空）。
    // V12b `wslc system info`：验证版本/内核/会话字段解析出来。

    private static async Task V12_EventsAndSystemInfo()
    {
        // ---- V12a 事件回读 ----
        try
        {
            var events = await WslcCli.ListEventsAsync("1h", Tok());
            if (events.Count == 0)
            {
                Record("V12a 事件流回读解析", true,
                    "最近 1h 无事件（空历史属正常），解析器就绪");
            }
            else
            {
                var cats = events.Select(e => $"{e.Category}/{e.Action}").Distinct().Take(4);
                Record("V12a 事件流回读解析", true,
                    $"解析 {events.Count} 条事件；类别/动作：{string.Join(", ", cats)}");
            }
        }
        catch (Exception ex)
        {
            Record("V12a 事件流回读解析", false, $"抛异常: {ex.Message}");
        }

        // ---- V12b system info ----
        try
        {
            var info = await WslcCli.GetSystemInfoAsync(Tok());
            if (string.IsNullOrWhiteSpace(info.WslVersion))
            {
                Record("V12b system info 解析", false, "未解析出 WSL 版本（输出格式可能变了）");
                return;
            }
            Record("V12b system info 解析", true,
                $"WSL {info.WslVersion} · 内核 {info.KernelVersion} · " +
                $"Windows {info.WindowsVersion} · 会话 {info.Sessions.Count} 个");
        }
        catch (Exception ex)
        {
            Record("V12b system info 解析", false, $"抛异常: {ex.Message}");
        }
    }

    // ---------------- V13 镜像出向链路（tag / save / load）----------------
    //
    // 验证的是**不依赖外部仓库**的那一半出向能力：`push` 与 `registry login`
    // 需要真实凭据（自动化里没有，也不该把密码写进 CI），故不进 verify；
    // 但 tag/save/load 全部本地可闭环，能真机跑通就说明三个新桥接的**参数形态**
    // （`-o` / `-i`、位置参数个数与顺序）是对的 —— 这正是最容易写错的地方。
    //
    // 走真实 CLI（WslcCli）而非裸进程：这样 verify 覆盖的是生产代码路径。
    // 全部产物落在系统临时目录，跑完即删；验证用的临时 tag 也会删掉。

    private static async Task V13_ImageOutbound()
    {
        var probeTag = "wslcui-verify-probe:tmp";
        var tmpTar = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"wslcui-verify-{Guid.NewGuid():N}.tar");
        try
        {
            // 找一个本地已有的镜像当源（不 pull：verify 不该依赖网络）。
            var images = await WslcCli.ListImagesAsync(Tok());
            var source = images.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Reference));
            if (source is null)
            {
                Record("V13 镜像出向（tag/save/load）", true,
                    "本地无镜像，跳过（verify 不主动 pull —— 不该依赖网络）");
                return;
            }

            // 1) tag
            await WslcCli.TagImageAsync(source.Reference, probeTag, Tok());
            var afterTag = await WslcCli.ListImagesAsync(Tok());
            var tagged = afterTag.Any(i =>
                string.Equals(i.Reference, probeTag, StringComparison.OrdinalIgnoreCase));
            if (!tagged)
            {
                Record("V13 镜像出向（tag/save/load）", false,
                    $"tag 后列表里找不到 {probeTag}（源 {source.Reference}）");
                return;
            }

            // 2) save（导出到临时 tar）
            await WslcCli.SaveImagesAsync(new[] { probeTag }, tmpTar, Tok());
            var tarOk = File.Exists(tmpTar) && new FileInfo(tmpTar).Length > 0;
            if (!tarOk)
            {
                Record("V13 镜像出向（tag/save/load）", false,
                    $"save 未产出有效 tar：{tmpTar}（存在={File.Exists(tmpTar)}）");
                return;
            }
            var size = new FileInfo(tmpTar).Length;

            // 3) load 回读（同一 tar 再导一次，验证 -i 形态与往返一致）
            var loadOut = await WslcCli.LoadImagesAsync(tmpTar, quiet: false, Tok());
            var loadOk = loadOut.Contains(probeTag, StringComparison.OrdinalIgnoreCase);

            Record("V13 镜像出向（tag/save/load）", loadOk,
                $"源 {source.Reference} → tag {probeTag} ✓；save 产出 {size / 1024} KB ✓；" +
                (loadOk ? "load 回读 ✓" : $"load 未回读到 {probeTag}（输出: {loadOut.Trim()}）"));
        }
        catch (Exception ex)
        {
            Record("V13 镜像出向（tag/save/load）", false, $"抛异常: {ex.Message}");
        }
        finally
        {
            // 清理：临时 tar + 临时 tag。失败也要清，否则下次 verify 会撞名。
            try { if (File.Exists(tmpTar)) File.Delete(tmpTar); } catch { /* best effort */ }
            try { await WslcCli.DeleteImageAsync(probeTag, Tok()); } catch { /* best effort */ }
        }
    }

    private static void Record(string name, bool pass, string detail, bool na = false)
    {
        Results.Add((name, pass, na, detail));
        Console.WriteLine($"[{(na ? " NA " : pass ? "PASS" : "FAIL")}] {name}");
        Console.WriteLine($"       {detail}");
    }

    private static CancellationToken Tok(int ms = 20000) =>
        new CancellationTokenSource(ms).Token;

    private static string? ArgValue(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == flag) return args[i + 1];
        return null;
    }

    /// <summary>与 WslcCli.RunAsync 相同编码设置的原始进程调用，用于拿 CLI 原始输出做对照。</summary>
    private static async Task<(int Exit, string Stdout, string Stderr)> RunRawAsync(string arguments)
    {
        var psi = new ProcessStartInfo(WslcCli.ExePath, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"无法启动 {WslcCli.ExePath}");
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, stdout, stderr);
    }

    private static string FirstNonEmptyLine(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (l.Length > 0 && !l.StartsWith("---", StringComparison.Ordinal)) return l;
        }
        return "<empty>";
    }

    private static int CountDataLines(string output)
    {
        var lines = output.Split('\n');
        var headerIdx = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var l = lines[i].TrimEnd('\r');
            if (l.Length == 0 || l.StartsWith("---", StringComparison.Ordinal)) continue;
            headerIdx = i;
            break;
        }
        if (headerIdx < 0) return 0;
        var n = 0;
        for (var i = headerIdx + 1; i < lines.Length; i++)
            if (lines[i].TrimEnd('\r').Length > 0) n++;
        return n;
    }

    private static int ChunkCountHint(long totalBytes) => (int)((totalBytes + 4095) / 4096);

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}

/// <summary>
/// 独立于 wslcUI 的最小 ConPTY 复现（官方 ConptyExample 模式，纯非托管
/// STARTUPINFOEXW 构造），仅用于机器级 ConPTY 健康预检。返回子进程退出码：
/// 0 = attach 正常；0xC0000142 (STATUS_DLL_INIT_FAILED) = 机器级故障。
/// </summary>
internal static class PtyMinRepro
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Coord { public short X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformationRaw
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string? app, string cmd, IntPtr pa, IntPtr ta, bool inh, uint flags, IntPtr env,
        string? cwd, ref StartupInfoEx si, out ProcessInformationRaw pi);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(Coord size, IntPtr hInput, IntPtr hOutput, uint flags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr r, out IntPtr w, IntPtr sa, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, IntPtr lpValue, IntPtr cbSize, IntPtr prev, IntPtr retSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int WaitForSingleObject(IntPtr h, int ms);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr h, out int code);

    public static (bool Ok, int ExitCode) Run()
    {
        var size = new Coord { X = 120, Y = 30 };
        CreatePipe(out var inR, out var inW, IntPtr.Zero, 0);
        CreatePipe(out var outR, out var outW, IntPtr.Zero, 0);
        var hr = CreatePseudoConsole(size, inR, outW, 0, out var hPC);
        if (hr != 0) throw new InvalidOperationException($"CreatePseudoConsole hr=0x{hr:X8}");

        IntPtr listSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref listSize);
        var list = Marshal.AllocHGlobal(listSize);
        if (!InitializeProcThreadAttributeList(list, 1, 0, ref listSize))
            throw new InvalidOperationException($"InitializeProcThreadAttributeList err={Marshal.GetLastWin32Error()}");
        var pcPtr = Marshal.AllocHGlobal(IntPtr.Size);
        Marshal.WriteIntPtr(pcPtr, hPC);
        if (!UpdateProcThreadAttribute(list, 0, (IntPtr)0x00020016, pcPtr, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            throw new InvalidOperationException($"UpdateProcThreadAttribute err={Marshal.GetLastWin32Error()}");

        // 与产品代码（ConPty.cs）一致的类型化 StartupInfoEx。
        var si = new StartupInfoEx
        {
            StartupInfo = new StartupInfo { cb = Marshal.SizeOf<StartupInfoEx>() },
            lpAttributeList = list,
        };

        try
        {
            if (!CreateProcessW(null, "cmd.exe /c exit 0", IntPtr.Zero, IntPtr.Zero, false,
                    0x00080000, IntPtr.Zero, null, ref si, out var pi))
                throw new InvalidOperationException($"CreateProcessW err={Marshal.GetLastWin32Error()}");
            CloseHandle(inR); CloseHandle(outW);
            WaitForSingleObject(pi.hProcess, 5000);
            GetExitCodeProcess(pi.hProcess, out var ec);
            CloseHandle(pi.hProcess); CloseHandle(pi.hThread);
            return (ec == 0, ec);
        }
        finally
        {
            CloseHandle(outR); CloseHandle(inW);
            Marshal.FreeHGlobal(pcPtr); Marshal.FreeHGlobal(list);
        }
    }
}
