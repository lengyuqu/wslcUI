using System;
using System.Collections.Generic;
using System.Globalization;
using wslcUI.Models;

namespace wslcUI.Services;

/// <summary>
/// <c>wslc events</c> 单行输出解析器（<b>纯函数</b>：不碰文件 / 进程 / UI，可直接单测）。
///
/// <para>
/// 输入行形态（wslc 3.0.1.0 真机原文，2026-10-02 抓取）：
/// </para>
/// <code>
/// 2026-10-02T20:09:46.000000000+08:00 container create f8ef7be7…(image=nginx, maintainer=NGINX Docker Maintainers &lt;docker-maint@nginx.com&gt;, name=wslcui-ev3)
/// </code>
/// <para>
/// 结构：<c>&lt;ISO8601 纳秒时间戳&gt; &lt;类别&gt; &lt;动作&gt; &lt;64 位十六进制 ID&gt; (&lt;key=value 列表&gt;)</c>。
/// 类别与动作**不做白名单校验** —— wslc 可能新增 <c>image</c> / <c>pull</c> / <c>build</c> 等事件，
/// 解析层不该因为不认识动作就丢行。
/// </para>
///
/// <para>
/// <b>为什么括号内不能按逗号 split</b>：真实行的 value 里含逗号和空格
/// （<c>maintainer=NGINX Docker Maintainers &lt;docker-maint@nginx.com&gt;</c>），
/// 朴素的 <c>Split(',')</c> 会把它切成两半。这里改为**用正则定位每个
/// <c>key=</c> 的起点**，再把相邻两个 key 之间的原文当作上一个 value ——
/// 于是 value 内部的逗号天然被完整保留。详见 <see cref="SplitFields"/>。
/// </para>
///
/// <para>
/// <b>为什么永不抛异常</b>：事件流是长驻资源，一条脏行（wslc 启动横幅、WSL 未安装的引导文案、
/// 半截的 UTF-8 行）不该把整条流炸掉。无法解析一律返回 <c>null</c>，由调用方丢弃。
/// 这与 <c>WslcCli.ParseStatsJson</c>「坏行只丢该行」的既有约定一致。
/// </para>
/// </summary>
internal static class EventLineParser
{
    /// <summary>
    /// 行长度上限。真机行约 250 字符；留 4 KB 余量后仍然远低于上限，
    /// 超长即视为脏行/日志混入，丢弃而不是继续解析（防御性上限）。
    /// </summary>
    private const int MaxLineLength = 4096;

    /// <summary>
    /// 字段定位用的宽松正则：时间戳 / 类别 / 动作 / ID 后面跟（可选的）括号块。
    /// <list type="bullet">
    ///   <item>时间戳：允许 1~9 位小数秒 + <c>Z</c> 或 <c>±hh:mm</c> 偏移</item>
    ///   <item>类别/动作：非空白词（不校验取值，避免白名单过期）</item>
    ///   <item>ID：十六进制串，1~64 位（不强制 64 —— 网络 ID 与容器 ID 长度可能不同）</item>
    /// </list>
    /// 括号块用 <c>(.*)</c> 贪婪吃到行尾：<c>maintainer</c> 的值里含 <code>&lt;…&gt;</code>
    /// 但不含圆括号，真机未见过嵌套括号；贪婪匹配保证末尾的 <c>)</c> 归到括号块而不是 ID。
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex LineRx = new(
        @"^\s*(?<ts>\S+)\s+(?<cat>\S+)\s+(?<act>\S+)\s+(?<id>[0-9a-fA-F]{1,64})" +
        @"(?:\s*\((?<details>.*)\))?\s*$",
        System.Text.RegularExpressions.RegexOptions.Compiled |
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// 在 <c>key=value</c> 列表里定位每个 <c>key=</c>。
    /// <para>
    /// 只认「<b>行首</b>或<b>逗号后紧跟</b>的标识符等号」，因此 value 内部的逗号不会误切：
    /// <c>maintainer=NGINX Docker Maintainers &lt;docker-maint@nginx.com&gt;, name=x</c> 中
    /// <c>name</c> 前是逗号（是真边界），而 maintainer 值里的空格/尖括号不是。
    /// </para>
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex KeyRx = new(
        @"(?:^|,\s*)(?<key>[A-Za-z_][A-Za-z0-9_.\-]*)\s*=",
        System.Text.RegularExpressions.RegexOptions.Compiled |
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// 解析一行事件输出。<b>解析失败返回 null，绝不抛异常。</b>
    /// </summary>
    /// <param name="line">wslc 的一行原始输出（可含尾部 <c>\r</c>）。</param>
    internal static ContainerEvent? Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        if (line.Length > MaxLineLength) return null;

        var m = LineRx.Match(line);
        if (!m.Success) return null;

        if (!TryParseTimestamp(m.Groups["ts"].Value, out var ts, out var subTickNanos))
            return null;

        var details = m.Groups["details"].Success ? m.Groups["details"].Value : "";
        var fields = SplitFields(details);

        return new ContainerEvent
        {
            Timestamp = ts,
            SubTickNanoseconds = subTickNanos,
            Category = m.Groups["cat"].Value,
            Action = m.Groups["act"].Value,
            ObjectId = m.Groups["id"].Value,
            Name = Get(fields, "name"),
            Image = Get(fields, "image"),
            Details = details,
            Fields = fields,
        };
    }

    /// <summary>
    /// 解析 wslc 的 9 位纳秒时间戳，<b>并把超出 <see cref="DateTimeOffset"/> 分辨率的
    /// 低 2 位单独返回</b>。
    ///
    /// <para>
    /// 实测（.NET 10）：<c>DateTimeOffset.Parse("2026-10-02T20:09:46.000000000+08:00")</c>
    /// <b>不会抛异常</b>，而是静默截断到 100ns；<c>ParseExact(…, "o")</c> 则会
    /// <c>FormatException</c>（9 位小数与 <c>o</c> 格式的 7 位不符）。
    /// 因此这里先手工把小数秒砍到 7 位再交给 BCL 解析，避免依赖未文档化的截断行为。
    /// </para>
    /// <para>
    /// 低 2 位（<c>.000000<b>01</b></c> 里的 <c>01</c>）存进
    /// <see cref="ContainerEvent.SubTickNanoseconds"/>，与
    /// <see cref="ContainerEvent.SortKey"/> 一起构成全序 —— 否则同一 100ns 内的
    /// 两条事件会得到相同的 <c>DateTimeOffset</c>，排序不稳定。
    /// </para>
    /// </summary>
    internal static bool TryParseTimestamp(
        string text, out DateTimeOffset value, out int subTickNanoseconds)
    {
        value = default;
        subTickNanoseconds = 0;
        if (string.IsNullOrEmpty(text)) return false;

        // 拆出小数秒起点：形如 2026-10-02T20:09:46.000000000+08:00
        var dot = text.IndexOf('.');
        if (dot < 0)
        {
            // 允许无小数秒（形如 2026-10-02T20:09:46+08:00）
            return DateTimeOffset.TryParse(
                text, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
        }

        // 小数点后可能直接跟时区（无小数秒的退化写法 xxxxxxxxx+08:00 不会出现，
        // 但防御性地扫到非数字为止）。
        var fracStart = dot + 1;
        var i = fracStart;
        while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
        var frac = text[fracStart..i];
        if (frac.Length == 0) return false;

        // 超过 9 位（超出纳秒）→ 只取前 9 位，其余丢弃：格式契约就是纳秒。
        if (frac.Length > 9) frac = frac[..9];

        // 前 7 位进 DateTimeOffset（补零到 7 位，兼容 .5 / .12345 等短小数）。
        var head = frac[..Math.Min(7, frac.Length)].PadRight(7, '0');
        // 低 2 位（不足 2 位补 0），即第 8~9 位。
        var tail = frac.Length > 7 ? frac[7..] : frac[Math.Min(7, frac.Length)..];
        subTickNanoseconds = tail.Length > 0 ? int.Parse(tail.PadRight(2, '0'), CultureInfo.InvariantCulture) : 0;

        // 重组「7 位小数 + 原时区偏移」交给 BCL，并显式指定不变文化。
        var rebuilt = string.Concat(
            text.AsSpan(0, dot + 1), head.AsSpan(), text.AsSpan(i));
        return DateTimeOffset.TryParse(
            rebuilt, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    /// <summary>
    /// 把括号内的 <c>key=value</c> 列表切成字典。
    /// <para>
    /// <b>不按逗号 split</b>：真实 value 里含逗号与空格
    /// （<c>maintainer=NGINX Docker Maintainers &lt;docker-maint@nginx.com&gt;</c>）。
    /// 做法是用 <see cref="KeyRx"/> 找出每个 key 的<b>起点</b>，再取「本 key 的等号之后
    /// 到下一个 key 起点之前」的原文作为 value。value 内部的逗号因此被完整保留。
    /// </para>
    /// <para>
    /// 边界退化：没有 <c>=</c> 的整段（脏行）返回空字典而不是抛；
    /// 重复 key 后者覆盖前者（与多数 key=value 解析器一致）。
    /// </para>
    /// <para>
    /// 已知取舍（格式本身无法消歧，非本实现缺陷）：
    /// ① value 里若出现「逗号 + 空格 + <c>标识=</c>」（如 <c>a=1, b=2</c> 形态）会被切成两个键；
    /// ② 末尾 value 若<b>本身以逗号结尾</b>（<c>desc=hello,</c>），那个逗号会被当作分隔符去掉。
    /// 真机 wslc 输出的 value 未出现过这两种形态（maintainer/image/name/exitCode/container/type
    /// 均不以逗号结尾，也不会内嵌 <c>x=y</c> 片段）。
    /// </para>
    /// </summary>
    internal static IReadOnlyDictionary<string, string> SplitFields(string? details)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(details)) return result;

        // 摘掉尾随分隔符（", " / "，"）。只需处理**末尾**：中间键的右边界落在下一个
        // match 的起点（那个逗号）上，本来就干净，只有末位需要这一步。
        var body = details.TrimEnd().TrimEnd(',', '，').TrimEnd();

        var matches = KeyRx.Matches(body);
        for (var n = 0; n < matches.Count; n++)
        {
            var key = matches[n].Groups["key"].Value;
            if (key.Length == 0) continue;

            // value 从本 match 的末尾（等号之后）开始，到**下一个 match 的起点**为止。
            // 关键：<see cref="KeyRx"/> 的分隔符（<c>,</c> + 空白）是 match 的**一部分**，
            // 所以 match 起点恰好落在那个逗号上 —— 用它作右边界天然把 ", " 排除在外。
            // （若误用下一 match 的 *key 组* 起点，逗号会留在 value 尾巴上，
            //   <c>image=nginx, name=web</c> 就会解析出 <c>"nginx,"</c>。实测踩过。）
            var valueStart = matches[n].Index + matches[n].Length;
            var valueEnd = n + 1 < matches.Count
                ? matches[n + 1].Index
                : body.Length;

            if (valueEnd < valueStart) continue;
            var value = body[valueStart..valueEnd].Trim();
            if (value.Length == 0) continue;
            result[key] = value;
        }
        return result;
    }

    /// <summary>取字段值，缺失/空值统一降级为「—」（与本项目其他模型的降级约定一致）。</summary>
    private static string Get(IReadOnlyDictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : "—";
}
