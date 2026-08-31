using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace wslcUI.Terminal;

/// <summary>
/// Minimal Windows Pseudoconsole (ConPTY) host, P/Invoke over kernel32.dll.
/// Zero external NuGet dependency. Gives the container shell a REAL TTY so
/// line editing, colours and full-screen apps behave correctly inside the
/// interactive terminal (TerminalWindow).
///
/// Written against the documented Win32 Pseudoconsole API and the canonical
/// C# sample in microsoft/terminal (samples/ConptyExample):
///   learn.microsoft.com/windows/console/creating-a-pseudoconsole-session
/// NOT compiled/run on a real Windows+wslc machine in this session
/// (no .NET / WSL available here). On first real run, verify:
///   1. CreatePseudoConsole / CreateProcessW succeed (no ACCESS_VIOLATION).
///   2. Typed input reaches the shell and output streams back.
///   3. ResizePseudoConsole works when the window is resized.
/// </summary>
internal sealed class PseudoConsole : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Coord { public short X, Y; }

    // Must match STARTUPINFOW EXACTLY — incl. the 3 "CountChars"/"FillAttribute"
    // DWORDs that sit between dwYSize and dwFlags. Missing them shifts every
    // later field (dwFlags, hStd*) to the wrong offset and corrupts CreateProcess.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize;
        public int dwXCountChars, dwYCountChars, dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
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
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(Coord size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ResizePseudoConsole(IntPtr hPC, Coord size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, IntPtr lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPrevious, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        [MarshalAs(UnmanagedType.LPWStr)] string? lpApplicationName,
        [MarshalAs(UnmanagedType.LPWStr)] string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        [MarshalAs(UnmanagedType.LPWStr)] string? lpCurrentDirectory,
        ref StartupInfoEx lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, int nNumberOfBytesToRead, out int lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, int nNumberOfBytesToWrite, out int lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = (IntPtr)0x00020016;

    private IntPtr _hPC = IntPtr.Zero;
    private IntPtr _hInRead, _hInWrite, _hOutRead, _hOutWrite;
    private IntPtr _attrList = IntPtr.Zero;
    private ProcessInformation _pi;
    private Thread? _reader;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    /// <summary>Fired with raw bytes streamed from the child's stdout.</summary>
    public event Action<byte[]>? OutputReceived;

    public PseudoConsole(string commandLine, int cols, int rows)
    {
        var size = new Coord { X = (short)cols, Y = (short)rows };

        try
        {
            // Pipes are intentionally NON-inheritable; the pseudoconsole attaches the
            // client via the PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE attribute list, NOT
            // via raw handle inheritance (matches microsoft/terminal sample).
            if (!CreatePipe(out _hInRead, out _hInWrite, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (input) 失败");
            if (!CreatePipe(out _hOutRead, out _hOutWrite, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (output) 失败");

            // hInput = read end of the input pipe; hOutput = write end of the output pipe.
            var hr = CreatePseudoConsole(size, _hInRead, _hOutWrite, 0, out _hPC);
            if (hr != 0)
                Marshal.ThrowExceptionForHR(hr);

            // Attribute list carrying the pseudoconsole handle.
            // lpSize is PSIZE_T → pointer-sized, so use IntPtr (not int) on 64-bit.
            IntPtr listSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref listSize); // first call -> size
            _attrList = Marshal.AllocHGlobal(listSize);
            if (!InitializeProcThreadAttributeList(_attrList, 1, 0, ref listSize))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList 失败");
            UpdateProcThreadAttribute(
                _attrList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                _hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero);

            var si = new StartupInfoEx
            {
                StartupInfo = new StartupInfo { cb = Marshal.SizeOf<StartupInfoEx>() },
                lpAttributeList = _attrList,
            };

            if (!CreateProcessW(
                    null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    EXTENDED_STARTUPINFO_PRESENT,
                    IntPtr.Zero, null, ref si, out _pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessW 失败");

            // The pseudoconsole duplicated the slave ends; we can close ours now.
            CloseHandle(_hInRead); _hInRead = IntPtr.Zero;
            CloseHandle(_hOutWrite); _hOutWrite = IntPtr.Zero;
        }
        catch
        {
            // 构造失败（如 wslc.exe 不存在）也要释放已创建的内核对象：
            // 构造函数抛异常不会触发调用方的 Dispose，句柄会一直泄漏到进程退出。
            ReleaseHandles();
            throw;
        }

        _reader = new Thread(ReaderLoop) { IsBackground = true };
        _reader.Start();
    }

    private void ReaderLoop()
    {
        const int BUF = 4096;
        var buf = new byte[BUF];
        while (!_cts.IsCancellationRequested)
        {
            if (!ReadFile(_hOutRead, buf, BUF, out int read, IntPtr.Zero))
                break;
            if (read == 0) break;
            var chunk = new byte[read];
            Array.Copy(buf, chunk, read);
            OutputReceived?.Invoke(chunk);
        }
    }

    /// <summary>Send raw bytes (e.g. a typed line + newline) to the child's stdin.</summary>
    public void Write(byte[] data)
    {
        if (_hInWrite == IntPtr.Zero) return;
        WriteFile(_hInWrite, data, data.Length, out _, IntPtr.Zero);
    }

    public void Resize(int cols, int rows)
    {
        if (_hPC == IntPtr.Zero) return;
        ResizePseudoConsole(_hPC, new Coord { X = (short)cols, Y = (short)rows });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();

        // Closing the pseudoconsole terminates the attached client(s) and breaks
        // the output pipe — that's what unblocks the reader thread's ReadFile.
        if (_hPC != IntPtr.Zero) { ClosePseudoConsole(_hPC); _hPC = IntPtr.Zero; }
        if (_hInWrite != IntPtr.Zero) { CloseHandle(_hInWrite); _hInWrite = IntPtr.Zero; }
        // 先等读线程退出 ReadFile，再关 _hOutRead：关闭一个正被同步 ReadFile
        // 使用的句柄是未定义行为（可能读到复用句柄的数据甚至 AV）。
        _reader?.Join(2000);
        ReleaseHandles();
        _cts.Dispose();
    }

    /// <summary>释放全部内核对象。Dispose（读线程已 join）与构造失败路径共用。</summary>
    private void ReleaseHandles()
    {
        if (_hPC != IntPtr.Zero) { ClosePseudoConsole(_hPC); _hPC = IntPtr.Zero; }
        if (_hInRead != IntPtr.Zero) { CloseHandle(_hInRead); _hInRead = IntPtr.Zero; }
        if (_hInWrite != IntPtr.Zero) { CloseHandle(_hInWrite); _hInWrite = IntPtr.Zero; }
        if (_hOutRead != IntPtr.Zero) { CloseHandle(_hOutRead); _hOutRead = IntPtr.Zero; }
        if (_hOutWrite != IntPtr.Zero) { CloseHandle(_hOutWrite); _hOutWrite = IntPtr.Zero; }
        if (_pi.hProcess != IntPtr.Zero) { CloseHandle(_pi.hProcess); _pi.hProcess = IntPtr.Zero; }
        if (_pi.hThread != IntPtr.Zero) { CloseHandle(_pi.hThread); _pi.hThread = IntPtr.Zero; }
        if (_attrList != IntPtr.Zero) { DeleteProcThreadAttributeList(_attrList); Marshal.FreeHGlobal(_attrList); _attrList = IntPtr.Zero; }
    }
}
