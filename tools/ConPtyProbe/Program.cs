// =============================================================================
// ConPTY 独立探针：判定「机器级 ConPTY 故障」是否只在非交互会话出现。
//
// 背景：agent shell（bash/PowerShell 均在 agent 进程树内）跑 wslcUI.Verify
// 预检恒报 PTY 子进程 0xC0000142。本探针经 explorer.exe 委托在**真实交互
// 会话**启动，结果写入固定文件（stdout 不可用），区分「机器故障」vs「会话
// 环境差异」。与 wslcUI 的 PseudoConsole.ProbeHealth 同款最小复现。
//
// 启动方式（agent 侧）：explorer.exe <本 exe 绝对路径>
// 结果：读 %TEMP%\wslcui-conpty-probe.txt
// =============================================================================

using System.Runtime.InteropServices;

var resultPath = Path.Combine(Path.GetTempPath(), "wslcui-conpty-probe.txt");
try { File.Delete(resultPath); } catch { /* 首次运行不存在 */ }

var sb = new System.Text.StringBuilder();
sb.AppendLine($"time={DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
sb.AppendLine($"session={System.Diagnostics.Process.GetCurrentProcess().SessionId}");

// ① 非PTY普通子进程基线
try
{
    var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c exit 0")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    var p = System.Diagnostics.Process.Start(psi);
    p!.WaitForExit(5000);
    sb.AppendLine($"plainChild(exit0)={p.ExitCode}"); // 应 = 0
}
catch (Exception ex)
{
    sb.AppendLine($"plainChild threw {ex.GetType().Name}: {ex.Message}");
}

// ② PTY 子进程（完整 ProbeHealth 路径：属性列表 attach + Native.CreateProcessW）
try
{
    var (exitCode, detail) = Native.PtyChildProbe();
    sb.AppendLine($"ptyChild={exitCode} {detail}"); // 健康 = 0；0xC0000142 = 附着即死
}
catch (Exception ex)
{
    sb.AppendLine($"ptyChild threw {ex.GetType().Name}: {ex.Message}");
}

File.WriteAllText(resultPath, sb.ToString());
return 0;

// ---- 与 wslcUI PseudoConsole 同款的 ConPTY P/Invoke ----
static class Native
{


[StructLayout(LayoutKind.Sequential)]
struct COORD { public short X, Y; }

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct STARTUPINFO
{
    public int cb;
    public string? Reserved, Desktop, Title;
    public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute;
    public int Flags;
    public short ShowWindow, Reserved2;
    public IntPtr Reserved3, StdInput, StdOutput, StdError;
}

[StructLayout(LayoutKind.Sequential)]
struct STARTUPINFOEX
{
    public STARTUPINFO StartupInfo;
    public IntPtr AttributeList;
}

[DllImport("kernel32.dll", SetLastError = true)]
static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool CreatePipe(out IntPtr r, out IntPtr w, IntPtr attr, uint size);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, IntPtr value, IntPtr size, IntPtr prev, IntPtr ret);

[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
static extern bool CreateProcessW(string? app, string cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string? dir, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);

[DllImport("kernel32.dll", SetLastError = true)]
static extern void CloseHandle(IntPtr h);

[DllImport("kernel32.dll", SetLastError = true)]
static extern void ClosePseudoConsole(IntPtr hPC);

[DllImport("kernel32.dll", SetLastError = true)]
static extern uint WaitForSingleObject(IntPtr h, int ms);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool GetExitCodeProcess(IntPtr h, out int code);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool ReadFile(IntPtr h, byte[] buf, int n, out int read, IntPtr overlapped);

[DllImport("kernel32.dll", SetLastError = true)]
static extern void DeleteProcThreadAttributeList(IntPtr list);

[StructLayout(LayoutKind.Sequential)]
struct PROCESS_INFORMATION
{
    public IntPtr Process, Thread;
    public int Pid, Tid;
}

internal static (int, string) PtyChildProbe()
{
    CreatePipe(out var inR, out var inW, IntPtr.Zero, 0);
    CreatePipe(out var outR, out var outW, IntPtr.Zero, 0);
    var hr = CreatePseudoConsole(new COORD { X = 80, Y = 25 }, inR, outW, 0, out var hPC);
    if (hr != 0) return (hr, $"createHR=0x{hr:X8}");

    IntPtr list = IntPtr.Zero, pcPtr = IntPtr.Zero;
    try
    {
        IntPtr size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        list = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(list, 1, 0, ref size))
            return (-2, $"initAttrErr={Marshal.GetLastWin32Error()}");
        pcPtr = Marshal.AllocHGlobal(IntPtr.Size);
        Marshal.WriteIntPtr(pcPtr, hPC);
        if (!UpdateProcThreadAttribute(list, 0, (IntPtr)0x00020016, pcPtr, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            return (-3, $"updAttrErr={Marshal.GetLastWin32Error()}");

        var si = new STARTUPINFOEX
        {
            StartupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFOEX>() },
            AttributeList = list,
        };
        if (!CreateProcessW(null, "cmd.exe /c echo hello", IntPtr.Zero, IntPtr.Zero, false,
                0x00080000 /*EXTENDED_STARTUPINFO_PRESENT*/, IntPtr.Zero, null, ref si, out var pi))
            return (-4, $"createProcErr={Marshal.GetLastWin32Error()}");

        CloseHandle(inR);
        CloseHandle(outW);
        // 并发读输出 + 长等待：区分「子进程挂起」vs「输出管线堵死」vs「正常退出」
        var outBuf = new System.Collections.Concurrent.ConcurrentQueue<byte>();
        var reader = new Thread(() =>
        {
            var buf = new byte[4096];
            while (ReadFile(outR, buf, 4096, out var n, IntPtr.Zero) && n > 0)
                for (var i = 0; i < n; i++) outBuf.Enqueue(buf[i]);
        }) { IsBackground = true };
        reader.Start();
        var wait = WaitForSingleObject(pi.Process, 20000);
        GetExitCodeProcess(pi.Process, out var code);
        // 收尾顺序关键：先 ClosePseudoConsole（断开输出管 → ReadFile 立即返回），
        // join 读线程之后才能关 outR——关闭正被同步 ReadFile 使用的句柄是
        // 未定义行为（上一版探针进程就此崩溃，结果没落盘）。
        if (hPC != IntPtr.Zero) { ClosePseudoConsole(hPC); hPC = IntPtr.Zero; }
        reader.Join(3000);
        Thread.Sleep(200);
        var got = outBuf.ToArray();
        var preview = System.Text.Encoding.UTF8.GetString(got, 0, Math.Min(160, got.Length))
            .Replace("\x1b", "ESC").Replace("\r", "\\r").Replace("\n", "\\n");
        CloseHandle(pi.Process); CloseHandle(pi.Thread);
        return (code, $"wait={wait} bytes={got.Length} out=[{preview}]");
    }
    finally
    {
        CloseHandle(inR); CloseHandle(inW); CloseHandle(outR); CloseHandle(outW);
        if (hPC != IntPtr.Zero) ClosePseudoConsole(hPC);
        if (list != IntPtr.Zero) { DeleteProcThreadAttributeList(list); Marshal.FreeHGlobal(list); }
        if (pcPtr != IntPtr.Zero) Marshal.FreeHGlobal(pcPtr);
    }
}
}
