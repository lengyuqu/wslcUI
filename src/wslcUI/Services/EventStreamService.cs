using System;
using System.Diagnostics;
using System.Threading;
using wslcUI.Models;

namespace wslcUI.Services;

/// <summary>
/// <c>wslc events</c> 长驻事件流服务。
///
/// <para>
/// <b>为什么必须用异步逐行读</b>：<c>wslc events</c> 是**永不结束**的长驻进程
/// （实测 2026-10-02：即使带 <c>--since</c> / <c>--until</c>，回放完历史后它会
/// **继续流式输出、绝不自行退出**）。因此：
/// </para>
/// <list type="number">
///   <item><b>绝不能 <c>ReadToEndAsync</c></b> —— 事件流永不 EOF，那会永久挂死
///         （且 <c>WaitForExit</c> 永远等不到）。</item>
///   <item>必须 <c>OutputDataReceived</c> + <c>BeginOutputReadLine()</c> 逐行读
///         （写法照抄 <c>WslcCli.BuildImageAsync</c>）。</item>
///   <item><b>stderr 也必须并发排空</b>（<c>BeginErrorReadLine</c>）—— 这是本实现
///         最容易漏掉的一处：只读 stdout 而不管 stderr，wslc 报错时写满 stderr 的
///         4 KB 管道缓冲就会永久阻塞在写侧，UI 表现为「事件流突然不再更新」，
///         而且没有任何报错。<c>WslcCli.RunAsync</c> 用的是「两个 ReadToEndAsync
///         先启动再 WhenAll」，对一次性命令足够，但长驻流只能靠异步事件排空。</item>
/// </list>
///
/// <para>
/// <b>停止</b>：<see cref="Stop"/> 先摘掉事件处理器（防止 kill 之后仍有回调打到
/// 已释放的对象），再 <c>Kill(entireProcessTree: true)</c> 杀整个进程树并回收，
/// 避免留下孤儿 <c>wslc</c> 进程。可重复调用（幂等）。
/// </para>
///
/// <para>
/// <b>为什么不进 <see cref="IWslcClient"/></b>：长驻资源不适合做成接口方法 ——
/// 接口方法应是「调一次、拿到结果、返回」。流服务的生命周期（Start/Stop/IsRunning）
/// 与 ViewModel 的页面进出耦合，留在具体类里由 ViewModel 直接持有。
/// 一次性历史回读走 <c>IWslcClient.ListEventsAsync</c>。
/// </para>
/// </summary>
internal sealed class EventStreamService : IDisposable
{
    private readonly object _gate = new();
    private Process? _proc;
    private CancellationTokenRegistration? _ctReg;
    private bool _disposed;

    /// <summary>流是否正在运行（子进程活着且未 disposed）。</summary>
    public bool IsRunning
    {
        get { lock (_gate) return _proc is { HasExited: false }; }
    }

    /// <summary>每收到一条可解析事件时触发（在**后台线程**，订阅方需自行切 UI 线程）。</summary>
    public event EventHandler<ContainerEvent>? EventReceived;

    /// <summary>子进程写到 stderr 的内容（非空才触发），用于把 wslc 的错误透给 UI。</summary>
    public event EventHandler<string>? ErrorReceived;

    /// <summary>子进程意外退出时触发（参数为退出码）。用户主动 <see cref="Stop"/> 不会触发。</summary>
    public event EventHandler<int>? Exited;

    /// <summary>
    /// 启动事件流（长驻）。
    /// </summary>
    /// <param name="since">
    /// 历史回读窗口（如 <c>5m</c> / <c>2h</c>）。<c>null</c> = 只收新事件。
    /// 回读的历史与后续实时事件走同一条回调，UI 无需区分。
    /// </param>
    /// <param name="ct">取消令牌：取消等价于 <see cref="Stop"/>（杀进程），不抛异常。</param>
    /// <exception cref="InvalidOperationException">已在运行。</exception>
    /// <exception cref="FileNotFoundException">找不到 wslc.exe（与其它 CLI 桥接一致的引导文案）。</exception>
    public void Start(string? since = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_proc is { HasExited: false })
                throw new InvalidOperationException("事件流已在运行中，请先 Stop()。");

            WslcCli.EnsureExe();

            var psi = new ProcessStartInfo
            {
                FileName = WslcCli.ExePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                // 必须显式 UTF-8：GUI 进程没有控制台，.NET 默认按系统 ANSI 代码页
                // 解码，含中文的 value（容器名）会整体乱码。
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            psi.ArgumentList.Add("events");
            if (!string.IsNullOrWhiteSpace(since))
            {
                // 空串/纯空白视为「只要实时」，不传 --since（传空值 wslc 会报参数错误）。
                psi.ArgumentList.Add("--since");
                psi.ArgumentList.Add(since);
            }

            var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("无法启动 wslc 进程。");

            proc.OutputDataReceived += OnOutput;
            proc.ErrorDataReceived += OnError;
            proc.BeginOutputReadLine();
            // ↓ 这一行不能省：见类注释「stderr 也必须并发排空」。
            proc.BeginErrorReadLine();
            proc.EnableRaisingEvents = true;
            proc.Exited += OnExited;

            _proc = proc;
            // 取消即停：与 WslcCli.RunAsync 的「取消即杀进程」安全网同构。
            _ctReg = ct.CanBeCanceled
                ? ct.Register(static state => ((EventStreamService)state!).Stop(), this)
                : null;
        }
    }

    /// <summary>
    /// 停止事件流并回收子进程。<b>幂等</b>：未运行时直接返回，可安全在
    /// <c>Stop</c>/<c>Dispose</c>/页面切换多处重复调用。
    /// </summary>
    public void Stop()
    {
        Process? proc;
        CancellationTokenRegistration? reg;
        lock (_gate)
        {
            proc = _proc;
            reg = _ctReg;
            _proc = null;
            _ctReg = null;
        }
        if (reg is { } r)
        {
            // 若当前正处在该令牌的回调里（即取消触发 Stop），Dispose 会死等自己。
            r.Dispose();
        }
        if (proc is null) return;

        try
        {
            // 先摘处理器：kill 会让进程退出并触发 OnExited/OnOutput，
            // 不摘的话回调会打到已释放的状态上。
            proc.OutputDataReceived -= OnOutput;
            proc.ErrorDataReceived -= OnError;
            proc.Exited -= OnExited;

            if (!proc.HasExited)
            {
                // entireProcessTree：wslc 可能派生子进程，只杀父进程会留孤儿。
                proc.Kill(entireProcessTree: true);
                // 有界等待回收；超时也不阻塞调用方（Dispose 里已经 best-effort）。
                proc.WaitForExit(2000);
            }
        }
        catch (InvalidOperationException)
        {
            // 进程已退出/已释放：无需处理。
        }
        finally
        {
            proc.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop();
        EventReceived = null;
        ErrorReceived = null;
        Exited = null;
    }

    // ---- 进程事件回调（均在后台线程）----

    private void OnOutput(object? sender, DataReceivedEventArgs e)
    {
        // e.Data == null 表示 stdout 流结束（EOF）。
        if (e.Data is null) return;
        // 解析失败返回 null（脏行不炸流），直接丢弃。
        var ev = EventLineParser.Parse(e.Data);
        if (ev is null) return;
        try
        {
            EventReceived?.Invoke(this, ev);
        }
        catch (Exception ex)
        {
            // 订阅方（ViewModel）抛异常绝不能反噬到流的读循环里。
            Debug.WriteLine($"[wslcUI] 事件订阅方处理失败: {ex}");
        }
    }

    private void OnError(object? sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data)) return;
        Debug.WriteLine($"[wslcUI] wslc events stderr: {e.Data}");
        try
        {
            ErrorReceived?.Invoke(this, e.Data);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[wslcUI] 错误订阅方处理失败: {ex}");
        }
    }

    private void OnExited(object? sender, EventArgs e)
    {
        var proc = sender as Process;
        int code;
        try { code = proc?.ExitCode ?? -1; }
        catch (InvalidOperationException) { code = -1; }   // 进程已释放

        bool userInitiated;
        lock (_gate)
        {
            // Stop() 已把 _proc 置 null → 说明是用户主动停的，不该报「意外退出」。
            userInitiated = _proc is null;
            _proc = null;
        }
        if (userInitiated || _disposed) return;

        try
        {
            Exited?.Invoke(this, code);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[wslcUI] 退出订阅方处理失败: {ex}");
        }
    }
}
