using System;
using System.Collections.Generic;

namespace wslcUI.Services;

/// <summary>
/// 把一串采样值折成折线坐标（sparkline）。刻意做成**不依赖任何 UI 类型**的纯函数
/// （返回 <c>(double X, double Y)</c> 而不是 <c>Windows.Foundation.Point</c>），
/// 这样曲线逻辑可以脱离窗口单测 —— ViewModel 只负责把结果塞进 <c>PointCollection</c>。
/// </summary>
internal static class SparklineGeometry
{
    /// <summary>
    /// 在 <paramref name="width"/> × <paramref name="height"/> 的矩形内生成折线点。
    /// 原点在左上（与 XAML 一致），因此值越大 Y 越小。
    /// </summary>
    /// <param name="values">按时间先后排列的采样值。</param>
    /// <param name="maxValue">
    /// Y 轴上限。≤ 0 时退化为「用本批数据的最大值」，全 0 时用 1 防止除零 ——
    /// 结果是一条贴底的直线，而不是 NaN。
    /// </param>
    internal static IReadOnlyList<(double X, double Y)> Build(
        IReadOnlyList<double> values, double width, double height, double maxValue = 0)
    {
        var points = new List<(double X, double Y)>();
        if (values.Count == 0 || width <= 0 || height <= 0) return points;

        var scale = maxValue;
        if (scale <= 0)
        {
            foreach (var v in values)
                if (v > scale) scale = v;
        }
        if (scale <= 0) scale = 1;

        if (values.Count == 1)
        {
            // 单点画不出线 —— 用同一高度横贯整幅，语义是"只有一个读数，还没有趋势"。
            var y0 = ClampY(values[0], scale, height);
            points.Add((0, y0));
            points.Add((width, y0));
            return points;
        }

        var step = width / (values.Count - 1);
        for (var i = 0; i < values.Count; i++)
            points.Add((i * step, ClampY(values[i], scale, height)));

        return points;
    }

    /// <summary>值 → Y，并夹到 [0, height]（负值或超上限都不画出画面外）。</summary>
    private static double ClampY(double value, double scale, double height)
    {
        var normalized = value / scale;
        if (normalized < 0) normalized = 0;
        if (normalized > 1) normalized = 1;
        return Math.Round(height - normalized * height, 2);
    }
}
