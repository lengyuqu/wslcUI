using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace wslcUI.Services;

/// <summary>
/// 把 CLI 里"给人看的字符串"折算成可比较的数值，供列表按列排序使用。
///
/// <para>
/// 为什么不能只取前导数字（<c>MainViewModel</c> 里旧 <c>LeadingNumber</c> 的做法）：
/// 真机 wslc 的这些列全都带单位，而<b>单位决定量级</b>：
/// </para>
/// <list type="bullet">
///   <item><description>已创建（zh-CN 3.0.1 实测）：<c>2 周前</c> / <c>3 个月前</c> / <c>5 分钟前</c>。
///   取前导数字会得到 3 &gt; 2，于是"3 个月前"排在"2 周前"后面 —— 正好反了。</description></item>
///   <item><description>镜像 SIZE / 内存：<c>4.45MB</c> 与 <c>1.5GB</c>，取前导数字得 4.45 &gt; 1.5，
///   同样是反的。</description></item>
/// </list>
/// <para>统一折成字节 / 秒之后才可比。无法识别的一律返回同一个极小值，
/// 让它们稳定聚在一端，而不是散落在数值中间。</para>
/// </summary>
internal static class NaturalSortKey
{
    /// <summary>不可比较的值统一落到这里，保证排序结果稳定（升序时排在最前）。</summary>
    internal const double Unrankable = double.MinValue;

    /// <summary>
    /// 数字 + 单位词 + 可选的「前 / ago」。<b>交替顺序必须长的在前</b>
    /// （<c>个月</c> 要先于 <c>月</c>、<c>minutes</c> 要先于 <c>min</c>），
    /// 否则 "3 个月前" 只会匹配到 "月"、把 "前" 当作非法尾巴而整体失配。
    /// </summary>
    private static readonly Regex Duration = new(
        @"^(?<n>\d+(?:\.\d+)?)\s*(?<unit>毫秒|秒钟|秒|分钟|分|小时|天|周|个月|月|年" +
        @"|milliseconds?|millis?|ms|seconds?|secs?|minutes?|mins?|hours?|hrs?|days?|weeks?|wks?|months?|years?|yrs?)" +
        @"(?:\s*(?:前|ago))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>把一列的显示文本折成可排序的数值；不可识别返回 <see cref="Unrankable"/>。</summary>
    internal static double Rank(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Unrankable;
        var s = text.Trim();

        // ① 时长（创建时间列）。折成秒。
        var m = Duration.Match(s);
        if (m.Success &&
            double.TryParse(m.Groups["n"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) &&
            UnitSeconds(m.Groups["unit"].Value, out var factor))
            return amount * factor;

        // ② 字节（SIZE / 内存 / 块 IO）。"0B / 3.86GiB" 这类"用量 / 限制"取前一半（用量）。
        if (SizeParser.TryParseBytes(s, out var bytes)) return bytes;
        var slash = s.IndexOf('/');
        if (slash > 0 && SizeParser.TryParseBytes(s[..slash].Trim(), out bytes)) return bytes;

        // ③ 纯数值（"12.3%"、"850"）。
        return LeadingNumber(s);
    }

    /// <summary>单位词 → 秒数。未收录的单位返回 false（宁可不可比，也不猜量纲）。</summary>
    private static bool UnitSeconds(string unit, out double seconds)
    {
        // 大小写不敏感（RegexOptions.IgnoreCase 只管匹配，取值仍可能是 "MIN"/"Min"）。
        switch (unit.ToLowerInvariant())
        {
            case "毫秒": seconds = 0.001; return true;
            case "秒钟": seconds = 1; return true;
            case "秒": seconds = 1; return true;
            case "分钟": seconds = 60; return true;
            case "分": seconds = 60; return true;
            case "小时": seconds = 3600; return true;
            case "天": seconds = 86400; return true;
            case "周": seconds = 604800; return true;
            case "个月": seconds = 2592000; return true;   // 30 天，与 docker humanize 的近似一致
            case "月": seconds = 2592000; return true;
            case "年": seconds = 31536000; return true;    // 365 天

            case var u when u.StartsWith("millis") || u == "ms": seconds = 0.001; return true;
            case var u when u.StartsWith("second") || u.StartsWith("sec") || u == "s": seconds = 1; return true;
            case var u when u.StartsWith("minute") || u.StartsWith("min") || u == "m": seconds = 60; return true;
            case var u when u.StartsWith("hour") || u.StartsWith("hr") || u == "h": seconds = 3600; return true;
            case var u when u.StartsWith("day") || u == "d": seconds = 86400; return true;
            case var u when u.StartsWith("week") || u.StartsWith("wk") || u == "w": seconds = 604800; return true;
            case var u when u.StartsWith("month") || u.StartsWith("mon"): seconds = 2592000; return true;
            case var u when u.StartsWith("year") || u.StartsWith("yr") || u == "y": seconds = 31536000; return true;

            default: seconds = 0; return false;
        }
    }

    /// <summary>取字符串开头的那段数字（含一个小数点）。没有数字返回 <see cref="Unrankable"/>。</summary>
    private static double LeadingNumber(string s)
    {
        var i = 0;
        while (i < s.Length && !char.IsDigit(s[i]) && s[i] != '.') i++;
        if (i >= s.Length) return Unrankable;
        var j = i;
        var seenDot = false;
        while (j < s.Length && (char.IsDigit(s[j]) || (s[j] == '.' && !seenDot && (seenDot = true)))) j++;
        return double.TryParse(s[i..j], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v : Unrankable;
    }
}
