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
    private static readonly Regex Ansi = new("\\x1b\\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly Regex Osc = new("\\x1b\\][^\\x07]*\\x07", RegexOptions.Compiled);

    public TerminalWindow(string container)
    {
        this.InitializeComponent();
        _container = container;
        Title = $"终端 — {container}";
        try
        {
            var exe = WslcCli.ExePath;
            var cmd = $"\"{exe}\" exec -it {container} /bin/sh";
            _pty = new PseudoConsole(cmd, 100, 30);
            _pty.OutputReceived += OnOutput;
        }
        catch (Exception ex)
        {
            OutputText.Text =
                $"无法启动终端: {ex.Message}\n\n请确认容器 {container} 正在运行 (wslc start {container})。";
        }
    }

    private void OnOutput(byte[] data)
    {
        var text = Ansi.Replace(Encoding.UTF8.GetString(data), "");
        text = Osc.Replace(text, "");
        DispatcherQueue.TryEnqueue(() =>
        {
            OutputText.Text += text;
            OutputScroller.ChangeView(null, OutputScroller.ScrollableHeight, false);
        });
    }

    private void InputBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        var line = InputBox.Text;
        InputBox.Text = "";
        _pty?.Write(Encoding.UTF8.GetBytes(line + "\n"));
    }

    protected override void OnClosed(EventArgs e)
    {
        _pty?.Dispose();
        base.OnClosed(e);
    }
}
