using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Text;
using Windows.System;
using wslcUI.Services;
using wslcUI.Terminal;

namespace wslcUI;

/// <summary>
/// Interactive container terminal（R3：XTerm.NET 集成）。
/// Launches `wslc exec -it &lt;name&gt; /bin/sh` inside a Windows Pseudoconsole
/// (see Services/ConPty.cs); the byte stream feeds an <see cref="XTerm.Terminal"/>,
/// rendered cell-by-cell by <see cref="TerminalView"/>.
/// 架构与选型见 docs/TERMINAL-RENDER-DECISION.md；R1 的 VtStripper+RichTextBlock
/// 路径已被此实现替代（保留在仓库作回退种子）。
/// </summary>
public sealed partial class TerminalWindow : Window
{
    private readonly PseudoConsole? _pty;
    private readonly string _container;
    private readonly XTerm.Terminal? _terminal;

    // 等宽字符单元尺寸（一次测量缓存），用于 窗口px → 终端cols/rows 换算。
    private double _cellWidth, _cellHeight;

    // 关窗标记：PTY 读线程是后台线程，窗口关闭后输出回调仍可能到达。
    private volatile bool _closed;

    public TerminalWindow(string container)
    {
        this.InitializeComponent();
        _container = container;
        Title = $"终端 — {container}";
        this.AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 560));
        this.Closed += TerminalWindow_Closed;

        try
        {
            var exe = WslcCli.ExePath;
            // 容器名加引号：名字含空格时不会被 CreateProcessW 的命令行解析拆开。
            var cmd = $"\"{exe}\" exec -it \"{container}\" /bin/sh";
            _pty = new PseudoConsole(cmd, 100, 30);

            _terminal = new XTerm.Terminal(new XTerm.Options.TerminalOptions
            {
                Cols = 100,
                Rows = 30,
                Scrollback = 1000,
            });
            View.Setup(_terminal);

            // PTY 输出 → Terminal（UI 线程：Terminal 非线程安全，渲染事件也在 UI 线程）
            _pty.OutputReceived += data =>
            {
                if (_closed) return;
                var terminal = _terminal;
                DispatcherQueue.TryEnqueue(() =>
                {
                    // 窗口关闭后仍可能有已入队的输出回调：Terminal 已 Dispose。
                    if (_closed || terminal is null) return;
                    try { terminal.Write(Encoding.UTF8.GetString(data)); }
                    catch (ObjectDisposedException) { /* 关窗竞态，忽略 */ }
                });
            };

            // 窗口尺寸 → Terminal.Resize → PTY.Resize（真终端跟随窗口）
            View.SizeChanged += OnViewSizeChanged;
        }
        catch (Exception ex)
        {
            _terminal = null;
            // 启动失败：直接在视图下方给一行提示（TerminalView 不可用时的降级）
            InputBox.PlaceholderText =
                $"无法启动终端: {ex.Message} — 请确认容器 {container} 正在运行 (wslc start {container})。";
        }
    }

    private void OnViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_terminal is null || _pty is null) return;
        if (!EnsureCellMetrics()) return;

        var cols = Math.Max(20, (int)(e.NewSize.Width / _cellWidth));
        var rows = Math.Max(5, (int)(e.NewSize.Height / _cellHeight));
        if (cols == _terminal.Cols && rows == _terminal.Rows) return;

        _terminal.Resize(cols, rows);
        _pty.Resize(cols, rows);
    }

    /// <summary>
    /// 测量等宽字符单元尺寸（一次，缓存）。用 10 个 'M' 的探针 TextBlock
    /// 离屏 Measure：比「首行 TextBlock 期望宽 / Cols」可靠——首行为空或
    /// 不满宽时旧法会得到偏小的 cell 宽，resize 换算系统性偏差。
    /// </summary>
    private bool EnsureCellMetrics()
    {
        if (_cellWidth > 0 && _cellHeight > 0) return true;

        var probe = new TextBlock
        {
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = TerminalView.FontSize,
            Text = new string('M', 10),
        };
        probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        if (probe.DesiredSize.Width <= 0 || probe.DesiredSize.Height <= 0) return false;

        _cellWidth = probe.DesiredSize.Width / 10;
        _cellHeight = probe.DesiredSize.Height;
        return true;
    }

    private void InputBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || _pty is null) return;
        var line = InputBox.Text;
        InputBox.Text = "";
        // PTY 行规范下按 Enter 应发 \r（icrnl 会转成 \n）。
        _pty.Write(Encoding.UTF8.GetBytes(line + "\r"));
    }

    private void TerminalWindow_Closed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _pty?.Dispose();
        (_terminal as IDisposable)?.Dispose();
    }
}
