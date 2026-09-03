using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using XTerm;
using XTerm.Buffer;

namespace wslcUI.Terminal;

/// <summary>
/// R3：XTerm.NET 终端视图（cell 渲染层）。
///
/// 职责：订阅 <see cref="XTerm.Terminal"/> 的 <see cref="XTerm.Terminal.BufferChanged"/>，
/// 把 <c>Buffer.Lines[y][x]</c> 的 cell 矩阵渲染为行级 <see cref="TextBlock"/> +
/// run 着色（连续同属性 cell 合并为一个 run）。选型与架构见
/// docs/TERMINAL-RENDER-DECISION.md。
///
/// 渲染策略（v1）：整屏重绘 + 视口外行回收。全屏程序（vi/top）每次 BufferChanged
/// 都是整屏或大区域更新，diff 优化收益有限；滚动场景靠行池复用控制分配。
/// 若后续性能不足，替换为 Win2D CanvasControl 自绘（接口不变）。
/// </summary>
public sealed class TerminalView : Panel
{
    private readonly List<TextBlock> _rowBlocks = new();
    private bool _dirty = true;
    private XTerm.Terminal? _terminal;

    private const string FontName = "Consolas";
    internal const double FontSize = 13.5; // TerminalWindow 的 cell 测量探针复用同一字号

    /// <summary>绑定终端实例（XAML 构造无参，运行时注入）。只能调用一次。</summary>
    public void Setup(XTerm.Terminal terminal)
    {
        if (_terminal is not null)
            throw new InvalidOperationException("TerminalView 已绑定终端实例。");
        _terminal = terminal;
        RebuildRowPool();
        _terminal.BufferChanged += OnBufferChanged;
        Loaded += (_, _) => Render();
        Unloaded += (_, _) => _terminal.BufferChanged -= OnBufferChanged;
        SizeChanged += (_, _) => InvalidateMeasure();
        Render();
    }

    /// <summary>当前绑定的终端（未绑定为 null）。</summary>
    public XTerm.Terminal? Terminal => _terminal;

    private void RebuildRowPool()
    {
        var terminal = _terminal ?? throw new InvalidOperationException("TerminalView 未 Setup。");
        foreach (var tb in _rowBlocks) Children.Remove(tb);
        _rowBlocks.Clear();
        for (var y = 0; y < terminal.Rows; y++)
        {
            var tb = CreateRowBlock();
            _rowBlocks.Add(tb);
            Children.Add(tb);
        }
    }

    private TextBlock CreateRowBlock() => new()
    {
        FontFamily = new FontFamily(FontName),
        FontSize = FontSize,
        TextWrapping = TextWrapping.NoWrap,
    };

    private void OnBufferChanged(object? sender, XTerm.Events.TerminalEvents.BufferChangedEventArgs e) => _dirty = true;

    // UI 线程上调用：把 cell 矩阵渲染到行池。
    public void Render()
    {
        var terminal = _terminal;
        if (terminal is null || !_dirty) return;
        _dirty = false;

        // 尺寸变化（Resize）：重建行池
        if (_rowBlocks.Count != terminal.Rows)
            RebuildRowPool();

        for (var y = 0; y < terminal.Rows; y++)
        {
            var tb = _rowBlocks[y];
            tb.Inlines.Clear();
            RenderRow(tb, terminal.Buffer.Lines[y]);
        }
    }

    private void RenderRow(TextBlock tb, BufferLine? line)
    {
        if (line is null) return;

        // 连续同属性 cell 合并为一个 run（同色整行是常态，合并后 run 数接近 1）
        var run = new Run { Text = "", Foreground = DefaultFgBrush };
        var pending = new System.Text.StringBuilder();
        int curFg = ColorCode.DefaultFg, curBg = ColorCode.DefaultBg, curExt = 0;

        void Flush()
        {
            if (pending.Length == 0) return;
            run.Text = pending.ToString();
            tb.Inlines.Add(run);
            pending.Clear();
        }

        var cols = Math.Min(line.Length, _terminal?.Cols ?? 80);
        var prevWide = false; // 上一格是否为双宽字符（CJK 等）
        for (var x = 0; x < cols; x++)
        {
            var cell = line[x];

            // 双宽字符在 cell 缓冲占 2 格：首格存字符，第二格 CodePoint=0、
            // Content 为空。但空白未写格也是 CodePoint=0——用 wcwidth 判定
            // 前格是否宽字符来区分：宽字符续格整格跳过（TextBlock 里 CJK
            // 字形本身 ≈ 2 个拉丁格宽，补空格会多出一列导致错位）。
            string emit;
            if (cell.CodePoint == 0)
            {
                if (prevWide) continue;
                emit = " ";
                prevWide = false;
            }
            else
            {
                emit = cell.Content;
                prevWide = CellWidth(cell.CodePoint) == 2;
            }

            var a = cell.Attributes;
            if (a.Fg != curFg || a.Bg != curBg || a.Extended != curExt)
            {
                Flush();
                curFg = a.Fg; curBg = a.Bg; curExt = a.Extended;
                var fg = a.Fg;
                var bg = a.Bg;
                // inverse：交换前景/背景
                if ((a.Extended & (int)AttributeFlags.Inverse) != 0)
                    (fg, bg) = (bg, fg);
                run = new Run
                {
                    Foreground = BrushFor(fg, foreground: true),
                    FontWeight = (a.Extended & (int)AttributeFlags.Bold) != 0
                        ? Microsoft.UI.Text.FontWeights.Bold
                        : default,
                };
            }
            pending.Append(emit);
        }
        Flush();
    }

    // Wcwidth 是 XTerm.NET 的传递依赖（同一作者维护，与解释器的宽度判定一致）。
    private static int CellWidth(int codePoint) => Wcwidth.UnicodeCalculator.GetWidth(codePoint);

    // --------------------------------------------------------------------
    //  调色板：XTerm.NET 的 Fg/Bg 是 int 编码（< 0 = 默认；0-255 = 256 色索引；
    //  >= 0x1000000 = 真彩色 RGB）。v1 支持 256 色索引，真彩色降级为索引取色。
    //  与 R1 的 TerminalWindow 调色板同源（xterm 标准）。
    // --------------------------------------------------------------------

    private static readonly SolidColorBrush[] Palette = BuildPalette();

    private static SolidColorBrush[] BuildPalette()
    {
        (byte R, byte G, byte B)[] base16 =
        {
            (0x00, 0x00, 0x00), (0xCD, 0x00, 0x00), (0x00, 0xCD, 0x00), (0xCD, 0xCD, 0x00),
            (0x00, 0x00, 0xEE), (0xCD, 0x00, 0xCD), (0x00, 0xCD, 0xCD), (0xE5, 0xE5, 0xE5),
            (0x7F, 0x7F, 0x7F), (0xFF, 0x00, 0x00), (0x00, 0xFF, 0x00), (0xFF, 0xFF, 0x00),
            (0x5C, 0x5C, 0xFF), (0xFF, 0x00, 0xFF), (0x00, 0xFF, 0xFF), (0xFF, 0xFF, 0xFF),
        };
        var p = new SolidColorBrush[256];
        for (var i = 0; i < 16; i++)
            p[i] = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, base16[i].R, base16[i].G, base16[i].B));
        var levels = new byte[] { 0, 95, 135, 175, 215, 255 };
        for (var i = 16; i < 232; i++)
        {
            var c = i - 16;
            p[i] = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF,
                levels[c / 36], levels[(c / 6) % 6], levels[c % 6]));
        }
        for (var i = 232; i < 256; i++)
        {
            var g = (byte)(8 + (i - 232) * 10);
            p[i] = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, g, g, g));
        }
        return p;
    }

    private static Brush DefaultFgBrush =>
        (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

    private static Brush BrushFor(int code, bool foreground)
    {
        // 默认色（256/257）：用主题前景，深浅色主题下都可见
        if (ColorCode.IsDefault(code, foreground)) return DefaultFgBrush;

        // 真彩色：直接构造（R 在高位——实测 0x0A141E 对应 rgb(10,20,30)）
        if (code >= ColorCode.TrueColorBase)
        {
            var r = (byte)((code >> 16) & 0xFF);
            var g = (byte)((code >> 8) & 0xFF);
            var b = (byte)(code & 0xFF);
            return new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, r, g, b));
        }

        // 0-255 调色板索引。前景黑色（0）在深色主题不可见 → 主题前景。
        var idx = code & 0xFF;
        if (foreground && idx == 0) return DefaultFgBrush;
        return Palette[idx];
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Render();
        var width = 0d;
        var height = 0d;
        foreach (var child in Children)
        {
            child.Measure(availableSize);
            width = Math.Max(width, child.DesiredSize.Width);
            height += child.DesiredSize.Height;
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var y = 0d;
        foreach (var child in Children)
        {
            child.Arrange(new Rect(0, y, finalSize.Width, child.DesiredSize.Height));
            y += child.DesiredSize.Height;
        }
        return new Size(finalSize.Width, y);
    }
}

/// <summary>XTerm.NET AttributeData.Extended 的样式标志位（实测 1.1.2 位映射）。</summary>
[Flags]
internal enum AttributeFlags
{
    Bold      = 0x01,
    Dim       = 0x02,
    Italic    = 0x04,
    Underline = 0x08,
    Blink     = 0x10,
    Inverse   = 0x20,
}

/// <summary>
/// XTerm.NET 的 Fg/Bg int 编码（实测）：
///   256（fg）/ 257（bg）= 默认色；0-255 = 256 色调色板索引；
///   >= 0x1000000 = 真彩色（0xRRGGBB）。
/// </summary>
internal static class ColorCode
{
    public const int DefaultFg = 256;
    public const int DefaultBg = 257;
    public const int TrueColorBase = 0x1000000;

    public static bool IsDefault(int code, bool fg) => code == (fg ? DefaultFg : DefaultBg);
}
