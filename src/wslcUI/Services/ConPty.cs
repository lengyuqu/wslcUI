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
/// ⚠️ UNVERIFIED: written against the documented Win32 Pseudoconsole API
/// (learn.microsoft.com/windows/console/creating-a-pseudoconsole-session)
/// but NOT compiled/run on a real Windows+wslc machine in this session
/// (no .NET / WSL available here). On first real run, verify:
///   1. CreatePseudoConsole / CreateProcessW succeed (no ACCESS_VIOLATION).
///   2. Typed input reaches the shell and output streams back.
///   3. ResizePseudoConsole works when the window is resized.
/// </summary>
internal sealed class PseudoConsole : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Coord { public short X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(Coord size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, Coord size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, IntPtr lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref int lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPrevious, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeleteProcThreadAttributeList(IntPtr lpAttributeList);

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

    private const uint HANDLE_FLAG_INHERIT = 0x00000001;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = (IntPtr)0x00020016;

    private IntPtr _hPC = IntPtr.Zero;
    private IntPtr _hInRead, _hInWrite, _hOutRead, _hOutWrite;
    private IntPtr _attrList = IntPtr.Zero;
    private IntPtr _saPtr = IntPtr.Zero;
    private ProcessInformation _pi;
    private Thread? _reader;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    /// <summary>Fired with raw bytes streamed from the child's stdout.</summary>
    public event Action<byte[]>? OutputReceived;

    public PseudoConsole(string commandLine, int cols, int rows)
    {
        var size = new Coord { X = (short)cols, Y = (short)rows };

        // Security descriptor marking handles as inheritable.
        var sa = new SecurityAttributes { nLength = Marshal.SizeOf<SecurityAttributes>(), bInheritHandle = 1 };
        _saPtr = Marshal.AllocHGlobal(sa.nLength);
        Marshal.StructureToPtr(sa, _saPtr, false);

        if (!CreatePipe(out _hInRead, out _hInWrite, _saPtr, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (input) 失败");
        if (!CreatePipe(out _hOutRead, out _hOutWrite, _saPtr, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (output) 失败");

        // The master ends we keep must NOT be inherited by the child.
        SetHandleInformation(_hInWrite, HANDLE_FLAG_INHERIT, 0);
        SetHandleInformation(_hOutRead, HANDLE_FLAG_INHERIT, 0);

        var hr = CreatePseudoConsole(size, _hInRead, _hOutWrite, 0, out _hPC);
        if (hr != 0)
            Marshal.ThrowExceptionForHR(hr);

        // Attribute list carrying the pseudoconsole handle.
        int listSize = 0;
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
        };
        si.lpAttributeList = _attrList;

        if (!CreateProcessW(
                null, commandLine, IntPtr.Zero, IntPtr.Zero, true,
                EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                IntPtr.Zero, null, ref si, out _pi))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessW 失败");

        // The pseudoconsole duplicated the slave ends; we can close ours now.
        CloseHandle(_hInRead); _hInRead = IntPtr.Zero;
        CloseHandle(_hOutWrite); _hOutWrite = IntPtr.Zero;

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

        // Best-effort: kill the child so its pipes break and the reader exits.
        try { if (_pi.hProcess != IntPtr.Zero) CloseHandle(_pi.hProcess); } catch { }
        if (_pi.hThread != IntPtr.Zero) CloseHandle(_pi.hThread);

        if (_hPC != IntPtr.Zero) { ClosePseudoConsole(_hPC); _hPC = IntPtr.Zero; }
        if (_hInWrite != IntPtr.Zero) { CloseHandle(_hInWrite); _hInWrite = IntPtr.Zero; }
        if (_hOutRead != IntPtr.Zero) { CloseHandle(_hOutRead); _hOutRead = IntPtr.Zero; }
        if (_attrList != IntPtr.Zero) { DeleteProcThreadAttributeList(_attrList); Marshal.FreeHGlobal(_attrList); _attrList = IntPtr.Zero; }
        if (_saPtr != IntPtr.Zero) { Marshal.FreeHGlobal(_saPtr); _saPtr = IntPtr.Zero; }
        _cts.Dispose();
    }
}
