using System.IO;
using System.Text;
using Xunit;

namespace wslcUI.Tests.Spike;

/// <summary>
/// P4-R2 Spike A：自研 cell-buffer 原型跑真实 VT 流。
/// 夹具来源：WSL `script` 命令采集（Linux PTY，不受本机 Windows ConPTY 故障影响）：
///   ls.vt / grep.vt   —— 滚动输出（SGR + CR/LF；注意 busybox grep 不发颜色码）
///   vi.vt             —— 全屏程序（DECSET 1049 + CUP + EL + 增量重绘，80x30）
///   top.vt            —— 全屏动态刷新（busybox top 整屏重绘 + 反显表头，80x30）
/// vi.vt 终态说明：script 的 stdin EOF 被 busybox vi 解释成了「跳到末行」，
/// 视图滚到文件第 3 行（确定性终态，断言按此写）。
/// </summary>
public class VtCellSpikeTests
{
    private static string LoadFixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", name);
        Assert.True(File.Exists(path), $"夹具缺失: {path}");
        return File.ReadAllText(path, Encoding.UTF8);
    }

    private static VtCellSpike FeedFixture(string name, int cols = 80, int rows = 24)
    {
        var vt = new VtCellSpike(cols, rows);
        vt.Feed(LoadFixture(name));
        return vt;
    }

    [Fact]
    public void LsFixture_RendersDirectoriesWithBoldBlue()
    {
        var vt = FeedFixture("ls.vt");
        // 目录名存在；ls --color 目录 = 1;34（bold + 蓝）
        var bin = vt.Find("bin");
        Assert.NotNull(bin);
        Assert.True(bin!.Value.Cell.Bold, "目录应为粗体");
        Assert.Equal((byte?)4, bin.Value.Cell.Fg);
        // 多列布局：opt 与 bin 同行（终端 80 列下 ls 排两列）
        var opt = vt.Find("opt");
        Assert.NotNull(opt);
        Assert.Equal(bin.Value.Row, opt!.Value.Row);
    }

    [Fact]
    public void GrepFixture_RendersLineNumberAndText()
    {
        // busybox grep 的 --color 静默无效（不发 SGR）——夹具只验证文本与 -n 行号
        var vt = FeedFixture("grep.vt");
        Assert.NotNull(vt.Find("1:root:x:0:0:root:/root:/bin/sh"));
    }

    [Fact]
    public void ViFixture_RendersFinalScreenState()
    {
        var vt = FeedFixture("vi.vt", cols: 80, rows: 30);
        var lines = vt.RenderLines();

        // 终态（stdin EOF 使 vi 跳到末行）：视图顶行 = 文件第 3 行，其余 ~ 填充
        Assert.Equal("line three", lines[0]);
        for (var y = 1; y <= 28; y++)
            Assert.Equal("~", lines[y]);

        // 状态栏经历 1/3 → 3/3 两次重绘（EL 后重写），终态 3/3 100%
        Assert.Contains("3/3 100%", lines[29]);
        Assert.DoesNotContain("1/3 33%", string.Join('\n', lines));

        // vi 用 ?1049h 备用屏 + ED 清屏：script 头部噪声不应残留
        Assert.DoesNotContain("Script started", string.Join('\n', lines));
    }

    [Fact]
    public void TopFixture_RendersHeaderAndProcessTable()
    {
        // 45 行终端：夹具共 31 行内容，避免末行滚动把 Mem: 表头滚出屏
        var vt = FeedFixture("top.vt", cols: 80, rows: 45);
        var all = string.Join('\n', vt.RenderLines());
        // top 表头与表体
        Assert.Contains("Mem:", all);
        Assert.Contains("CPU:", all);
        Assert.Contains("COMMAND", all);
        // busybox top 用 ESC[7m 反显表头行 —— spike 把 7 当未知 SGR 忽略，文本仍在
        var pid = vt.Find("PID");
        Assert.NotNull(pid);
        // script 头部噪声应被整屏重绘（ESC[H ESC[J）覆盖
        Assert.DoesNotContain("Script started", all);
    }

    [Fact]
    public void CursorPositioning_CupAndRelativeMoves()
    {
        var vt = new VtCellSpike();
        vt.Feed("\x1b[5;10HX");        // 行 5 列 10 写 X（写入后光标右移一列）
        Assert.Equal((10, 4), vt.Cursor);
        vt.Feed("\x1b[2A\x1b[3C>");     // 上 2 行、右 3 列写 >
        Assert.Equal((14, 2), vt.Cursor);
        var lines = vt.RenderLines();
        Assert.Equal('X', lines[4][9]);
        Assert.Equal('>', lines[2][13]);
    }

    [Fact]
    public void EraseOps_EdAndEl()
    {
        var vt = new VtCellSpike();
        vt.Feed("AAAA\r\nBBBB\r\nCCCC");
        vt.Feed("\x1b[2;3H\x1b[K");    // 第 2 行第 3 列起 EL(0)：BB 剩
        vt.Feed("\x1b[1;2H\x1b[J");    // 行 1 列 2 起 ED(0)：A 剩，其后全清
        var lines = vt.RenderLines();
        Assert.Equal("A", lines[0]);
        Assert.Equal("", lines[1]);
        Assert.Equal("", lines[2]);
    }

    [Fact]
    public void LineFeed_PreservesColumn()
    {
        // VT 语义：LF 只下移不回列首（程序需显式发 \r\n）
        var vt = new VtCellSpike();
        vt.Feed("ab\ncd");
        var lines = vt.RenderLines();
        Assert.Equal("ab", lines[0]);
        Assert.Equal("  cd", lines[1]);
    }

    [Fact]
    public void Scroll_NewlinePastLastRow()
    {
        var vt = new VtCellSpike(10, 3);
        vt.Feed("one\r\ntwo\r\nthree\r\nfour");
        var lines = vt.RenderLines();
        Assert.Equal("two", lines[0]);
        Assert.Equal("three", lines[1]);
        Assert.Equal("four", lines[2]);
    }

    [Fact]
    public void CrossChunkFeed_ProducesSameScreen()
    {
        // 整流分块喂 vs 一次喂：屏幕一致（跨块状态机的核心断言）
        var text = LoadFixture("vi.vt");
        var whole = new VtCellSpike(80, 30);
        whole.Feed(text);
        var chunked = new VtCellSpike(80, 30);
        for (var i = 0; i < text.Length; i += 7)
            chunked.Feed(text.Substring(i, System.Math.Min(7, text.Length - i)));
        Assert.Equal(whole.RenderLines(), chunked.RenderLines());
    }
}
