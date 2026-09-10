using wslcUI.Services;
using Xunit;

namespace wslcUI.Tests.Services;

/// <summary>
/// wslc 表格解析器回归测试。
///
/// 为什么需要这组测试：容器 / 镜像 / 网络 / 统计四个列表页的数据**全部**来自
/// <see cref="WslcCli"/> 的定宽表格解析（列宽由表头推导，再按同一套列位切片）。
/// 这套算法历史上出过「解析静默返回空列表」的事故（早期 JSON 优先路径），
/// 而它此前只有手敲的真机 verify V2 覆盖，没有任何自动回归保护。
///
/// 夹具分两类：
///   • 真机原文 —— 2026-09-10 在 WinR9 / wslc 2.9.9.0 上直接抓取
///     （`wslc list -a` / `images` / `network ls` / `stats`）。换 wslc 版本
///     后若这里挂掉，说明输出列格式变了，需按新格式校正夹具与解析器。
///   • 构造行 —— 真机跑不出的分支（长 ID、缺列、带 sha256: 前缀、中文 stats
///     有数据行等）。列位由脚本按「列宽 = max(表头, 值)」对齐，与 wslc 自身
///     的 pad 规则一致（字符级，中文按 BMP 码点计位）。
///
/// 夹具维护：wslc 升级后若这组测试变红，先重跑一次
/// `wslc list -a` / `images` / `network ls` / `stats` 核对真实表头，再按
/// 「列宽 = max(表头 caption, 值) 字符数、列间 3 空格」重建构造行；若表头
/// caption 本身变了，则要同步改 <see cref="WslcCli"/> 里的 FindColumn 参数。
/// </summary>
public class TableParserTests
{
    // ================= 真机夹具（wslc 2.9.9.0，2026-09-10 抓取）================

    /// <summary>`wslc list -a`（中文表头；数据行端口列为空、行尾有 pad 空格）</summary>
    private const string ListRealOutput =
        "容器 ID          名称        映像            已创建     状态            端口\n" +
        "20f2ed8f095c   wslc-pg   postgres:16   10 天前   exited 9 天前   \n";

    /// <summary>`wslc images`（英文表头，3 条）</summary>
    private const string ImagesRealOutput =
        "REPOSITORY   TAG      IMAGE ID       CREATED   SIZE\n" +
        "postgres     16       80f4c7a5e916   2 周前      451MB\n" +
        "alpine       latest   d529dd0c6e55   2 个月前     8.42MB\n" +
        "busybox      latest   c6348fa86ba0   4 个月前     4.45MB\n";

    /// <summary>`wslc network ls`（英文表头，3 条）</summary>
    private const string NetworkRealOutput =
        "NETWORK ID     NAME      DRIVER    SCOPE\n" +
        "7ff8d20149cd   bridge    bridge    local\n" +
        "a108496fa403   host      host      local\n" +
        "e4245251477d   none      null      local\n";

    /// <summary>`wslc stats`（本机无运行容器 —— 只有表头，零数据行）</summary>
    private const string StatsRealHeaderOnlyOutput =
        "容器 ID   名称   CPU 百分比   最大用量/限制   内存百分比   网络 I/O   块 I/O   PIDS\n";

    // ================= 构造夹具（列位按内容宽度对齐）================

    /// <summary>stats 中文表头 + 数据行（真机表头，行为构造）</summary>
    private const string StatsZhOutput =
        "容器 ID          名称        CPU 百分比   最大用量/限制        内存百分比   网络 I/O      块 I/O     PIDS\n" +
        "20f2ed8f095c   wslc-pg   1.23%     10MiB / 2GiB   0.50%   1kB / 2kB   0B / 0B   5\n";

    /// <summary>stats 英文表头 + 数据行</summary>
    private const string StatsEnOutput =
        "CONTAINER ID   NAME   CPU %   MEM USAGE / LIMIT   MEM %   NET I/O     BLOCK I/O   PIDS\n" +
        "abc123def456   web    1.23%   10MiB / 2GiB        0.50%   1kB / 2kB   0B / 0B     5\n";

    /// <summary>list 英文表头 + 64 字符长 ID（覆盖短 ID 裁剪）</summary>
    private const string ListEnLongIdOutput =
        "CONTAINER ID                                                       NAME   IMAGE          CREATED       STATUS       PORTS \n" +
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa   web    nginx:latest   2 hours ago   Up 2 hours   80/tcp\n";

    /// <summary>list 只有 ID/NAME/IMAGE（CREATED/STATUS/PORTS 列缺失）</summary>
    private const string ListMinimalColumnsOutput =
        "CONTAINER ID   NAME   IMAGE       \n" +
        "abc123def456   web    nginx:latest\n";

    /// <summary>network 只有 NAME（DRIVER/SCOPE 缺失）</summary>
    private const string NetworkNameOnlyOutput =
        "NETWORK ID     NAME  \n" +
        "7ff8d20149cd   bridge\n";

    /// <summary>images IMAGE ID 带 sha256: 前缀</summary>
    private const string ImagesSha256PrefixOutput =
        "REPOSITORY   TAG      IMAGE ID              CREATED        SIZE  \n" +
        "alpine       latest   sha256:d529dd0c6e55   2 months ago   8.42MB\n";

    /// <summary>无效表头（ID/NAME/IMAGE 全缺）</summary>
    private const string UnrecognisedHeaderOutput =
        "FOO   BAR\n" +
        "1     2\n";

    // ================= 容器列表 `wslc list -a` =================

    [Fact]
    public void ParseContainerList_RealOutput_ParsesAllColumns()
    {
        var rows = WslcCli.ParseContainerList(ListRealOutput);

        var c = Assert.Single(rows);
        Assert.Equal("20f2ed8f095c", c.Id);
        Assert.Equal("wslc-pg", c.Name);
        Assert.Equal("postgres:16", c.Image);
        Assert.Equal("exited 9 天前", c.Status);
        Assert.Equal("10 天前", c.CreatedAt);
    }

    [Fact]
    public void ParseContainerList_EmptyPortsCell_DegradesToDashInsteadOfEmptyString()
    {
        // 端口列在表头里存在，但该行单元格为空 → 降级为「—」，
        // 不用空字符串冒充「有值」。这是 UI 可读性的硬约定。
        var c = Assert.Single(WslcCli.ParseContainerList(ListRealOutput));
        Assert.Equal("—", c.Ports);
    }

    [Fact]
    public void ParseContainerList_LongId_TruncatedToTwelveChars()
    {
        var c = Assert.Single(WslcCli.ParseContainerList(ListEnLongIdOutput));

        Assert.Equal(new string('a', 12), c.Id);
        Assert.Equal("web", c.Name);
        Assert.Equal("nginx:latest", c.Image);
        Assert.Equal("Up 2 hours", c.Status);
        Assert.Equal("80/tcp", c.Ports);
        Assert.Equal("2 hours ago", c.CreatedAt);
    }

    [Fact]
    public void ParseContainerList_MissingOptionalColumns_StatusEmptyAndOthersDash()
    {
        // 只有 ID/NAME/IMAGE 三列时：Status 落空串（模型默认值），
        // Ports / CreatedAt 走「—」降级。
        var c = Assert.Single(WslcCli.ParseContainerList(ListMinimalColumnsOutput));

        Assert.Equal("abc123def456", c.Id);
        Assert.Equal("web", c.Name);
        Assert.Equal("nginx:latest", c.Image);
        Assert.Equal("", c.Status);
        Assert.Equal("—", c.Ports);
        Assert.Equal("—", c.CreatedAt);
    }

    [Fact]
    public void ParseContainerList_HeaderOnly_ReturnsEmpty()
    {
        var headerOnly = ListRealOutput[..ListRealOutput.IndexOf('\n')];
        Assert.Empty(WslcCli.ParseContainerList(headerOnly));
    }

    [Fact]
    public void ParseContainerList_UnrecognisedHeader_ReturnsEmpty()
    {
        // 缺 ID/NAME/IMAGE 全部必需列 → 判为不可识别，返回空而不是瞎猜列序。
        Assert.Empty(WslcCli.ParseContainerList(UnrecognisedHeaderOutput));
    }

    [Fact]
    public void ParseContainerList_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(WslcCli.ParseContainerList(""));
    }

    // ================= 网络 `wslc network ls` =================

    [Fact]
    public void ParseNetworkList_RealOutput_ParsesThreeRows()
    {
        var rows = WslcCli.ParseNetworkList(NetworkRealOutput);

        Assert.Equal(3, rows.Count);
        Assert.Equal(("bridge", "bridge", "local"), (rows[0].Name, rows[0].Driver, rows[0].Scope));
        Assert.Equal(("host", "host", "local"), (rows[1].Name, rows[1].Driver, rows[1].Scope));
        Assert.Equal(("none", "null", "local"), (rows[2].Name, rows[2].Driver, rows[2].Scope));
    }

    [Fact]
    public void ParseNetworkList_MissingDriverAndScope_ReturnsEmptyStrings()
    {
        var n = Assert.Single(WslcCli.ParseNetworkList(NetworkNameOnlyOutput));

        Assert.Equal("bridge", n.Name);
        Assert.Equal("", n.Driver);
        Assert.Equal("", n.Scope);
    }

    [Fact]
    public void ParseNetworkList_LowercaseNameCaption_IsNotMatched()
    {
        // FindColumn 是 Ordinal 精确匹配（大小写敏感）。表头 caption 若是小写
        // "name"，会被判为「无 NAME 列」→ 返回空，而不是错列位取值。
        // 与其静默错位，宁可空列表 —— 这是刻意的保守取向。
        const string header = "NETWORK ID   name   DRIVER   SCOPE\n";
        const string row = "7ff8d20149cd bridge bridge   local\n";

        Assert.Empty(WslcCli.ParseNetworkList(header + row));
    }

    // ================= 镜像 `wslc images` =================

    [Fact]
    public void ParseImageList_RealOutput_ParsesThreeRows()
    {
        var rows = WslcCli.ParseImageList(ImagesRealOutput);

        Assert.Equal(3, rows.Count);
        Assert.Equal("postgres", rows[0].Repository);
        Assert.Equal("16", rows[0].Tag);
        Assert.Equal("80f4c7a5e916", rows[0].Id);
        Assert.Equal("451MB", rows[0].Size);
        Assert.Equal("alpine", rows[1].Repository);
        Assert.Equal("busybox", rows[2].Repository);
    }

    [Fact]
    public void ParseImageList_Sha256PrefixedId_HasPrefixStripped()
    {
        var i = Assert.Single(WslcCli.ParseImageList(ImagesSha256PrefixOutput));

        Assert.Equal("d529dd0c6e55", i.Id);
        Assert.Equal("alpine", i.Repository);
        Assert.Equal("latest", i.Tag);
        Assert.Equal("8.42MB", i.Size);
    }

    // ================= 统计 `wslc stats` =================

    [Fact]
    public void ParseStats_RealHeaderWithNoRunningContainers_ReturnsEmpty()
    {
        Assert.Empty(WslcCli.ParseStats(StatsRealHeaderOnlyOutput));
    }

    [Fact]
    public void ParseStats_ChineseHeader_ParsesAllEightColumns()
    {
        var s = Assert.Single(WslcCli.ParseStats(StatsZhOutput));

        Assert.Equal("wslc-pg", s.Container);
        Assert.Equal("1.23%", s.Cpu);
        Assert.Equal("10MiB / 2GiB", s.Mem);
        Assert.Equal("0.50%", s.MemPercent);
        Assert.Equal("1kB / 2kB", s.NetIo);
        Assert.Equal("0B / 0B", s.BlockIo);
        Assert.Equal("5", s.Pids);
    }

    [Fact]
    public void ParseStats_EnglishHeader_ParsesAllEightColumns()
    {
        var s = Assert.Single(WslcCli.ParseStats(StatsEnOutput));

        Assert.Equal("web", s.Container);
        Assert.Equal("1.23%", s.Cpu);
        Assert.Equal("10MiB / 2GiB", s.Mem);
        Assert.Equal("0.50%", s.MemPercent);
        Assert.Equal("1kB / 2kB", s.NetIo);
        Assert.Equal("0B / 0B", s.BlockIo);
        Assert.Equal("5", s.Pids);
    }

    // ================= 列位推导 / 切片 =================

    [Fact]
    public void ComputeColumnBoundaries_RealContainerListHeader_YieldsSevenBoundaries()
    {
        var header = ListRealOutput[..ListRealOutput.IndexOf('\n')];
        var b = WslcCli.ComputeColumnBoundaries(header);

        // 6 列 → 6 个起点 + 1 个结尾哨兵。
        Assert.Equal(new[] { 0, 15, 25, 39, 47, 61, 63 }, b);
    }

    [Fact]
    public void ComputeColumnBoundaries_RealStatsHeader_YieldsNineBoundaries()
    {
        var header = StatsRealHeaderOnlyOutput.TrimEnd('\n');
        var b = WslcCli.ComputeColumnBoundaries(header);

        // 8 列 → 8 个起点 + 1 个结尾哨兵。此表头列间只有 3 个空格，
        // 仍被 [ ]{2,} 正确切分。
        Assert.Equal(new[] { 0, 8, 13, 23, 33, 41, 50, 58, 62 }, b);
    }

    [Fact]
    public void ComputeColumnBoundaries_SingleSpaceSeparator_IsNotSplit()
    {
        // 已知限制：分隔符要求 2+ 空格。单空格不切分 → 整行成一个 caption，
        // 列数 < 3 因而被 LocateHeader/ParseContainerList 判为「不是表头」而跳过。
        // wslc 2.9.9 的实际输出列间 ≥ 3 空格，不触发此限制。
        Assert.Equal(new[] { 0, 5 }, WslcCli.ComputeColumnBoundaries("A B C")!);
    }

    [Fact]
    public void ComputeColumnBoundaries_BlankOrEmptyLine_ReturnsNull()
    {
        Assert.Null(WslcCli.ComputeColumnBoundaries(""));
        Assert.Null(WslcCli.ComputeColumnBoundaries("   "));
    }

    [Fact]
    public void FindColumn_MatchesCaptionCaseSensitively()
    {
        const string header = "NETWORK ID   name   DRIVER";
        var b = WslcCli.ComputeColumnBoundaries(header)!;

        Assert.Equal(0, WslcCli.FindColumn(b, header, "NETWORK ID"));
        Assert.Equal(1, WslcCli.FindColumn(b, header, "name"));
        Assert.Equal(-1, WslcCli.FindColumn(b, header, "NAME"));
        Assert.Equal(-1, WslcCli.FindColumn(b, header, "SCOPE"));
    }

    [Fact]
    public void SplitByColumns_ShortRow_PadsMissingTailWithEmptyCells()
    {
        var cells = WslcCli.SplitByColumns(new[] { 0, 5, 10, 15 }, "abc");

        Assert.Equal(new[] { "abc", "", "" }, cells);
    }

    [Fact]
    public void SplitByColumns_LastColumnOverflow_FoldedIntoTailInsteadOfTruncated()
    {
        // 最后一列的值比列宽长（如 SIZE / PORTS 被表头裁剪）时，溢出部分
        // 折叠回该列而不是丢弃 —— 保证不丢数据，只损失完美对齐。
        // boundaries { 0, 4, 8, 12 } = 3 列（第 3 列宽 4）+ 结尾哨兵 12。
        var cells = WslcCli.SplitByColumns(new[] { 0, 4, 8, 12 }, "aaaabbbbccccEEEEEE");

        Assert.Equal(new[] { "aaaa", "bbbb", "ccccEEEEEE" }, cells);
    }

    // ================= 表头定位 =================

    [Fact]
    public void LocateHeader_SkipsBlankLinesAndDashSeparator()
    {
        const string output = "\n---\nNETWORK ID   NAME\n7ff8d20149cd   bridge\n";

        var (header, boundaries, index) = WslcCli.LocateHeader(output);

        Assert.Equal("NETWORK ID   NAME", header);
        Assert.Equal(2, index);
        Assert.Equal(new[] { 0, 13, 17 }, boundaries);
    }

    [Fact]
    public void LocateHeader_NoSplittableLine_ReturnsNullSentinel()
    {
        var (header, boundaries, index) = WslcCli.LocateHeader("\n\n   \n");

        Assert.Null(header);
        Assert.Empty(boundaries);
        Assert.Equal(-1, index);
    }
}
