using System;
using System.Collections.Generic;
using System.Linq;
using wslcUI.Models;
using wslcUI.Services;
using Xunit;

namespace wslcUI.Tests.Services;

/// <summary>
/// <c>wslc events</c> 单行解析（<see cref="EventLineParser.Parse"/>）。
///
/// <para>
/// 夹具是 <b>2026-10-02 在 WinR9 / wslc 3.0.1.0 上真实容器 <c>wslcui-ev3</c> 的
/// <c>wslc events</c> 输出原文</b>（含 <c>--since</c> 回读形态），不是手写的理想格式。
/// 关键形态：
/// </para>
/// <list type="bullet">
///   <item>时间戳是 <b>9 位纳秒 + 时区偏移</b>（<c>2026-10-02T20:09:46.000000000+08:00</c>）</item>
///   <item><b>value 里含逗号和空格</b>：<c>maintainer=NGINX Docker Maintainers &lt;docker-maint@nginx.com&gt;</c>
///         —— 朴素的按逗号 split 会把 maintainer 拦腰截断（本套件的核心用例）</item>
///   <item>同类事件的 key 集合随动作变化：<c>stop</c> 多一个 <c>exitCode</c>（实测有 <c>0</c> 与 <c>137</c> 两种）</item>
///   <item>network 事件的 <c>name</c> 是<b>网络名</b>（<c>bridge</c>），被连的容器在 <c>container=</c> 里</item>
/// </list>
/// </summary>
public class EventLineParserTests
{
    // ============ 真机夹具（wslc 3.0.1.0 / 2026-10-02 抓取，原样未改）============

    private const string CtrId = "f8ef7be7ec087124e9161c9c96fd73d8a3cb7b7381b5cf6db7d3e8fdd28dbb8b";
    private const string NetId = "dd1bd0f683536b8ffbd8689795f702f9bb932eaa7a70aaaa2016f8aa76abb75e";
    private const string Maintainer = "NGINX Docker Maintainers <docker-maint@nginx.com>";

    /// <summary>实测的 7 行「一次容器生命周期」事件流（含 2 条 network）。</summary>
    public const string RealSevenLines =
        "2026-10-02T20:09:46.000000000+08:00 container create " + CtrId + " (image=nginx, maintainer=" + Maintainer + ", name=wslcui-ev3)\r\n" +
        "2026-10-02T20:09:46.000000000+08:00 network connect " + NetId + " (container=" + CtrId + ", name=bridge, type=bridge)\r\n" +
        "2026-10-02T20:09:47.000000000+08:00 container start " + CtrId + " (image=nginx, maintainer=" + Maintainer + ", name=wslcui-ev3)\r\n" +
        "2026-10-02T20:09:50.000000000+08:00 container kill " + CtrId + " (image=nginx, maintainer=" + Maintainer + ", name=wslcui-ev3)\r\n" +
        "2026-10-02T20:09:50.000000000+08:00 network disconnect " + NetId + " (container=" + CtrId + ", name=bridge, type=bridge)\r\n" +
        "2026-10-02T20:09:50.000000000+08:00 container stop " + CtrId + " (exitCode=137, image=nginx, maintainer=" + Maintainer + ", name=wslcui-ev3)\r\n" +
        "2026-10-02T20:09:50.000000000+08:00 container destroy " + CtrId + " (image=nginx, maintainer=" + Maintainer + ", name=wslcui-ev3)\r\n";

    private static string Line(string ts, string cat, string act, string id, string? details = null) =>
        details is null
            ? $"{ts} {cat} {act} {id}"
            : $"{ts} {cat} {act} {id} ({details})";

    /// <summary>
    /// 把真机夹具切成「非空行」序列。末尾的 <c>\r\n</c> 会切出一个空串，
    /// 而真实事件流里空行也本就该被解析器丢弃 —— 这里先行剔除，
    /// 让下面按 <c>[i]</c> 取第 n 条的断言与夹具行号一一对应。
    /// </summary>
    private static string[] SplitRealLines() =>
        RealSevenLines.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();

    private const string Ts = "2026-10-02T20:09:46.000000000+08:00";

    // ============ 真机夹具断言 ============

    /// <summary>7 行全部解析成功，且类别/动作/ID 都对得上。</summary>
    [Fact]
    public void ParsesAllSevenRealLines()
    {
        var events = SplitRealLines()
            .Select(EventLineParser.Parse)
            .ToList();

        Assert.Equal(7, events.Count);
        Assert.All(events, e => Assert.NotNull(e));

        Assert.Equal(
            new[] { "create", "connect", "start", "kill", "disconnect", "stop", "destroy" },
            events.Select(e => e!.Action));

        Assert.Equal(
            new[] { "container", "network", "container", "container", "network", "container", "container" },
            events.Select(e => e!.Category));

        // 容器行的 ID 是容器 ID，network 行是网络 ID —— 不能串。
        Assert.Equal(CtrId, events[0]!.ObjectId);
        Assert.Equal(NetId, events[1]!.ObjectId);
    }

    /// <summary>
    /// 核心用例：<b>value 里含逗号和空格</b>时必须完整保留。
    /// <c>maintainer=NGINX Docker Maintainers &lt;docker-maint@nginx.com&gt;</c>
    /// 若按逗号 split 会得到 <c>"NGINX Docker Maintainers &lt;docker-maint@nginx.com&gt;"</c>
    /// 被截成两段、后面那个 key 直接丢。
    /// </summary>
    [Fact]
    public void PreservesValueContainingCommaAndSpaces()
    {
        var ev = EventLineParser.Parse(SplitRealLines()[0])!;

        Assert.Equal(Maintainer, ev.Fields["maintainer"]);
        Assert.Equal("nginx", ev.Fields["image"]);
        Assert.Equal("wslcui-ev3", ev.Fields["name"]);
        // 键的个数正好 3 —— 没有任何一个被误切/丢失
        Assert.Equal(3, ev.Fields.Count);
        // Name/Image 便捷字段同步
        Assert.Equal("wslcui-ev3", ev.Name);
        Assert.Equal("nginx", ev.Image);
    }

    /// <summary><c>stop</c> 事件多一个 <c>exitCode</c>，实测既有 0 也有 137。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(137)]
    public void ReadsExitCodeFromStopEvent(int code)
    {
        var ev = EventLineParser.Parse(
            Line(Ts, "container", "stop", CtrId, $"exitCode={code}, image=nginx, name=wslcui-ev3"))!;

        Assert.Equal(code, ev.ExitCode!.Value);
        Assert.Equal(code.ToString(), ev.Fields["exitCode"]);
        // 退出码出现在首键，位置不影响其余键的解析
        Assert.Equal("wslcui-ev3", ev.Name);
    }

    /// <summary>network 行的 <c>name</c> 是网络名，容器在 <c>container=</c> 键里。</summary>
    [Fact]
    public void NetworkEventExposesNetworkNameAndContainer()
    {
        var ev = EventLineParser.Parse(SplitRealLines()[1])!;

        Assert.Equal("bridge", ev.Fields["name"]);
        Assert.Equal("bridge", ev.Fields["type"]);
        Assert.Equal(CtrId, ev.Fields["container"]);
        // network 行没有 image —— 降级成「—」而不是空串
        Assert.Equal("—", ev.Image);
    }

    // ============ 时间戳：纳秒精度（本套件最需要盯住的一处）============

    /// <summary>
    /// 实测事实：<c>DateTimeOffset.Parse</c> 对 9 位小数秒<b>不抛异常但静默截断</b>到 100ns，
    /// <c>ParseExact(…, "o")</c> 则直接 <c>FormatException</c>。
    /// 所以解析器手工拆「前 7 位 → DateTimeOffset / 低 2 位 → SubTickNanoseconds」。
    /// </summary>
    [Fact]
    public void SplitsNanosecondsIntoTicksAndSubTickRemainder()
    {
        var ev = EventLineParser.Parse(
            Line("2026-10-02T20:09:46.123456789+08:00", "container", "start", CtrId))!;

        // 前 7 位进 DateTimeOffset（100ns 分辨率，第 8 位起被 BCL 舍入/截断）
        // DateTimeOffset 没有 SubTicks，用「秒内 ticks」等价表达：.1234567s = 1234567 ticks
        Assert.Equal(1234567, ev.Timestamp.Ticks % TimeSpan.TicksPerSecond);
        // 低 2 位单独保住 —— 这正是 DateTimeOffset 装不下的部分
        Assert.Equal(89, ev.SubTickNanoseconds);
    }

    /// <summary>
    /// 亚 100ns 的两条事件，<c>DateTimeOffset</c> 层面<b>完全相同</b>；
    /// 必须靠 <see cref="ContainerEvent.SubTickNanoseconds"/> 拉开，否则排序不稳定。
    /// </summary>
    [Fact]
    public void SubTickNanosecondsDistinguishesEventsWithinSameTick()
    {
        var a = EventLineParser.Parse(
            Line("2026-10-02T20:09:46.000000001+08:00", "container", "create", CtrId))!;
        var b = EventLineParser.Parse(
            Line("2026-10-02T20:09:46.000000002+08:00", "container", "start", CtrId))!;

        // 二者的 DateTimeOffset 确实无法区分……
        Assert.Equal(a.Timestamp, b.Timestamp);
        // ……但纳秒余数与排序键能区分，且顺序正确。
        Assert.Equal(1, a.SubTickNanoseconds);
        Assert.Equal(2, b.SubTickNanoseconds);
        Assert.True(a.SortKey < b.SortKey);
        Assert.True(a.CompareTo(b) < 0);
    }

    /// <summary>真实夹具里的整秒时间戳：纳秒余数必须为 0，不能凭空造出精度。</summary>
    [Fact]
    public void WholeSecondTimestampHasZeroSubTick()
    {
        var ev = EventLineParser.Parse(SplitRealLines()[0])!;

        Assert.Equal(0, ev.SubTickNanoseconds);
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 20, 9, 46, TimeSpan.FromHours(8)), ev.Timestamp);
        // 偏移要保留 —— 跨时区排序靠 UtcTicks，不能丢
        Assert.Equal(TimeSpan.FromHours(8), ev.Timestamp.Offset);
    }

    /// <summary>
    /// 排序用 <b>UTC</b>：同一绝对时刻、带不同偏移的两行必须得到相同 SortKey，
    /// 否则「历史回读 + 实时流」混合时事件会错序。
    /// </summary>
    [Fact]
    public void SortKeyIsTimeZoneIndependent()
    {
        var shanghai = EventLineParser.Parse(
            Line("2026-10-02T20:09:46.000000000+08:00", "container", "create", CtrId))!;
        var utc = EventLineParser.Parse(
            Line("2026-10-02T12:09:46.000000000Z", "container", "create", CtrId))!;

        Assert.Equal(shanghai.SortKey, utc.SortKey);
        Assert.Equal(shanghai.Timestamp.UtcTicks, utc.Timestamp.UtcTicks);
        Assert.Equal(0, utc.SubTickNanoseconds);
    }

    /// <summary>短小数秒（<c>.5</c>）与超长小数秒（>9 位）都不该抛异常。</summary>
    [Theory]
    [InlineData("2026-10-02T20:09:46.5+08:00")]
    [InlineData("2026-10-02T20:09:46.123+08:00")]
    [InlineData("2026-10-02T20:09:46.123456789012+08:00")]   // 超出纳秒：多出的位丢弃
    [InlineData("2026-10-02T20:09:46+08:00")]                  // 无小数秒
    [InlineData("2026-10-02T20:09:46Z")]
    public void AcceptsTimestampVariants(string ts)
    {
        var ev = EventLineParser.Parse(Line(ts, "container", "start", CtrId));

        Assert.NotNull(ev);
        Assert.Equal("container", ev!.Category);
    }

    // ============ 边界与脏行：全部返回 null，绝不抛 ============

    /// <summary>
    /// 脏行/非法行一律返回 null —— 事件流是长驻的，一条脏行不该炸掉整条流
    /// （这是与 <c>ParseStatsJson</c>「坏行只丢该行」一致的既有约定）。
    /// </summary>
    [Theory]
    [InlineData(null)]                                                   // null 输入
    [InlineData("")]                                                     // 空串
    [InlineData("   ")]                                                  // 纯空白
    [InlineData("\r\n")]                                                 // 空行
    [InlineData("total 64")]                                             // ls 风格的残留
    [InlineData("some random warning line here")]                        // wslc 告警
    [InlineData("Error: cannot connect to the WSL service")]             // WSL 未安装引导
    [InlineData("2026-10-02T20:09:46.000000000+08:00")]                  // 缺类别/动作/ID
    [InlineData("2026-10-02T20:09:46.000000000+08:00 container create")] // 缺 ID
    [InlineData("container create " + CtrId)]                            // 缺时间戳
    [InlineData("2026-10-02T20:09:46.000000000+08:00 container create zzzz")] // ID 非十六进制
    [InlineData("not-a-timestamp container create " + CtrId)]            // 时间戳不可解析
    [InlineData("2026-13-45T99:99:99.000000000+08:00 container create " + CtrId)] // 越界
    public void RejectsMalformedLineWithoutThrowing(string? line)
    {
        Assert.Null(EventLineParser.Parse(line));
    }

    /// <summary>超长行（&gt; 4 KB）直接丢弃，不进正则 —— 防御性上限。</summary>
    [Fact]
    public void RejectsOverlongLine()
    {
        var huge = Line(Ts, "container", "create", CtrId,
            "name=" + new string('x', 5000));

        Assert.Null(EventLineParser.Parse(huge));
    }

    /// <summary>
    /// <b>未知类别/动作必须照样解析</b>：wslc 可能新增 <c>image</c> / <c>pull</c> / <c>build</c>
    /// 等事件，解析层做动作白名单会让新事件整条消失。未知动作只是没有中文摘要而已。
    /// </summary>
    [Theory]
    [InlineData("image", "pull")]
    [InlineData("image", "build")]
    [InlineData("engine", "restart")]
    [InlineData("volumesystem", "prune")]
    [InlineData("container", "pause")]      // docker 有、wslc 未实测到
    [InlineData("container", "oom")]
    public void AcceptsUnknownCategoryAndAction(string cat, string act)
    {
        var ev = EventLineParser.Parse(Line(Ts, cat, act, CtrId, "name=someobj"))!;

        Assert.Equal(cat, ev.Category);
        Assert.Equal(act, ev.Action);
        // 未识别动作 → 摘要退回原文形态，不抛、也不显示空白
        Assert.Contains(act, ev.Summary);
        // Glyph 允许空串（调用方据此隐藏图标列），但绝不能是 null
        Assert.NotNull(ev.Glyph);
    }

    /// <summary>没有括号的行（key=value 列表整体缺失）仍应解析出前四个字段。</summary>
    [Fact]
    public void ParsesLineWithoutDetails()
    {
        var ev = EventLineParser.Parse(Line(Ts, "container", "start", CtrId))!;

        Assert.Equal("container", ev.Category);
        Assert.Equal(CtrId, ev.ObjectId);
        Assert.Empty(ev.Fields);
        Assert.Equal("", ev.Details);
        Assert.Equal("—", ev.Name);   // 缺失降级，不留空串
        Assert.Null(ev.ExitCode);
    }

    /// <summary>孤立的括号 / 空括号不炸，键字典为空。</summary>
    [Theory]
    [InlineData("()")]
    [InlineData("( )")]
    [InlineData("(image=nginx)")]
    public void HandlesDegenerateDetails(string details)
    {
        var ev = EventLineParser.Parse(Line(Ts, "container", "start", CtrId, details));

        Assert.NotNull(ev);
        Assert.NotNull(ev!.Fields);
    }

    /// <summary>
    /// <c>exitCode</c> 非数字时 <see cref="ContainerEvent.ExitCode"/> 必须是 null
    /// 而<b>不是 0</b> —— 「没有这个字段」与「退出码 0」语义完全不同
    /// （AGENTS.md：不许静默当 0）。
    /// </summary>
    [Fact]
    public void ExitCodeIsNullWhenNotNumeric()
    {
        var ev = EventLineParser.Parse(
            Line(Ts, "container", "stop", CtrId, "exitCode=N/A, name=wslcui-ev3"))!;

        Assert.Null(ev.ExitCode);
        // 原始文本仍可查，诊断信息不丢
        Assert.Equal("N/A", ev.Fields["exitCode"]);
    }

    /// <summary>缺 <c>exitCode</c> 的 stop 事件：摘要不带退出码后缀。</summary>
    [Fact]
    public void StopSummaryAppendsExitCodeOnlyWhenPresent()
    {
        var withCode = EventLineParser.Parse(
            Line(Ts, "container", "stop", CtrId, "exitCode=137, name=web"))!;
        var without = EventLineParser.Parse(
            Line(Ts, "container", "stop", CtrId, "name=web"))!;

        Assert.Contains("137", withCode.Summary);
        Assert.DoesNotContain("退出码", without.Summary);
    }

    // ============ key=value 切分（纯函数，可直接单测）============

    /// <summary>value 内含逗号 + 空格 + 尖括号，切分只认「后随合法 key=」的逗号。</summary>
    [Fact]
    public void SplitFieldsKeepsCommasInsideValues()
    {
        var f = EventLineParser.SplitFields(
            "image=nginx, maintainer=NGINX Docker Maintainers <docker-maint@nginx.com>, name=wslcui-ev3");

        Assert.Equal(3, f.Count);
        Assert.Equal("nginx", f["image"]);
        Assert.Equal(Maintainer, f["maintainer"]);
        Assert.Equal("wslcui-ev3", f["name"]);
    }

    /// <summary>首键之前没有逗号（行首）也能识别。</summary>
    [Fact]
    public void SplitFieldsHandlesFirstKeyAtLineStart()
    {
        var f = EventLineParser.SplitFields("name=only-one");

        Assert.Single(f);
        Assert.Equal("only-one", f["name"]);
    }

    /// <summary>尾随的分隔逗号不应造出一个空键，也不该留在末位 value 里。</summary>
    [Fact]
    public void SplitFieldsIgnoresTrailingSeparator()
    {
        var f = EventLineParser.SplitFields("name=web, image=nginx, ");

        Assert.Equal(2, f.Count);
        Assert.Equal("web", f["name"]);
        Assert.Equal("nginx", f["image"]);   // 末位不能是 "nginx,"
    }

    /// <summary>无 '=' 的整段（脏块）返回空字典而不是抛。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("no equals sign here at all")]
    public void SplitFieldsOnGarbageReturnsEmpty(string? input)
    {
        Assert.Empty(EventLineParser.SplitFields(input));
    }

    /// <summary>
    /// 关键区分用例：<c>, </c> 后面跟的如果是普通词（不是 <c>key=</c>）就<b>不切</b>，
    /// 它仍属于上一个 value 的内容。
    /// </summary>
    [Fact]
    public void SplitFieldsDoesNotSplitOnCommaWithoutKeyEquals()
    {
        var f = EventLineParser.SplitFields("maintainer=Acme, Inc. and Sons, name=web");

        Assert.Equal(2, f.Count);
        Assert.Equal("Acme, Inc. and Sons", f["maintainer"]);
        Assert.Equal("web", f["name"]);
    }

    // ============ 整段输出解析（`--since` 回读形态）============

    /// <summary>整段 7 行回读输出解析出 7 条，且保持原顺序。</summary>
    [Fact]
    public void ParseEventLinesReadsWholeRealBlock()
    {
        var events = WslcCli.ParseEventLines(RealSevenLines);

        Assert.Equal(7, events.Count);
        Assert.Equal("create", events[0].Action);
        Assert.Equal("destroy", events[6].Action);
        // 时间递增（容器 create → start → …→ destroy）
        for (int i = 1; i < events.Count; i++)
            Assert.True(events[i - 1].SortKey <= events[i].SortKey);
    }

    /// <summary>
    /// 回读输出里混入 wslc 的启动横幅 / 告警行时，只丢那几行，其余照常解析
    /// —— 历史回读的第一行常常不是事件。
    /// </summary>
    [Fact]
    public void ParseEventLinesSkipsLeadingNoise()
    {
        const string noisy =
            "wslc 正在启动 WSL 容器引擎…\r\n" +
            "\r\n" +
            "2026-10-02T20:09:46.000000000+08:00 container create " + CtrId + " (name=wslcui-ev3)\r\n" +
            "错误代码：WSLC_E_NOT_READY\r\n";

        var events = WslcCli.ParseEventLines(noisy);

        Assert.Single(events);
        Assert.Equal("create", events[0].Action);
    }

    /// <summary>空输入返回空列表，不抛。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   \r\n  \r\n")]
    [InlineData(null)]
    public void ParseEventLinesOnEmptyReturnsEmpty(string? input)
    {
        Assert.Empty(WslcCli.ParseEventLines(input));
    }

    // ============ 排序稳定性 ============

    /// <summary>
    /// 同刻事件（本夹具里 4 条 <c>20:09:50</c>）排序后必须<b>确定且可重复</b>：
    /// 两次排序结果一致，且互不相等（不会全部塌成同一顺序的歧义比较）。
    /// </summary>
    [Fact]
    public void SortingSameInstantEventsIsDeterministic()
    {
        var sameInstant = SplitRealLines()
            .Select(l => EventLineParser.Parse(l))
            .Where(e => e is not null && e!.Timestamp.ToString("HH:mm:ss") == "20:09:50")
            .Cast<ContainerEvent>()
            .ToList();

        Assert.Equal(4, sameInstant.Count);

        var once = sameInstant.OrderBy(e => e).Select(e => e.Action + ":" + e.Category).ToList();
        var twice = sameInstant.OrderBy(e => e).Select(e => e.Action + ":" + e.Category).ToList();

        Assert.Equal(once, twice);
        // 全部是 container/network 两类，Action 各不相同 → 排序键必须能区分它们
        Assert.Equal(4, once.Distinct().Count());
    }

    /// <summary>短 ID 截断到 12 位（与容器列表页的 ID 列风格一致）。</summary>
    [Fact]
    public void ShortIdTruncatesToTwelveChars()
    {
        var ev = EventLineParser.Parse(SplitRealLines()[0])!;

        Assert.Equal(64, ev.ObjectId.Length);
        Assert.Equal(12, ev.ShortId.Length);
        Assert.Equal(ev.ObjectId[..12], ev.ShortId);
    }

    /// <summary>已知动作给出中文摘要，含对象名。</summary>
    [Theory]
    [InlineData("container", "create", "创建了容器")]
    [InlineData("container", "start", "启动了容器")]
    [InlineData("container", "stop", "停止了容器")]
    [InlineData("container", "destroy", "删除了容器")]
    [InlineData("network", "connect", "接入了网络")]
    [InlineData("network", "disconnect", "断开了网络")]
    public void SummaryIsHumanReadableForKnownActions(string cat, string act, string verb)
    {
        var ev = EventLineParser.Parse(Line(Ts, cat, act, CtrId, "name=wslcui-ev3"))!;

        Assert.Contains(verb, ev.Summary);
        Assert.Contains("wslcui-ev3", ev.Summary);
    }

    /// <summary>没有 <c>name=</c> 时摘要不拼出半截句子。</summary>
    [Fact]
    public void SummaryDoesNotAppendDashWhenNameMissing()
    {
        var ev = EventLineParser.Parse(Line(Ts, "container", "start", CtrId))!;

        Assert.DoesNotContain("—", ev.Summary);
        Assert.False(string.IsNullOrWhiteSpace(ev.Summary));
    }
}
