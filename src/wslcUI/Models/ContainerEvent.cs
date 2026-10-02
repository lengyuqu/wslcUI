using System;
using System.Collections.Generic;

namespace wslcUI.Models;

/// <summary>
/// 一条容器事件（<c>wslc events</c> 的一行）。
///
/// <para>
/// 真实行形态（wslc 3.0.1.0 实测，2026-10-02）：
/// </para>
/// <code>
/// 2026-10-02T20:09:46.000000000+08:00 container create f8ef7be7…(image=nginx, maintainer=NGINX Docker Maintainers &lt;docker-maint@nginx.com&gt;, name=wslcui-ev3)
/// </code>
/// <para>
/// 字段：ISO8601 时间戳（**纳秒精度 + 时区偏移**）/ 类别 / 动作 / 对象 ID（64 位十六进制）/
/// 括号内的 <c>key=value</c> 列表。
/// </para>
///
/// <para>
/// <b>纳秒精度的处理（本模型最需要解释的一处）</b>：wslc 输出 9 位小数秒，
/// 而 <see cref="DateTimeOffset"/> 的分辨率只有 100ns（ticks）。实测
/// <c>DateTimeOffset.Parse("2026-10-02T20:09:46.123456789+08:00")</c>
/// **不抛异常但静默截断**到 <c>.1234568</c>——第 8、9 位（<c>789</c>）永久丢失。
/// 于是同一 100ns 内的两条事件（<c>.000000001</c> 与 <c>.000000002</c>）
/// 会得到**完全相同的 <see cref="Timestamp"/>**，排序退化成不稳定比较。
/// </para>
/// <para>
/// 因此把 9 位小数**拆成两半**存：前 7 位进 <see cref="Timestamp"/>（DateTimeOffset 能表达的上限），
/// 余下 2 位（0~99）单独存进 <see cref="SubTickNanoseconds"/>，并由
/// <see cref="SortKey"/> 把两者拼成一个**可比较的 long**，让排序成为全序。
/// 实测 wslc 的时间戳目前实际都落在整秒（<c>.000000000</c>），但格式契约里写了纳秒，
/// 不能赌它永远不出现亚 100ns 的值。
/// </para>
/// </summary>
public class ContainerEvent : IComparable<ContainerEvent>
{
    // ---- 标识字段：构造后不变，auto-prop 即可（照抄 StatInfo.cs 风格）----

    /// <summary>事件时间（**截断到 100ns**）。保留原始时区偏移，<c>UtcDateTime</c> 用于跨偏移排序。</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// 小数秒的第 8~9 位（纳秒余数，0~99）。
    /// <see cref="DateTimeOffset"/> 装不下这 2 位，单独存以保证 <see cref="SortKey"/> 是全序。
    /// </summary>
    public int SubTickNanoseconds { get; set; }

    /// <summary>类别：<c>container</c> / <c>network</c> /（可能的）<c>image</c> / <c>engine</c>。不做白名单校验。</summary>
    public string Category { get; set; } = "";

    /// <summary>动作：<c>create</c> / <c>start</c> / <c>kill</c> / <c>stop</c> / <c>destroy</c> / <c>connect</c> / …</summary>
    public string Action { get; set; } = "";

    /// <summary>对象 ID（64 位十六进制）。</summary>
    public string ObjectId { get; set; } = "";

    /// <summary>对象名（<c>name=</c>）：容器名 / 网络名。缺失时降级为「—」。</summary>
    public string Name { get; set; } = "—";

    /// <summary>镜像（<c>image=</c>）。缺失时降级为「—」。</summary>
    public string Image { get; set; } = "—";

    /// <summary>原始括号内字符串（不含外层括号），如 <c>image=nginx, name=wslcui-ev3</c>。无括号时为空串。</summary>
    public string Details { get; set; } = "";

    /// <summary>
    /// 括号内解析出的全部 <c>key=value</c>（含 <c>maintainer</c> / <c>exitCode</c> /
    /// <c>container</c> / <c>type</c> 等未知也会保留的键）。
    /// </summary>
    public IReadOnlyDictionary<string, string> Fields { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    // ---- 派生显示属性（供 XAML 直接绑定）----

    /// <summary>
    /// 排序用全序键：<see cref="Timestamp"/> 的 **UTC** ticks 放大 100 倍，
    /// 再加上 <see cref="SubTickNanoseconds"/>。用 UTC 而非本地 ticks：不同事件行可能带
    /// 不同偏移（历史回读与实时流混合时尤其），本地 ticks 会让跨偏移的先后关系错掉。
    /// </summary>
    public long SortKey => Timestamp.UtcTicks * 100 + SubTickNanoseconds;

    /// <summary>列表短 ID（<c>ObjectId</c> 前 12 位，docker 风格）。非十六进制或已够短时返回原值。</summary>
    public string ShortId =>
        ObjectId.Length > 12 && IsHex(ObjectId) ? ObjectId[..12] : ObjectId;

    /// <summary>
    /// 退出码（<c>exitCode=</c>）。非纯数字或缺失时为 null
    /// —— <b>不伪造 0</b>（0 与「没有这个字段」语义完全不同，见 AGENTS.md「不许静默当 0」）。
    /// </summary>
    public int? ExitCode =>
        Fields.TryGetValue("exitCode", out var v) && int.TryParse(v, out var n) ? n : null;

    /// <summary>时间列短文本（<c>HH:mm:ss</c>）。跨天由日期列区分。</summary>
    public string TimeText => Timestamp.ToString("HH:mm:ss");

    /// <summary>
    /// 「类别/动作」列文本，如 <c>container/start</c>、<c>network/connect</c>。
    /// 原样用 wslc 的英文词——活动流是排障现场，中译反而要回头对照原始输出。
    /// 中文语义在 <see cref="Summary"/> 那一列。
    /// </summary>
    public string CategoryAction => $"{Category}/{Action}";

    /// <summary>
    /// 一行中文摘要，形如「container 启动了容器 wslcui-ev3」
    /// （停止事件追加退出码：「container 停止了容器 wslcui-ev3（退出码 137）」）。
    /// <para>
    /// <b>未识别的类别/动作退化为原文</b>（如 <c>image pull → "image pull"</c>），
    /// 而不是抛异常或显示空白 —— 活动流是长驻页面，遇到 wslc 新增的事件类型
    /// 不该整页崩掉。
    /// </para>
    /// </summary>
    public string Summary
    {
        get
        {
            var verb = ActionVerb(Category, Action);
            // 没有可读对象名时（如 image pull 早期的事件没有 name=）就只说动作，
            // 拼出「network 未知」这种半截句子反而更难懂。
            var text = verb is null || Name == "—"
                ? $"{Category} {Action}"
                : $"{Category} {verb} {Name}";

            // 停止事件带退出码时补上——这是排查“为什么停了”的第一信息。
            if (Category == "container" && Action == "stop" && ExitCode is { } code)
                text += $"（退出码 {code}）";
            return text;
        }
    }

    /// <summary>
    /// Segoe MDL2 Assets 的 <c>FontIcon.Glyph</c> 字形（<c>\uXXXX</c> 转义而非字面量：
    /// 私有区码位在编辑器/复制粘贴中极易被替换成豆腐块）。码位已对
    /// <c>C:\Windows\Fonts\segmdl2.ttf</c> 的 cmap 表逐一核验存在。
    /// 空串 = 无可用图标，调用方据此隐藏图标列，而不是画一个空白方块。
    /// </summary>
    public string Glyph => (Category, Action) switch
    {
        ("container", "create") => "\uE710",              // Add
        ("container", "start") => "\uE768",               // Play
        ("container", "kill") => "\uE71A",                // Stop
        ("container", "stop") => "\uE71A",                // Stop
        ("container", "destroy") => "\uE74D",             // Delete
        ("network", "connect") => "\uE768",                // Play（接入）
        ("network", "disconnect") => "\uE71A",               // Stop（断开）
        _ => "",
    };

    /// <summary>是否是需要留意的事件（停止 / 销毁 / 断开）。UI 可据此强调着色。</summary>
    public bool IsNoteworthy => Action is "kill" or "stop" or "destroy" or "disconnect";

    /// <summary>
    /// 全序比较：先 <see cref="SortKey"/>；完全相同（同 100ns 且同纳秒余数）时回退到
    /// <see cref="ObjectId"/> 字典序，使相等时刻也有确定次序，避免 <c>List.Sort</c>
    /// 把同刻的「create / start / connect」任意重排。
    /// </summary>
    public int CompareTo(ContainerEvent? other)
    {
        if (other is null) return 1;
        if (ReferenceEquals(this, other)) return 0;
        var c = SortKey.CompareTo(other.SortKey);
        return c != 0 ? c : string.CompareOrdinal(ObjectId, other.ObjectId);
    }

    /// <summary>
    /// 类别+动作 → 中文动作词。<b>未识别返回 null</b>，由 <see cref="Summary"/> 用原文兜底。
    /// 这里刻意<b>不</b>做动作白名单校验：wslc 可能新增事件类型（image/pull/build），
    /// 解析层不该因为不认识动作就丢行。
    /// </summary>
    private static string? ActionVerb(string category, string action) => (category, action) switch
    {
        ("container", "create") => "创建了容器",
        ("container", "start") => "启动了容器",
        ("container", "kill") => "强制终止了容器",
        ("container", "stop") => "停止了容器",
        ("container", "destroy") => "删除了容器",
        ("network", "connect") => "接入了网络",
        ("network", "disconnect") => "断开了网络",
        _ => null,
    };

    private static bool IsHex(string s)
    {
        if (s.Length == 0) return false;
        foreach (var c in s)
            if (!char.IsAsciiHexDigit(c)) return false;
        return true;
    }
}
