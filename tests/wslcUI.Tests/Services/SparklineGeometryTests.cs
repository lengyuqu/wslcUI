using System.Collections.Generic;
using System.Linq;
using wslcUI.Services;
using Xunit;

namespace wslcUI.Tests.Services;

/// <summary>
/// 曲线的坐标换算（<see cref="SparklineGeometry.Build"/>）。纯函数、不碰 UI 类型，
/// 所以能脱离窗口逐条钉住 —— 折线画歪了在看图时很难发现，只能靠这里。
/// 约定：原点左上（与 XAML 一致），值越大 Y 越小。
/// </summary>
public class SparklineGeometryTests
{
    private const double W = 296;
    private const double H = 36;

    [Fact]
    public void EmptyInputYieldsNoPoints()
    {
        Assert.Empty(SparklineGeometry.Build(new List<double>(), W, H));
    }

    [Theory]
    [InlineData(0, 36)]
    [InlineData(296, 0)]
    [InlineData(-1, 36)]
    public void DegenerateSizeYieldsNoPoints(double width, double height)
    {
        Assert.Empty(SparklineGeometry.Build(new List<double> { 1, 2 }, width, height));
    }

    /// <summary>单点画不出趋势 —— 用同一高度横贯整幅（而不是返回 0 个点让图上空着）。</summary>
    [Fact]
    public void SingleValueBecomesFlatLine()
    {
        var pts = SparklineGeometry.Build(new List<double> { 5 }, W, H, maxValue: 10);

        Assert.Equal(2, pts.Count);
        Assert.Equal(0, pts[0].X);
        Assert.Equal(W, pts[1].X);
        Assert.Equal(pts[0].Y, pts[1].Y);
        Assert.Equal(18, pts[0].Y); // 5/10 → 半高
    }

    /// <summary>点数与 X 分布：首点 0、末点 W、等距。</summary>
    [Fact]
    public void XSpansFullWidthEvenly()
    {
        var pts = SparklineGeometry.Build(new List<double> { 1, 2, 3 }, W, H, maxValue: 3);

        Assert.Equal(3, pts.Count);
        Assert.Equal(0, pts[0].X);
        Assert.Equal(W, pts[2].X);
        Assert.Equal(W / 2, pts[1].X); // 296/2
    }

    /// <summary>Y 轴：值 == max 贴顶（0），值 == 0 贴底（height）。</summary>
    [Fact]
    public void ValueMapsToProportionalHeight()
    {
        var pts = SparklineGeometry.Build(new List<double> { 0, 10 }, W, H, maxValue: 10);

        Assert.Equal(H, pts[0].Y); // 0 → 贴底
        Assert.Equal(0, pts[1].Y); // 10 → 贴顶
    }

    /// <summary>超上限的值被夹到贴顶，不画出画面外。</summary>
    [Fact]
    public void ValuesAboveMaxAreClampedToTop()
    {
        var pts = SparklineGeometry.Build(new List<double> { 999 }, W, H, maxValue: 10);
        Assert.Equal(0, pts[0].Y);
    }

    /// <summary>负值被夹到贴底（不能跑到图外，也不能反过来当成"负高度"）。</summary>
    [Fact]
    public void NegativeValuesAreClampedToBottom()
    {
        var pts = SparklineGeometry.Build(new List<double> { -5, 5 }, W, H, maxValue: 5);
        Assert.Equal(H, pts[0].Y);
        Assert.Equal(0, pts[1].Y);
    }

    /// <summary>maxValue 缺省时按本批最大值自动缩放。</summary>
    [Fact]
    public void AutoScalesToDataMax()
    {
        var pts = SparklineGeometry.Build(new List<double> { 0, 5 }, W, H);

        Assert.Equal(H, pts[0].Y);
        Assert.Equal(0, pts[1].Y);
    }

    /// <summary>全 0 序列不能出 NaN（除零保护）—— 结果应是一条贴底的直线。</summary>
    [Fact]
    public void AllZeroSeriesHasNoNaN()
    {
        var pts = SparklineGeometry.Build(new List<double> { 0, 0, 0 }, W, H);

        Assert.Equal(3, pts.Count);
        Assert.All(pts, p =>
        {
            Assert.False(double.IsNaN(p.Y));
            Assert.Equal(H, p.Y);
        });
    }

    /// <summary>点数与输入一一对应（曲线不能悄悄丢点或补点）。</summary>
    [Fact]
    public void PointCountMatchesInputCount()
    {
        var values = Enumerable.Range(0, 60).Select(i => (double)i).ToList();
        var pts = SparklineGeometry.Build(values, W, H);

        Assert.Equal(values.Count, pts.Count);
        Assert.Equal(0, pts.First().X);
        Assert.Equal(W, pts.Last().X);
    }
}
