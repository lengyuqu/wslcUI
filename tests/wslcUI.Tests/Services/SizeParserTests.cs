using wslcUI.Services;
using Xunit;

namespace wslcUI.Tests.Services;

/// <summary>
/// `wslc images` SIZE 列解析（<see cref="SizeParser"/>）。这是维护页"镜像总占用"
/// 的唯一数据来源，算错会给出误导性数字，因此把边界都钉住：
/// 真机形态（无空格英文单位）、FakeWslcClient 的带空格形态、二进制写法、
/// 以及**不可识别时必须返回 false 而非 0**（返回 0 会让合计静默偏小）。
/// </summary>
public class SizeParserTests
{
    // ---- 真机形态（wslc 3.0.1 zh-CN，实测 `wslc images` 输出）----
    // 期望值写成**精确字面量**而不是 (long)(8.42 * 1024 * 1024)：后者是截断，
    // 与实现里的 Math.Round 差 1 字节，拿同款公式再算一遍等于没测。
    // 8.42MB = 8829009.92 → 取最近 = 8829010；4.45MB = 4666163.2 → 4666163。

    [Theory]
    [InlineData("451MB", 472907776L)]
    [InlineData("8.42MB", 8829010L)]
    [InlineData("4.45MB", 4666163L)]
    public void ParsesRealWslcSizes(string text, long expected)
    {
        Assert.True(SizeParser.TryParseBytes(text, out var bytes));
        Assert.Equal(expected, bytes);
    }

    // ---- 带空格（docker 生态常见写法；FakeWslcClient 用的就是这种）----

    [Theory]
    [InlineData("187 MB", 196083712L)]
    [InlineData("  7.3 MB  ", 7654605L)]
    public void ToleratesWhitespace(string text, long expected)
    {
        Assert.True(SizeParser.TryParseBytes(text, out var bytes));
        Assert.Equal(expected, bytes);
    }

    // ---- 各单位 + 大小写不敏感 + 二进制写法 ----

    [Theory]
    [InlineData("0B", 0L)]
    [InlineData("512B", 512L)]
    [InlineData("1KB", 1024L)]
    [InlineData("1kb", 1024L)]
    [InlineData("1KiB", 1024L)]
    [InlineData("1GB", 1073741824L)]
    [InlineData("2gib", 2147483648L)]
    [InlineData("1TB", 1099511627776L)]
    [InlineData("1.5GB", 1610612736L)]
    public void ParsesAllUnits(string text, long expected)
    {
        Assert.True(SizeParser.TryParseBytes(text, out var bytes));
        Assert.Equal(expected, bytes);
    }

    /// <summary>裸数字按字节处理（wslc 的 `list --size` 会出现 `0B` 这类值）。</summary>
    [Fact]
    public void BareNumberIsBytes()
    {
        Assert.True(SizeParser.TryParseBytes("1024", out var bytes));
        Assert.Equal(1024, bytes);
    }

    // ---- 不可识别 → false（关键：不能返回 0 冒充"已解析"）----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("N/A")]
    [InlineData("—")]
    [InlineData("unknown")]
    [InlineData("451MB (虚拟 1GB)")]
    [InlineData("-5MB")]
    [InlineData("MB")]
    public void UnrecognisedReturnsFalse(string? text)
    {
        Assert.False(SizeParser.TryParseBytes(text, out var bytes));
        Assert.Equal(0, bytes);
    }

    // ---- 反向格式化 ----

    [Theory]
    [InlineData(0L, "0B")]
    [InlineData(-1L, "0B")]
    [InlineData(512L, "512B")]
    [InlineData(1024L, "1KB")]
    [InlineData(1536L, "1.5KB")]
    [InlineData(472907776L, "451MB")]
    [InlineData(1073741824L, "1GB")]
    public void FormatsHumanReadable(long bytes, string expected)
    {
        Assert.Equal(expected, SizeParser.Format(bytes));
    }

    /// <summary>解析 → 格式化 的往返：真机三个镜像 SIZE 求和后应落在 GB 级以下。</summary>
    [Fact]
    public void SumsRealImageList()
    {
        string[] sizes = { "451MB", "8.42MB", "4.45MB" };
        long total = 0;
        foreach (var s in sizes)
        {
            Assert.True(SizeParser.TryParseBytes(s, out var b));
            total += b;
        }
        Assert.Equal("463.9MB", SizeParser.Format(total));
    }
}
