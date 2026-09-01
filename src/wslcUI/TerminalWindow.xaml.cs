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
                DispatcherQueue.TryEnqueue(() => _terminal.Write(Encoding.UTF8.GetString(data)));

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
        if (_cellWidth <= 0 || _cellHeight <= 0)
        {
            // 首次布局后测量单元尺寸（用 View 首行 TextBlock 的期望尺寸近似）
            if (View.Children.Count > 0 && View.Children[0] is TextBlock first)
            {
                _cellWidth = first.DesiredSize.Width / Math.Max(1, _terminal.Cols);
                _cellHeight = first.DesiredSize.Height;
            }
            if (_cellWidth <= 0 || _cellHeight <= 0) return;
        }

        var cols = Math.Max(20, (int)(e.NewSize.Width / _cellWidth));
        var rows = Math.Max(5, (int)(e.NewSize.Height / _cellHeight));
        if (cols == _terminal.Cols && rows == _terminal.Rows) return;

        _terminal.Resize(cols, rows);
        _pty.Resize(cols, rows);
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
        _pty?.Dispose();
        (_terminal as IDisposable)?.Dispose();
    }
}
