using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace wslcUI.Services;

/// <summary>
/// `wslc images` 的 SIZE 列解析（如 <c>451MB</c> / <c>8.42MB</c> / <c>1.2GB</c>），
/// 以及反向格式化。刻意做成纯函数以便单测——这是"镜像总占用"的唯一数据来源，
/// 算错会让维护页给出误导性数字。
///
/// <para>
/// 实测形态（wslc 3.0.1，zh-CN）：值本身是<b>英文单位、无空格</b>（<c>451MB</c>）；
/// 但 docker 生态的其他来源可能给带空格的写法（<c>187 MB</c>，见 FakeWslcClient），
/// 因此空格一律容忍。单位大小写不敏感，接受 B/KB/MB/GB/TB 与二进制写法
/// （KiB/MiB/GiB/TiB）。十进制与二进制前缀都按 1024 进位——docker 的 SIZE 列
/// 本身就不精确（是 10 进制与 2 进制混用的展示值），这里不去纠结 1000 vs 1024。
/// </para>
/// </summary>
internal static class SizeParser
{
    private static readonly Regex Pattern = new(
        @"^(?<num>\d+(?:\.\d+)?)\s*(?<unit>[KMGTPE]?i?B)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 解析尺寸字符串为字节数。无法识别（空串、<c>N/A</c>、<c>—</c> 等）时返回 false，
    /// 调用方应据此把该条目排除在合计之外而不是当 0——否则合计数会静默偏小。
    /// </summary>
    internal static bool TryParseBytes(string? text, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var m = Pattern.Match(text.Trim());
        if (!m.Success) return false;

        if (!double.TryParse(m.Groups["num"].Value, NumberStyles.Float,
                             CultureInfo.InvariantCulture, out var value))
            return false;

        var unit = m.Groups["unit"].Value.ToUpperInvariant();
        // 去掉二进制前缀的 'i'：KiB → KB，MiB → MB…
        unit = unit.Replace("I", "");
        var exponent = unit switch
        {
            "" or "B" => 0,
            "KB" => 1,
            "MB" => 2,
            "GB" => 3,
            "TB" => 4,
            "PB" => 5,
            "EB" => 6,
            _ => -1,
        };
        if (exponent < 0) return false;

        var scaled = value * Math.Pow(1024, exponent);
        if (scaled > long.MaxValue || scaled < 0) return false;
        bytes = (long)Math.Round(scaled);
        return true;
    }

    /// <summary>人类可读格式化（如 <c>1.4GB</c> / <c>0B</c>），用于卡片展示。</summary>
    internal static string Format(long bytes)
    {
        if (bytes <= 0) return "0B";
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double value = bytes;
        var i = 0;
        while (value >= 1024 && i < units.Length - 1)
        {
            value /= 1024;
            i++;
        }
        return i == 0
            ? $"{bytes}B"
            : value.ToString("0.#", CultureInfo.InvariantCulture) + units[i];
    }
}
