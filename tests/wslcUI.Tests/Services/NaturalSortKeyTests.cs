using System.Collections.Generic;
using wslcUI.Services;
using Xunit;

namespace wslcUI.Tests.Services;

/// <summary>
/// 单位感知的排序折算（<see cref="NaturalSortKey"/>）。
/// 夹具取自真机 <c>wslc images</c> / <c>wslc stats -a</c>（3.0.1，zh-CN）：
/// <c>78.1MB / 451MB / 2 周前 / 3 个月前 / 0B / 3.86GiB</c>。
/// 这些列全是"给人看的字符串"，只取前导数字会排反 —— 下面的顺序断言本身就是修复。
/// </summary>
public class NaturalSortKeyTests
{
    [Theory]
    [InlineData("5 分钟前", "2 周前")]
    [InlineData("2 周前", "3 个月前")]
    [InlineData("3 个月前", "1 年前")]
    [InlineData("1 天", "1 周")]
    [InlineData("30 秒", "1 分钟")]
    [InlineData("59 分钟", "1 小时")]
    [InlineData("1 小时", "1 天")]
    public void DurationsOrderAcrossUnits(string smaller, string larger)
    {
        Assert.True(NaturalSortKey.Rank(smaller) < NaturalSortKey.Rank(larger),
            $"{smaller} 应排在 {larger} 之前");
    }

    [Theory]
    [InlineData("2 weeks ago", "3 months ago")]
    [InlineData("5 minutes", "2 hours")]
    [InlineData("1 day", "1 week")]
    [InlineData("90 seconds", "2 minutes")]
    public void EnglishDurationsOrder(string smaller, string larger)
    {
        Assert.True(NaturalSortKey.Rank(smaller) < NaturalSortKey.Rank(larger),
            $"{smaller} 应排在 {larger} 之前");
    }

    [Theory]
    [InlineData("1 hr", "1 hour")]
    [InlineData("1 min", "1 minute")]
    [InlineData("2 secs", "2 seconds")]
    public void EnglishAbbreviationsEqualFullNames(string a, string b)
    {
        Assert.Equal(NaturalSortKey.Rank(a), NaturalSortKey.Rank(b));
    }

    [Theory]
    [InlineData("1024KB", "1MB")]
    [InlineData("1MB", "1MiB")]
    [InlineData("1GiB", "1024MiB")]
    [InlineData("0B", "0MB")]
    public void SameSizeInDifferentUnitsIsEqual(string a, string b)
    {
        Assert.Equal(NaturalSortKey.Rank(a), NaturalSortKey.Rank(b));
    }

    [Theory]
    [InlineData("4.45MB", "78.1MB")]
    [InlineData("78.1MB", "162MB")]
    [InlineData("162MB", "451MB")]
    [InlineData("451MB", "1.5GB")]     // 只取前导数字会得 451 > 1.5 —— 正是旧写法的错
    [InlineData("9 MB", "850 MB")]     // 带空格的 docker 写法同样要按量级比
    public void ByteSizesOrderAcrossUnits(string smaller, string larger)
    {
        Assert.True(NaturalSortKey.Rank(smaller) < NaturalSortKey.Rank(larger),
            $"{smaller} 应小于 {larger}");
    }

    [Fact]
    public void RealImageSizeColumnSortsByMagnitude()
    {
        // 真机 wslc images 的 SIZE 列原样（busybox/alpine/ubuntu/postgres + 一个更大的）。
        var ranked = new List<string> { "1.5GB", "451MB", "78.1MB", "8.42MB", "4.45MB" };
        ranked.Sort((a, b) => NaturalSortKey.Rank(a).CompareTo(NaturalSortKey.Rank(b)));
        Assert.Equal(new[] { "4.45MB", "8.42MB", "78.1MB", "451MB", "1.5GB" }, ranked);
    }

    [Fact]
    public void UsageOverLimitRanksByUsedPortion()
    {
        // stats 的「最大用量/限制」形如 "0B / 3.86GiB"：整串不是合法尺寸，
        // 取斜杠前一半（用量）来排；否则整列都落在不可比，排序等于没排。
        Assert.Equal(0d, NaturalSortKey.Rank("0B / 3.86GiB"));
        Assert.True(NaturalSortKey.Rank("12MB / 3.86GiB") < NaturalSortKey.Rank("1.2GB / 3.86GiB"));
    }

    [Theory]
    [InlineData("12.3%", 12.3)]
    [InlineData("0.00%", 0.0)]
    [InlineData("850", 850)]
    public void PlainNumbersStillRank(string text, double expected)
    {
        Assert.Equal(expected, NaturalSortKey.Rank(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("—")]
    [InlineData("N/A")]
    public void UnrankableValuesClusterAtOneEnd(string? text)
    {
        Assert.Equal(NaturalSortKey.Unrankable, NaturalSortKey.Rank(text));
        // 任何可比值都必须大于它，否则"读不出来的行"会插进数值中间。
        Assert.True(NaturalSortKey.Rank(text) < NaturalSortKey.Rank("1B"));
    }

    [Fact]
    public void UnknownUnitFallsBackToTheBareNumber()
    {
        // 单位词没收录时**不猜量纲**（猜错会整列静默排错），退到"取前导数字"这一层。
        // 所以 7 光年 与 7 秒 会折成同一个数 —— 已知取舍，这里钉住而不是假装它精确。
        Assert.Equal(7d, NaturalSortKey.Rank("7 光年"));
        Assert.True(NaturalSortKey.Rank("8 秒") > NaturalSortKey.Rank("7 光年"));
    }
}
