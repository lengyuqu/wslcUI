using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Text;
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

    // R1：SGR 颜色子集渲染。VtStripper 解析 CSI/OSC，保留 SGR 颜色/粗细，
    // 丢弃光标移动等其他序列（MVP 不支持光标定位，见 REMAINING-PLAN P4-R1）。
    private readonly VtStripper _vt = new();

    // 跨块有状态的 UTF-8 解码器：中文等多字节字符横跨 4KB 读取块边界时，
    // 逐块独立 GetString 会产生 U+FFFD 丢字。GetDecoder 缓存未完成的字节序列。
    private readonly System.Text.Decoder _utf8Decoder = Encoding.UTF8.GetDecoder();

    // 终端输出上限（run 数）：RichTextBlock 无界增长会拖垮 UI 线程和内存，
    // 长会话（cat 大文件）尤甚。超限丢弃最旧的 Paragraph。
    private const int MaxParagraphs = 2000;

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
            AppendSpans(new[] { new StyledSpan(
                $"无法启动终端: {ex.Message}\n\n请确认容器 {container} 正在运行 (wslc start {container})。",
                default) });
        }
    }

    private void OnOutput(byte[] data)
    {
        // 字节 → 字符（跨块解码）→ VT 解析（跨块状态机）→ 带样式文本段。
        var chars = new char[_utf8Decoder.GetCharCount(data, 0, data.Length, flush: false)];
        _utf8Decoder.GetChars(data, 0, data.Length, chars, 0, flush: false);
        var spans = _vt.Feed(new string(chars));
        if (spans.Count == 0) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            AppendSpans(spans);
            OutputScroller.ChangeView(null, OutputScroller.ScrollableHeight, 1f);
        });
    }

    /// <summary>
    /// 把带样式文本段追加为当前段落的 Run。样式变化才新建段落边界。
    /// WinUI 的 Run 不支持 Background/Opacity（WPF 属性）：背景色忽略
    /// （R1 已知限制），dim 用亮色阶画刷模拟。
    /// </summary>
    private void AppendSpans(IReadOnlyList<StyledSpan> spans)
    {
        var para = new Paragraph();
        foreach (var s in spans)
        {
            var run = new Run { Text = s.Text };
            if (s.Style.Foreground is { } fg) run.Foreground = PaletteBrush(fg, dim: s.Style.Dim);
            if (s.Style.Bold) run.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
            para.Inlines.Add(run);
        }
        OutputText.Blocks.Add(para);

        // 输出上限：丢弃最旧段落（近似终端回滚缓冲区的行为）。
        while (OutputText.Blocks.Count > MaxParagraphs)
            OutputText.Blocks.RemoveAt(0);
    }

    // --------------------------------------------------------------------
    //  调色板（xterm 256 色的前 16 项 + 常用 256 色立方体索引的直接映射）。
    //  浅色/深色主题用 Application.Current.Resources 里的系统画刷做前景，
    //  其余用固定 Color（终端惯例配色不跟主题翻转）。
    // --------------------------------------------------------------------

    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush[] Palette =
        CreatePalette();

    private static Microsoft.UI.Xaml.Media.SolidColorBrush[] CreatePalette()
    {
        // xterm 标准 16 色（0-15）：黑、红、绿、黄、蓝、洋红、青、白 + 亮色变体。
        (byte R, byte G, byte B)[] base16 =
        {
            (0x00, 0x00, 0x00), (0xCD, 0x00, 0x00), (0x00, 0xCD, 0x00), (0xCD, 0xCD, 0x00),
            (0x00, 0x00, 0xEE), (0xCD, 0x00, 0xCD), (0x00, 0xCD, 0xCD), (0xE5, 0xE5, 0xE5),
            (0x7F, 0x7F, 0x7F), (0xFF, 0x00, 0x00), (0x00, 0xFF, 0x00), (0xFF, 0xFF, 0x00),
            (0x5C, 0x5C, 0xFF), (0xFF, 0x00, 0xFF), (0x00, 0xFF, 0xFF), (0xFF, 0xFF, 0xFF),
        };
        var palette = new Microsoft.UI.Xaml.Media.SolidColorBrush[256];
        for (var i = 0; i < 16; i++)
            palette[i] = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(0xFF, base16[i].R, base16[i].G, base16[i].B));
        // 16-231：6×6×6 色立方，阶 (0,95,135,175,215,255)。
        var levels = new byte[] { 0, 95, 135, 175, 215, 255 };
        for (var i = 16; i < 232; i++)
        {
            var c = i - 16;
            palette[i] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF,
                levels[c / 36], levels[(c / 6) % 6], levels[c % 6]));
        }
        // 232-255：灰度 8-238。
        for (var i = 232; i < 256; i++)
        {
            var g = (byte)(8 + (i - 232) * 10);
            palette[i] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, g, g, g));
        }
        return palette;
    }

    private static Microsoft.UI.Xaml.Media.Brush PaletteBrush(byte index, bool dim)
    {
        // 前景黑色（调色板 0）在深色主题下不可见：换用主题前景色。
        if (index == 0)
            return (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorPrimaryBrush"];
        // dim：亮色降一档（8-15 → 0-7），普通色用主题灰模拟。
        if (dim)
        {
            if (index >= 8 && index < 16) index -= 8;
            else if (index >= 248) index -= 12; // 很亮的灰 → 稍暗
            else if (index >= 232) index = 244; // 灰阶压暗
        }
        return Palette[index];
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
