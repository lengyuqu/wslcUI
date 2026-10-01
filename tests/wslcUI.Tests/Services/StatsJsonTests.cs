using System.Collections.Generic;
using System.Linq;
using wslcUI.Services;
using Xunit;

namespace wslcUI.Tests.Services;

/// <summary>
/// `wslc stats -a --format json` 解析（<see cref="WslcCli.ParseStatsJson"/>）+
/// 百分数解析（<see cref="WslcCli.TryParsePercent"/>）。
///
/// 夹具是 **2026-10-02 在 WinR9 / wslc 3.0.1.0 上跑两个容器实测抓到的原文**。
/// 关键形态：**NDJSON，一行一个容器**（单容器时看起来像"单对象"，这正是本仓库早期
/// 误判"stats 是单对象"的由来）。
/// </summary>
public class StatsJsonTests
{
    private const string TwoContainerNdjson =
        "{\"BlockIO\":\"1.72MB / 0B\",\"CPUPerc\":\"0.00%\",\"ID\":\"0c9c6a6dc26f03f2ee86bbbd633c8bf637436a95b4633e474ef994d3ead1b916\",\"MemPerc\":\"0.01%\",\"MemUsage\":\"2.715MiB / 30.96GiB\",\"Name\":\"wslcui-probe2\",\"NetIO\":\"1.18kB / 0B\",\"PIDs\":1}\n" +
        "{\"BlockIO\":\"0B / 0B\",\"CPUPerc\":\"0.00%\",\"ID\":\"20f2ed8f095c770e1086a935c2d7d2bae46ab8e15634adf35ba19b1ec8f3f359\",\"MemPerc\":\"0.00%\",\"MemUsage\":\"0B / 0B\",\"Name\":\"wslc-pg\",\"NetIO\":\"0B / 0B\",\"PIDs\":0}\n";

    [Fact]
    public void ParsesNdjsonOneObjectPerContainer()
    {
        var stats = WslcCli.ParseStatsJson(TwoContainerNdjson);

        Assert.Equal(2, stats.Count);
        Assert.Equal("wslcui-probe2", stats[0].Container);
        Assert.Equal("wslc-pg", stats[1].Container);
    }

    [Fact]
    public void FillsDisplayStringsAndNumbers()
    {
        var first = WslcCli.ParseStatsJson(TwoContainerNdjson)[0];

        // 给人看的字符串（与表格路径同形）
        Assert.Equal("0.00%", first.Cpu);
        Assert.Equal("2.715MiB / 30.96GiB", first.Mem);
        Assert.Equal("0.01%", first.MemPercent);
        Assert.Equal("1.18kB / 0B", first.NetIo);
        Assert.Equal("1.72MB / 0B", first.BlockIo);
        Assert.Equal("1", first.Pids);

        // 数值（曲线用）。2.715MiB = 2846883.84 → 2846884
        Assert.True(first.HasNumbers);
        Assert.Equal(0d, first.CpuPercent, 3);
        Assert.Equal(2846884L, first.MemUsedBytes);
    }

    /// <summary>已停止的容器在 `-a` 里是真实读数（全 0），不是解析失败 —— 必须保留。</summary>
    [Fact]
    public void ZeroReadingContainerIsKept()
    {
        var stopped = WslcCli.ParseStatsJson(TwoContainerNdjson)[1];

        Assert.Equal("0B / 0B", stopped.Mem);
        Assert.Equal(0L, stopped.MemUsedBytes);
        Assert.True(stopped.HasNumbers);
    }

    [Theory]
    [InlineData("2.20%", 2.20)]
    [InlineData("0.00%", 0.0)]
    [InlineData("100%", 100.0)]
    [InlineData("5", 5.0)]
    [InlineData(" 12.5 % ", 12.5)]
    public void ParsesPercentText(string text, double expected)
    {
        Assert.True(WslcCli.TryParsePercent(text, out var value));
        Assert.Equal(expected, value, 3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("%")]
    [InlineData("abc")]
    [InlineData("N/A")]
    public void RejectsNonNumericPercent(string? text)
    {
        Assert.False(WslcCli.TryParsePercent(text, out _));
    }

    /// <summary>数组包裹形态也兼容（不同子命令 JSON 形态不一致，见类注释）。</summary>
    [Fact]
    public void AcceptsArrayWrappedForm()
    {
        var json = "[" + TwoContainerNdjson.Replace("\n", "").Replace("}{", "},{") + "]";
        var stats = WslcCli.ParseStatsJson(json);

        Assert.Equal(2, stats.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  \n")]
    [InlineData("not json at all")]
    public void GarbageYieldsNothing(string output)
    {
        Assert.Empty(WslcCli.ParseStatsJson(output));
    }

    /// <summary>单行坏掉不影响其余（NDJSON 逐行独立解析的意义）。</summary>
    [Fact]
    public void BrokenLineDoesNotPoisonOthers()
    {
        const string mixed =
            "{\"Name\":\"good1\",\"CPUPerc\":\"1.5%\",\"MemUsage\":\"1MiB / 2GiB\",\"PIDs\":1}\n" +
            "{ this is not json }\n" +
            "{\"Name\":\"good2\",\"CPUPerc\":\"2.5%\",\"MemUsage\":\"2MiB / 2GiB\",\"PIDs\":2}\n";

        var stats = WslcCli.ParseStatsJson(mixed);
        Assert.Equal(2, stats.Count);
        Assert.Equal(new[] { "good1", "good2" }, stats.Select(s => s.Container).ToArray());
    }

    /// <summary>没有 Name 的对象无名可归，丢弃（否则会在曲线上出现一个空名桶）。</summary>
    [Fact]
    public void EntryWithoutNameIsDropped()
    {
        const string json = "{\"CPUPerc\":\"1.0%\",\"MemUsage\":\"1MiB / 2GiB\"}\n";
        Assert.Empty(WslcCli.ParseStatsJson(json));
    }

    /// <summary>内存段解析不了时 HasNumbers=false —— 曲线上跳过该点，而不是画成 0。</summary>
    [Fact]
    public void UnparsableMemoryClearsHasNumbers()
    {
        const string json = "{\"Name\":\"x\",\"CPUPerc\":\"1.0%\",\"MemUsage\":\"N/A\",\"PIDs\":1}\n";
        var stat = Assert.Single(WslcCli.ParseStatsJson(json));

        Assert.False(stat.HasNumbers);
        Assert.Equal("N/A", stat.Mem);
    }
}
