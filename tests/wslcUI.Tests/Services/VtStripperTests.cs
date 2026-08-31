using wslcUI.Services;
using Xunit;

namespace wslcUI.Tests.Services;

/// <summary>
/// VtStripper（P4-R1 SGR 颜色子集解析器）测试。纯逻辑、无 Win32 依赖。
/// 用例取自常见 CLI 工具的真实输出形态（ls / grep / echo -e）。
/// </summary>
public class VtStripperTests
{
    private static List<StyledSpan> FeedOne(string input) => new VtStripper().Feed(input).ToList();

    [Fact]
    public void PlainText_SingleDefaultSpan()
    {
        var spans = FeedOne("hello 终端");
        Assert.Single(spans);
        Assert.Equal("hello 终端", spans[0].Text);
        Assert.Equal(default(SpanStyle), spans[0].Style);
    }

    [Fact]
    public void SgrRedFg_SplitIntoTwoSpans()
    {
        // echo -e '\x1b[31m红\x1b[0m正常'
        var spans = FeedOne("\x1b[31m红\x1b[0m正常");
        Assert.Equal(2, spans.Count);
        Assert.Equal("红", spans[0].Text);
        Assert.Equal((byte?)1, spans[0].Style.Foreground);      // 31 → 调色板 1
        Assert.Equal("正常", spans[1].Text);
        Assert.Null(spans[1].Style.Foreground);              // 0 重置
    }

    [Fact]
    public void SgrCombinedParams_BoldAndColor()
    {
        // \x1b[1;33m → bold + 前景 3（黄）
        var spans = FeedOne("\x1b[1;33m警告\x1b[0m");
        Assert.Single(spans);
        Assert.True(spans[0].Style.Bold);
        Assert.Equal((byte?)3, spans[0].Style.Foreground);     // 1;33 → 黄
    }

    [Fact]
    public void SgrBoldOff_KeepsColor()
    {
        // 1;32 → bold 绿，22 → 取消 bold 但颜色保留
        var spans = FeedOne("\x1b[1;32mgo\x1b[22mon");
        Assert.Equal(2, spans.Count);
        Assert.True(spans[0].Style.Bold);
        Assert.Equal((byte?)2, spans[0].Style.Foreground);
        Assert.False(spans[1].Style.Bold);
        Assert.Equal((byte?)2, spans[1].Style.Foreground);     // 颜色未被 22 重置
    }

    [Fact]
    public void BrightForeground_MapsToPalette8To15()
    {
        var spans = FeedOne("\x1b[91mx");
        Assert.Equal((byte?)9, spans[0].Style.Foreground);      // 91 → 调色板 9
        var spans2 = FeedOne("\x1b[107mx");
        Assert.Equal((byte?)15, spans2[0].Style.Background);    // 107 → 调色板 15
    }

    [Fact]
    public void Sgr256Color_Parsed()
    {
        var spans = FeedOne("\x1b[38;5;208m橙\x1b[48;5;17m底");
        Assert.Equal((byte?)208, spans[0].Style.Foreground);
        Assert.Equal((byte?)17, spans[1].Style.Background);
    }

    [Fact]
    public void TrueColor_SkippedGracefully()
    {
        // 38;2;r;g;b 不支持：整组跳过，后续参数（1）仍生效
        var spans = FeedOne("\x1b[38;2;10;20;30;1m粗");
        Assert.Single(spans);
        Assert.True(spans[0].Style.Bold);
        Assert.Null(spans[0].Style.Foreground);
    }

    [Fact]
    public void NonSgrCsi_Dropped()
    {
        // 光标移动/擦除序列丢弃，文本不丢
        var spans = FeedOne("a\x1b[2K\rb");
        Assert.Equal(2, spans.Count);
        Assert.Equal("a", spans[0].Text);
        Assert.Equal("\rb", spans[1].Text);
    }

    [Fact]
    public void Osc_BelAndStEndings_Stripped()
    {
        var spans = FeedOne("\x1b]0;标题\x07正文\x1b]0;t\x1b\\尾");
        Assert.Equal(2, spans.Count);
        Assert.Equal("正文", spans[0].Text);
        Assert.Equal("尾", spans[1].Text);
    }

    [Fact]
    public void TwoCharEscape_Dropped()
    {
        // ESC c（终端重置）等两字符转义：丢弃 ESC 与后续字符
        // 注意：\x 转义贪婪，ESC 后跟十六进制字符时必须用 \u001b 明确终止。
        var spans = FeedOne("a\u001bcb");
        Assert.Equal(2, spans.Count);
        Assert.Equal("a", spans[0].Text);
        Assert.Equal("b", spans[1].Text);
    }

    [Fact]
    public void CrossChunkSequence_StatePreserved()
    {
        // 转义序列横跨两次 Feed：\x1b | [31 | m
        var stripper = new VtStripper();
        var spans = new List<StyledSpan>();
        spans.AddRange(stripper.Feed("前\x1b"));
        spans.AddRange(stripper.Feed("[31"));
        spans.AddRange(stripper.Feed("m红"));
        Assert.Equal(2, spans.Count);
        Assert.Equal("前", spans[0].Text);
        Assert.Equal("红", spans[1].Text);
        Assert.Equal((byte?)1, spans[1].Style.Foreground);
    }

    [Fact]
    public void CrossChunkText_SeparateSpansSameStyle()
    {
        // 纯文本分两块：每块 Feed 尾部各 flush 一段（渲染层逐块追加是设计），
        // 两段样式一致（同为绿色），合并职责在渲染层。
        var stripper = new VtStripper();
        var spans = new List<StyledSpan>();
        spans.AddRange(stripper.Feed("\x1b[32mgree"));
        spans.AddRange(stripper.Feed("n"));
        Assert.Equal(2, spans.Count);
        Assert.Equal("gree", spans[0].Text);
        Assert.Equal("n", spans[1].Text);
        Assert.Equal(spans[0].Style, spans[1].Style);            // 样式状态跨块保持
    }

    [Fact]
    public void EmptySgr_ActsAsReset()
    {
        var spans = FeedOne("\x1b[31m红\x1b[m后");
        Assert.Equal(2, spans.Count);
        Assert.Null(spans[1].Style.Foreground);
    }

    [Fact]
    public void StyleResetBetweenInstances()
    {
        // 新实例默认无样式（不残留上一实例状态）
        var a = new VtStripper();
        a.Feed("\x1b[31m");
        var b = new VtStripper();
        var spans = b.Feed("x").ToList();
        Assert.Single(spans);
        Assert.Null(spans[0].Style.Foreground);
    }

    [Fact]
    public void RealWorldGrepOutput_Parsed()
    {
        // grep 输出形态：\x1b[01;31m 文件名 \x1b[01;35m 行号 \x1b[36m 内容 \x1b[m\x1b[K
        var input = "\x1b[01;31msrc/main\x1b[01;35m:42\x1b[36m:var x = 1;\x1b[m\x1b[K";
        var spans = FeedOne(input);
        Assert.Equal(3, spans.Count);
        Assert.Equal("src/main", spans[0].Text);
        Assert.True(spans[0].Style.Bold);
        Assert.Equal((byte?)1, spans[0].Style.Foreground);
        Assert.Equal(":42", spans[1].Text);
        Assert.Equal((byte?)5, spans[1].Style.Foreground);
        Assert.Equal(":var x = 1;", spans[2].Text);
        Assert.Equal((byte?)6, spans[2].Style.Foreground);
    }
}
