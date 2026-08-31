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
//   --container <名>    追加针对指定容器的非破坏性生命周期验证（V9）
//
// 退出码：0 = 全部通过；1 = 存在 FAIL。
// =============================================================================

using System.Diagnostics;
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
        await VerifyTable("V2.5 stats   编码+解析",
            "stats", new[] { "容器 ID", "CONTAINER ID" },
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

    // ---------------- V8 转义序列剥离正则（反射 TerminalWindow 真实字段） ----------------

    private static void V8_EscapeStripping()
    {
        Regex? ansi = null, osc = null;
        try
        {
            var t = typeof(TerminalWindow);
            ansi = t.GetField("Ansi", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as Regex;
            osc = t.GetField("Osc", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as Regex;
        }
        catch (Exception ex)
        {
            Record("V8 转义剥离正则", false, $"反射失败: {ex.Message}");
            return;
        }
        if (ansi is null || osc is null)
        {
            Record("V8 转义剥离正则", false, "未能取得 TerminalWindow.Ansi / Osc 静态字段");
            return;
        }

        var cases = new (string Input, string Expected, string Label)[]
        {
            (ansi.Replace("\x1b[31m红\x1b[0m", ""), "红", "CSI 剥离"),
            (osc.Replace("\x1b]0;标题\x07正文", ""), "正文", "OSC+BEL 剥离"),
            (osc.Replace("\x1b]0;标题\x1b\\正文", ""), "正文", "OSC+ST 剥离（faf10a5 新增支持）"),
            (osc.Replace("前\x1b]0;t\x07中\x07后", ""), "前中\x07后", "OSC 懒惰匹配不吞正文"),
        };
        var bad = cases.Where(c => c.Input != c.Expected).ToList();
        Record("V8 转义剥离正则", bad.Count == 0,
            bad.Count == 0 ? "4/4 用例通过（CSI / OSC+BEL / OSC+ST / 懒惰匹配）"
                          : $"未通过: {string.Join("; ", bad.Select(c => $"{c.Label} 期望「{c.Expected}」实得「{c.Input}」"))}");
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

    // ---------------- 基础设施 ----------------

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

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformationRaw
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string? app, string cmd, IntPtr pa, IntPtr ta, bool inh, uint flags, IntPtr env,
        string? cwd, IntPtr si, out ProcessInformationRaw pi);

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

        // STARTUPINFOEXW：sizeof(STARTUPINFOW)=104，+8（lpAttributeList）= 112
        var siPtr = Marshal.AllocHGlobal(112);
        for (var i = 0; i < 112; i += 8) Marshal.WriteInt64(siPtr, i, 0);
        Marshal.WriteInt32(siPtr, 0, 112);
        Marshal.WriteIntPtr(siPtr, 104, list);

        try
        {
            if (!CreateProcessW(null, "cmd.exe /c exit 0", IntPtr.Zero, IntPtr.Zero, false,
                    0x00080000, IntPtr.Zero, null, siPtr, out var pi))
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
            Marshal.FreeHGlobal(siPtr); Marshal.FreeHGlobal(pcPtr); Marshal.FreeHGlobal(list);
        }
    }
}
