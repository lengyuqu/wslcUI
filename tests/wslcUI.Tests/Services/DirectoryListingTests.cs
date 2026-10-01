using System.Linq;
using wslcUI.Models;
using wslcUI.Services;
using Xunit;

namespace wslcUI.Tests.Services;

/// <summary>
/// `wslc exec &lt;ctr&gt; ls -la` 输出解析（<see cref="WslcCli.ParseDirectoryListing"/>）。
///
/// 夹具是 **2026-10-02 在 WinR9 / wslc 3.0.1.0 上对一次性 alpine 容器（busybox ls）
/// 直接抓取的原文**，不是手写的理想格式 —— 关键形态包括：
/// <list type="bullet">
///   <item>`total N` 首行（不靠本地化文案，靠"首段不是权限串"丢弃）</item>
///   <item>第 8 列**可能是年份**（`Mar 24  2026`）而不是时刻（`Oct  1 16:44`）</item>
///   <item>符号链接的 `name -&gt; target`</item>
///   <item>含空格的文件名</item>
/// </list>
/// </summary>
public class DirectoryListingTests
{
    // ============ 真机夹具（wslc 3.0.1.0 / alpine，2026-10-02 抓取）============

    /// <summary>探针容器里 `ls -la /tmp` 的原文（自建了 dir / link / 含空格文件名）。</summary>
    private const string TmpListing =
        "total 16\r\n" +
        "drwxrwxrwt    1 root     root          4096 Oct  1 16:44 .\r\n" +
        "drwxr-xr-x    1 root     root          4096 Oct  1 16:44 ..\r\n" +
        "drwxr-xr-x    2 root     root          4096 Oct  1 16:44 adir\r\n" +
        "lrwxrwxrwx    1 root     root            13 Oct  1 16:44 link1 -> /etc/hostname\r\n" +
        "-rw-r--r--    1 root     root             3 Oct  1 16:44 name with space.txt\r\n";

    /// <summary>`ls -la /` 原文 —— 含"设备号很大"的 proc/sys 与 root 属主的 700 目录。</summary>
    private const string RootListing =
        "total 64\n" +
        "drwxr-xr-x    1 root     root          4096 Oct  1 16:44 .\n" +
        "drwxr-xr-x    1 root     root          4096 Oct  1 16:44 ..\n" +
        "-rwxr-xr-x    1 root     root             0 Oct  1 16:44 .dockerenv\n" +
        "drwxr-xr-x    2 root     root          4096 Jun 13 16:38 bin\n" +
        "dr-xr-xr-x  214 root     root             0 Oct  1 16:44 proc\n" +
        "drwx------    2 root     root          4096 Jun 13 16:38 root\n" +
        "drwxrwxrwt    2 root     root          4096 Jun 13 16:38 tmp\n";

    /// <summary>`ls -la /etc` 原文 —— 覆盖**第 8 列是年份**的分支（`Mar 24  2026`）。</summary>
    private const string EtcListing =
        "total 156\n" +
        "drwxr-xr-x    1 root     root          4096 Oct  1 16:44 .\n" +
        "drwxr-xr-x    1 root     root          4096 Oct  1 16:44 ..\n" +
        "-rw-r--r--    1 root     root             7 Jun 13 15:17 alpine-release\n" +
        "drwxr-xr-x    4 root     root          4096 Jun 13 16:38 apk\n" +
        "-rw-r--r--    1 root     root            89 Mar 24  2026 fstab\n" +
        "-rw-r--r--    1 root     root           510 Mar 24  2026 group\n";

    // ============ 真机夹具断言 ============

    [Fact]
    public void ParsesRealTmpListing()
    {
        var entries = WslcCli.ParseDirectoryListing(TmpListing);

        // `.` / `..` 被丢弃 → 剩下的正好是自建的三个条目
        Assert.Equal(3, entries.Count);

        var dir = entries.Single(e => e.Name == "adir");
        Assert.Equal(ContainerFileKind.Directory, dir.Kind);
        Assert.True(dir.IsDirectory);
        Assert.Equal("drwxr-xr-x", dir.Permissions);
        Assert.Equal("root", dir.Owner);
        Assert.Equal("root", dir.Group);
        Assert.Equal(4096, dir.SizeBytes);
        Assert.Equal("Oct 1 16:44", dir.Modified);

        // 符号链接：名字与目标分开
        var link = entries.Single(e => e.Name == "link1");
        Assert.Equal(ContainerFileKind.Link, link.Kind);
        Assert.True(link.IsLink);
        Assert.Equal("/etc/hostname", link.LinkTarget);
        Assert.Equal("lrwxrwxrwx", link.Permissions);
        Assert.Contains("→  /etc/hostname", link.Detail);

        // 含空格的文件名必须完整保留（第 9 段整体）
        var spaced = entries.Single(e => e.Name == "name with space.txt");
        Assert.Equal(ContainerFileKind.File, spaced.Kind);
        Assert.Equal(3, spaced.SizeBytes);
    }

    [Fact]
    public void ParsesRealRootListing()
    {
        var entries = WslcCli.ParseDirectoryListing(RootListing);

        Assert.Equal(5, entries.Count); // .dockerenv / bin / proc / root / tmp
        Assert.DoesNotContain(entries, e => e.Name is "." or "..");

        // 隐藏文件保留（-a 的意义）
        Assert.Contains(entries, e => e.Name == ".dockerenv");

        // 链接数很大（214）导致列宽变化，切片不能错位：
        // 原行 `dr-xr-xr-x  214 root     root             0 Oct  1 16:44 proc`
        // → links=214，**size 是 0**（别把 links 列当 size）。
        var proc = entries.Single(e => e.Name == "proc");
        Assert.Equal(0, proc.SizeBytes);
        Assert.Equal(ContainerFileKind.Directory, proc.Kind);

        // 权限串原样保留（drwx------ 的 700 目录）
        Assert.Equal("drwx------", entries.Single(e => e.Name == "root").Permissions);
    }

    /// <summary>第 8 列是年份而不是时刻时同样成立（`Mar 24  2026`）。</summary>
    [Fact]
    public void HandlesYearInsteadOfTime()
    {
        var entries = WslcCli.ParseDirectoryListing(EtcListing);

        Assert.Equal(4, entries.Count); // alpine-release / apk / fstab / group
        var fstab = entries.Single(e => e.Name == "fstab");
        Assert.Equal("Mar 24 2026", fstab.Modified);
        Assert.Equal(89, fstab.SizeBytes);
    }

    // ============ 边界 ============

    [Fact]
    public void TotalLineProducesNoEntry()
    {
        // 只有 total 行 → 0 条（不依赖 "total" 这个英文词，靠首段不是权限串）
        Assert.Empty(WslcCli.ParseDirectoryListing("total 64\n"));
        // 命中本地化的 total 文案也照样丢弃
        Assert.Empty(WslcCli.ParseDirectoryListing("总用量 64\n"));
    }

    /// <summary>错误文本不能被误当成条目（真机 `ls -la /nope` 的输出形态）。</summary>
    [Theory]
    [InlineData("ls: /nope: No such file or directory")]
    [InlineData("ls: cannot access '/nope': No such file or directory")]
    [InlineData("")]
    [InlineData("\n\n")]
    [InlineData("some random warning line here")]
    public void NonListingOutputYieldsNothing(string output)
    {
        Assert.Empty(WslcCli.ParseDirectoryListing(output));
    }

    /// <summary>第 5 段不是数字（列错位 / 截断行）时整行丢弃，不产生半条错误记录。</summary>
    [Fact]
    public void RejectsRowWithNonNumericSize()
    {
        const string broken =
            "drwxr-xr-x    1 root     root        abc Oct  1 16:44 bad\n" +
            "drwxr-xr-x    1 root     root       4096 Oct  1 16:44 good\n";

        var entries = WslcCli.ParseDirectoryListing(broken);
        Assert.Single(entries);
        Assert.Equal("good", entries[0].Name);
    }

    /// <summary>非常规类型的首字符（b/c/p/s）归到 Other，且不被丢弃。</summary>
    [Theory]
    [InlineData("brw-rw----")]
    [InlineData("crw-rw-rw-")]
    [InlineData("prw-r--r--")]
    [InlineData("srwxrwxrwx")]
    public void NonRegularTypesMapToOther(string perms)
    {
        var line = $"{perms}    1 root     root             0 Oct  1 16:44 node\n";
        var entries = WslcCli.ParseDirectoryListing(line);

        Assert.Single(entries);
        Assert.Equal(ContainerFileKind.Other, entries[0].Kind);
    }

    /// <summary>ACL/SELinux 的尾随标记（`+` / `.` / `@`）不影响识别。</summary>
    [Theory]
    [InlineData("-rw-r--r--+")]
    [InlineData("-rw-r--r--.")]
    [InlineData("-rw-r--r--@")]
    public void ToleratesTrailingAclMarker(string perms)
    {
        var line = $"{perms}    1 root     root             5 Oct  1 16:44 x\n";
        var entries = WslcCli.ParseDirectoryListing(line);

        Assert.Single(entries);
        Assert.Equal("x", entries[0].Name);
    }

    /// <summary>普通文件名里含 "->" 不会（在非链接时）被切开。</summary>
    [Fact]
    public void ArrowInRegularFileNameIsNotSplit()
    {
        const string line = "-rw-r--r--    1 root     root             5 Oct  1 16:44 a -> b.txt\n";
        var entries = WslcCli.ParseDirectoryListing(line);

        Assert.Single(entries);
        Assert.Equal("a -> b.txt", entries[0].Name);
        Assert.Equal("", entries[0].LinkTarget);
    }

    /// <summary>目录的大小显示为破折号 —— `ls` 给的是 inode 大小，展示成体积会误导。</summary>
    [Fact]
    public void DirectorySizeDisplayIsDash()
    {
        var entries = WslcCli.ParseDirectoryListing(TmpListing);
        Assert.Equal("—", entries.Single(e => e.Name == "adir").SizeDisplay);
        // 文件仍显示真实体积
        Assert.Equal("3B", entries.Single(e => e.Name == "name with space.txt").SizeDisplay);
    }
}
