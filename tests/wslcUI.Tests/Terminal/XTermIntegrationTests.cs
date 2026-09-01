using System.IO;
using System.Text;
using XTerm;
using XTerm.Options;
using Xunit;

namespace wslcUI.Tests.Terminal;

/// <summary>
/// R3：XTerm.NET 集成的离线驱动验证（spike 夹具 → Terminal 状态断言）。
/// TerminalView（XAML 控件）的视觉验证需 ConPTY 恢复（P1a），此处验证数据层：
/// PTY 字节流 → Terminal.Write → Buffer 状态（文本/颜色/光标/备用屏）。
/// 夹具与断言口径延续 Spike A/B（docs/TERMINAL-RENDER-DECISION.md）。
/// </summary>
public class XTermIntegrationTests
{
    private static string LoadFixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", name);
        Assert.True(File.Exists(path), $"夹具缺失: {path}");
        return File.ReadAllText(path, Encoding.UTF8);
    }

    private static XTerm.Terminal Feed(string name, int cols = 80, int rows = 30)
    {
        var t = new XTerm.Terminal(new TerminalOptions { Cols = cols, Rows = rows, Scrollback = 100 });
        t.Write(LoadFixture(name));
        return t;
    }

    [Fact]
    public void LsFixture_DirectoryCellHasBoldBlueAttributes()
    {
        var t = Feed("ls.vt");
        // 首行 "bin opt"（清洗后的夹具从第 0 行开始）；bin 是 1;34 = bold + 蓝色（调色板 4）
        var line = t.Buffer.Lines[0] ?? throw new InvalidOperationException("line 0 missing");
        var cell = line[0];
        Assert.Equal('b', cell.Content[0]);
        Assert.Equal(1, cell.Attributes.Extended & AttributeFlagsProbe.Bold);
        Assert.Equal(4, cell.Attributes.Fg);
    }

    [Fact]
    public void ViFixture_FinalStateAndAltBuffer()
    {
        var t = Feed("vi.vt", cols: 80, rows: 30);
        // 终态（stdin EOF 使 vi 跳到末行）：视图顶行 = 文件第 3 行
        Assert.Equal("line three", t.GetLine(0).TrimEnd());
        Assert.Equal("~", t.GetLine(1).TrimEnd());
        // 状态栏终态 3/3 100%（EL 重写后不残留旧值）
        Assert.Contains("3/3 100%", t.GetLine(29));
        Assert.DoesNotContain("1/3 33%", string.Join('\n', t.GetVisibleLines()));
        // script 头部噪声被备用屏清掉
        Assert.DoesNotContain("Script started", string.Join('\n', t.GetVisibleLines()));
    }

    [Fact]
    public void TopFixture_HeaderAndFullWidthRows()
    {
        var t = Feed("top.vt", cols: 80, rows: 45);
        var all = string.Join('\n', t.GetVisibleLines());
        Assert.Contains("Mem:", all);
        Assert.Contains("CPU:", all);
        Assert.Contains("COMMAND", all);
        // 满宽行（80 列）+ 延迟换行正确：Mem: 仍在第 0 行（若立即换行会整体下移）
        Assert.StartsWith("Mem:", t.GetLine(0).TrimEnd());
        Assert.DoesNotContain("Script started", all);
    }

    [Fact]
    public void CrossChunkWrite_ProducesSameScreen()
    {
        // PTY 是分块回调（4KB 块），Terminal.Write 分块喂必须与一次喂等价
        var text = LoadFixture("vi.vt");
        var whole = new XTerm.Terminal(new TerminalOptions { Cols = 80, Rows = 30 });
        whole.Write(text);
        var chunked = new XTerm.Terminal(new TerminalOptions { Cols = 80, Rows = 30 });
        for (var i = 0; i < text.Length; i += 7)
            chunked.Write(text.Substring(i, Math.Min(7, text.Length - i)));
        Assert.Equal(whole.GetVisibleLines(), chunked.GetVisibleLines());
    }

    [Fact]
    public void ColorEncoding_DefaultAndPaletteAndTrueColor()
    {
        // 与 TerminalView.BrushFor 的编码假设对齐（实测探针结论，防止库升级漂移）：
        var t = new XTerm.Terminal();
        t.Write("\x1b[31mR\x1b[0mN \x1b[38;2;10;20;30mT");
        var line = t.Buffer.Lines[0] ?? throw new InvalidOperationException("line 0 missing");
        var red = line[0];
        Assert.Equal(1, red.Attributes.Fg);                      // 31 → 调色板 1
        var normal = line[1];
        Assert.Equal(256, normal.Attributes.Fg);                  // 默认 fg = 256
        var trueColor = line[3];
        Assert.True(trueColor.Attributes.Fg >= 0x1000000);        // 真彩色高位编码
        Assert.Equal(10, (trueColor.Attributes.Fg >> 16) & 0xFF); // R=10
    }

    [Fact]
    public void CjkWideChar_OccupiesTwoCells()
    {
        // 中文场景刚需：宽字符占 2 cell，第二 cell 宽度标记（渲染层跳过占位 cell）
        var t = new XTerm.Terminal();
        t.Write("中文");
        var line = t.Buffer.Lines[0] ?? throw new InvalidOperationException("line 0 missing");
        var c0 = line[0];
        var c1 = line[1];
        Assert.Equal("中", c0.Content);
        Assert.Equal(2, c0.Width);
        // 占位 cell：宽度 0 / 无内容
        Assert.Equal(0, c1.Width);
    }

    [Fact]
    public void Resize_PreservesContent()
    {
        var t = new XTerm.Terminal(new TerminalOptions { Cols = 80, Rows = 30 });
        t.Write("hello\r\nworld");
        t.Resize(120, 40);
        Assert.Equal(120, t.Cols);
        Assert.Equal(40, t.Rows);
        Assert.Equal("hello", t.GetLine(0).TrimEnd());
        Assert.Equal("world", t.GetLine(1).TrimEnd());
    }
}

/// <summary>XTerm.NET AttributeData.Extended 样式位（与 TerminalView.AttributeFlags 同值）。</summary>
internal static class AttributeFlagsProbe
{
    public const int Bold = 0x01;
    public const int Dim = 0x02;
    public const int Italic = 0x04;
    public const int Underline = 0x08;
    public const int Blink = 0x10;
    public const int Inverse = 0x20;
}
