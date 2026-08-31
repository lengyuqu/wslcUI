using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Text;
using System.Text.RegularExpressions;
using Windows.System;
using wslcUI.Services;
using wslcUI.Terminal;

namespace wslcUI;

/// <summary>
/// Interactive container terminal. Launches `wslc exec -it &lt;name&gt; /bin/sh`
/// inside a Windows Pseudoconsole (see Services/ConPty.cs) so the shell gets
/// a real TTY. Double-click / the 终端 button in MainWindow opens this.
/// </summary>
public sealed partial class TerminalWindow : Window
{
    private readonly PseudoConsole? _pty;
    private readonly string _container;

    // Strip CSI (SGR/cursor) and OSC sequences so the read-only TextBlock
    // stays readable. Full VT rendering (XTermSharp / TermControl) is a future
    // enhancement; this MVP shows the text without escape noise.
    // NOTE: regular (non-verbatim) strings so \x1b is the ESC char and
    // regex backslashes are escaped as \\[ / \\].
    // OSC 结尾同时接受 BEL(\x07) 与 ST(ESC \)：现代程序普遍用 ST，只认 BEL
    // 会漏剥；且旧的 [^\x07]* 贪婪到下一个 BEL 会把中间正文整段吞掉。
    private static readonly Regex Ansi = new("\\x1b\\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly Regex Osc = new("\\x1b\\].*?(\\x07|\\x1b\\\\)", RegexOptions.Compiled);

    public TerminalWindow(string container)
    {
        this.InitializeComponent();
        _container = container;
        Title = $"终端 — {container}";
        // Set window size (WinUI 3 Window has no Height/Width XAML attributes)
        this.AppWindow.Resize(new Windows.Graphics.SizeInt32(820, 520));
        this.Closed += TerminalWindow_Closed;
        try
        {
            var exe = WslcCli.ExePath;
            // 容器名加引号：名字含空格时不会被 CreateProcessW 的命令行解析拆开。
            var cmd = $"\"{exe}\" exec -it \"{container}\" /bin/sh";
            _pty = new PseudoConsole(cmd, 100, 30);
            _pty.OutputReceived += OnOutput;
        }
        catch (Exception ex)
        {
            OutputText.Text =
                $"无法启动终端: {ex.Message}\n\n请确认容器 {container} 正在运行 (wslc start {container})。";
        }
    }

    // 跨块有状态的 UTF-8 解码器：中文等多字节字符横跨 4KB 读取块边界时，
    // 逐块独立 GetString 会产生 U+FFFD 丢字。GetDecoder 缓存未完成的字节序列。
    private readonly System.Text.Decoder _utf8Decoder = Encoding.UTF8.GetDecoder();

    // 终端输出上限（字符）：Text += 是 O(n) 拷贝且 TextBlock 渲染无界增长，
    // 长会话（cat 大文件）会拖垮 UI 线程和内存。超限丢弃头部。
    private const int MaxOutputChars = 512 * 1024;

    private void OnOutput(byte[] data)
    {
        var chars = new char[_utf8Decoder.GetCharCount(data, 0, data.Length, flush: false)];
        _utf8Decoder.GetChars(data, 0, data.Length, chars, 0, flush: false);
        var text = Ansi.Replace(new string(chars), "");
        text = Osc.Replace(text, "");
        DispatcherQueue.TryEnqueue(() =>
        {
            if (OutputText.Text.Length + text.Length > MaxOutputChars)
            {
                var keep = Math.Max(0, MaxOutputChars - text.Length);
                OutputText.Text = OutputText.Text[^keep..] + text;
            }
            else
            {
                OutputText.Text += text;
            }
            OutputScroller.ChangeView(null, OutputScroller.ScrollableHeight, 1f);
        });
    }

    private void InputBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        var line = InputBox.Text;
        InputBox.Text = "";
        // PTY 行规范下按 Enter 应发 \r（icrnl 会转成 \n）；直接发 \n
        // 依赖终端默认配置，个别 shell 下不触发执行。
        _pty?.Write(Encoding.UTF8.GetBytes(line + "\r"));
    }

    private void TerminalWindow_Closed(object sender, WindowEventArgs args)
    {
        _pty?.Dispose();
    }
}
