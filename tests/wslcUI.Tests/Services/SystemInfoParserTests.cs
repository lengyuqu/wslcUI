using wslcUI.Services;
using Xunit;

namespace wslcUI.Tests.Services;

/// <summary>
/// <c>wslc system info</c> 输出解析的回归测试。
///
/// <para>
/// 为什么这组测试重要：<c>system info</c> 的输出**既不是表格也不是 JSON**，
/// 而是「两个小节标题 + <c>键: 值</c> 行 + 一张三列会话表」的混合形态，
/// 且三个地方都容易写错：
///   ① 小节标题用**中文冒号**且行尾无空格；
///   ② 值里可能含冒号（设置文件路径 <c>C:\Users\…</c>）→ 只能按**第一个**冒号切；
///   ③ 会话表有一列名叫 <c>创建者 PID</c>（**列名内部含一个空格**），
///      按「2+ 空格」切列才不会被拆错；
///   ④ ⚠️ 会话表的**数据行不能按表头列位切片**（本仓库其他表格解析器都是按列位切的，
///      这里必须不同）：某行 PID 比表头 caption 窄时整行左移，会把显示名称啃掉开头
///      几个字母。因此数据行改按「2+ 空格切最多 3 段」解析。
/// </para>
///
/// <para>
/// 夹具是<strong>真机原文</strong>（wslc 3.0.1 GA，2026-10-02 在 WinR9 上抓取）。
/// wslc 升级后若这里变红，先重跑 <c>wslc system info</c> 核对真实形态，
/// 再按新形态改解析器与夹具——不要为了迁就测试去改解析器的容错语义。
/// </para>
/// </summary>
public class SystemInfoParserTests
{
    /// <summary>
    /// 真机原文（wslc 3.0.1 GA，2026-10-02，zh-CN locale）。
    /// 末尾带一个换行，且「客户端:」与「服务器:」之间有一个空行。
    /// </summary>
    private const string RealOutput =
        "客户端:\n" +
        "WSL 版本: 3.0.1.0\n" +
        "内核版本: 6.18.40.1-1\n" +
        "Direct3D 版本: 1.611.1-81528511\n" +
        "DXCore 版本: 10.0.26100.1-240331-1435.ge-release\n" +
        "Windows 版本: 10.0.26300.9550\n" +
        "设置文件: C:\\Users\\Administrator\\AppData\\Local\\wslc\\settings.yaml\n" +
        "\n" +
        "服务器:\n" +
        "会话管理器版本: 3.0.1\n" +
        "会话: 1\n" +
        "ID   创建者 PID   显示名称\n" +
        "1    36352     wslc-cli-admin-Administrator\n";

    // ================= 正常形态 =================

    [Fact]
    public void 真实输出_解析出全部客户端字段()
    {
        var info = WslcCli.ParseSystemInfo(RealOutput);

        Assert.Equal("3.0.1.0", info.WslVersion);
        Assert.Equal("6.18.40.1-1", info.KernelVersion);
        Assert.Equal("1.611.1-81528511", info.Direct3DVersion);
        Assert.Equal("10.0.26100.1-240331-1435.ge-release", info.DxCoreVersion);
        Assert.Equal("10.0.26300.9550", info.WindowsVersion);
    }

    [Fact]
    public void 真实输出_设置文件路径里的冒号不被截断()
    {
        var info = WslcCli.ParseSystemInfo(RealOutput);

        // 最容易写错的点：值是 Windows 路径，含盘符冒号。按第一个冒号切分，
        // 其余冒号必须原样保留，否则路径会被砍成 "C"。
        Assert.Equal(
            @"C:\Users\Administrator\AppData\Local\wslc\settings.yaml",
            info.SettingsFile);
    }

    [Fact]
    public void 真实输出_解析服务器段与会话数()
    {
        var info = WslcCli.ParseSystemInfo(RealOutput);

        Assert.Equal("3.0.1", info.SessionManagerVersion);
        Assert.Equal(1, info.SessionCount);
    }

    [Fact]
    public void 真实输出_解析会话表_列名含空格的列不被拆错()
    {
        var info = WslcCli.ParseSystemInfo(RealOutput);

        var session = Assert.Single(info.Sessions);
        Assert.Equal("1", session.Id);
        // 「创建者 PID」列名内部有一个空格，若按单空格切列会把 PID 和显示名混在一起。
        Assert.Equal(36352, session.CreatorPid);
        Assert.Equal("wslc-cli-admin-Administrator", session.DisplayName);
    }

    [Fact]
    public void 真实输出_至少解析到客户端信息()
    {
        Assert.True(WslcCli.ParseSystemInfo(RealOutput).HasAnyInfo);
    }

    // ================= CRLF / 尾部换行 =================

    [Fact]
    public void CRLF行尾与末尾多余空行_结果与LF一致()
    {
        var crlf = RealOutput.Replace("\n", "\r\n") + "\r\n\r\n";

        var info = WslcCli.ParseSystemInfo(crlf);

        Assert.Equal("3.0.1.0", info.WslVersion);
        Assert.Equal(@"C:\Users\Administrator\AppData\Local\wslc\settings.yaml", info.SettingsFile);
        Assert.Equal(36352, Assert.Single(info.Sessions).CreatorPid);
    }

    // ================= 缺字段 / 空输入 =================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    public void 空输入_返回全空对象而不抛异常(string? output)
    {
        var info = WslcCli.ParseSystemInfo(output!);

        Assert.Equal("", info.WslVersion);
        Assert.Equal("", info.SettingsFile);
        Assert.Null(info.SessionCount);
        Assert.Empty(info.Sessions);
        Assert.False(info.HasAnyInfo);
    }

    [Fact]
    public void 只有客户端段_会话字段保持未解析状态()
    {
        var info = WslcCli.ParseSystemInfo(
            "客户端:\nWSL 版本: 3.0.1.0\n内核版本: 6.18.40.1-1\n");

        Assert.Equal("3.0.1.0", info.WslVersion);
        // 读不到 ≠ 是 0：必须能区分，否则 UI 会把「没信息」显示成「0 个会话」。
        Assert.Null(info.SessionCount);
        Assert.Empty(info.Sessions);
        Assert.Equal("", info.SessionManagerVersion);
    }

    [Fact]
    public void 缺设置文件行_其余字段照常解析()
    {
        var info = WslcCli.ParseSystemInfo(
            "客户端:\nWSL 版本: 3.0.1.0\nWindows 版本: 10.0.26300.9550\n");

        Assert.Equal("3.0.1.0", info.WslVersion);
        Assert.Equal("10.0.26300.9550", info.WindowsVersion);
        Assert.Equal("", info.SettingsFile);
    }

    [Fact]
    public void 会话数为零_与未解析可区分()
    {
        var info = WslcCli.ParseSystemInfo("服务器:\n会话管理器版本: 3.0.1\n会话: 0\n");

        // 「真的是 0 个会话」是有效信息，不能与 null（读不到）混为一谈。
        Assert.Equal(0, info.SessionCount);
        Assert.Empty(info.Sessions);
    }

    [Fact]
    public void 会话数非数字_不谎报数值()
    {
        var info = WslcCli.ParseSystemInfo("服务器:\n会话: 未知\n");

        Assert.Null(info.SessionCount);
    }

    // ================= 异常 / 畸形形态 =================

    [Fact]
    public void 多个会话_逐行解析且列位不串()
    {
        // 真机表格是「列宽 = max(表头, 值)」对齐；这里构造两行以覆盖多行路径。
        var info = WslcCli.ParseSystemInfo(
            "服务器:\n" +
            "会话管理器版本: 3.0.1\n" +
            "会话: 2\n" +
            "ID   创建者 PID   显示名称\n" +
            "1    36352     wslc-cli-admin-Administrator\n" +
            "2    41004     wslcui-dev-Administrator\n");

        Assert.Equal(2, info.SessionCount);
        Assert.Equal(2, info.Sessions.Count);
        Assert.Equal("1", info.Sessions[0].Id);
        Assert.Equal(36352, info.Sessions[0].CreatorPid);
        Assert.Equal("wslc-cli-admin-Administrator", info.Sessions[0].DisplayName);
        Assert.Equal("2", info.Sessions[1].Id);
        Assert.Equal(41004, info.Sessions[1].CreatorPid);
        Assert.Equal("wslcui-dev-Administrator", info.Sessions[1].DisplayName);
    }

    [Fact]
    public void 无会话的服务器段_不产生空会话条目()
    {
        var info = WslcCli.ParseSystemInfo(
            "服务器:\n会话管理器版本: 3.0.1\n会话: 0\nID   创建者 PID   显示名称\n");

        Assert.Empty(info.Sessions);
    }

    [Fact]
    public void 英文locale_键名同样被识别()
    {
        // wslc 跟随系统语言；英文 locale 下键名全变英文，解析不能只认中文。
        var info = WslcCli.ParseSystemInfo(
            "Client:\n" +
            "WSL version: 3.0.1.0\n" +
            "Kernel version: 6.18.40.1-1\n" +
            "Windows version: 10.0.26300.9550\n" +
            "Settings file: C:\\Users\\me\\AppData\\Local\\wslc\\settings.yaml\n" +
            "\n" +
            "Server:\n" +
            "Session manager version: 3.0.1\n" +
            "Sessions: 1\n" +
            "ID   CREATOR PID   DISPLAY NAME\n" +
            "1    36352     wslc-cli-admin\n");

        Assert.Equal("3.0.1.0", info.WslVersion);
        Assert.Equal("6.18.40.1-1", info.KernelVersion);
        Assert.Equal("10.0.26300.9550", info.WindowsVersion);
        Assert.Equal(@"C:\Users\me\AppData\Local\wslc\settings.yaml", info.SettingsFile);
        Assert.Equal("3.0.1", info.SessionManagerVersion);
        Assert.Equal(1, info.SessionCount);
        Assert.Equal(36352, Assert.Single(info.Sessions).CreatorPid);
    }

    [Fact]
    public void 会话键不被会话管理器版本键吞掉()
    {
        // 「会话」是「会话管理器版本」的前缀——若用 StartsWith 匹配，
        // 服务器版本会被错当成会话数。这是本解析器必须避免的退化写法。
        var info = WslcCli.ParseSystemInfo("服务器:\n会话管理器版本: 3.0.1\n会话: 7\n");

        Assert.Equal("3.0.1", info.SessionManagerVersion);
        Assert.Equal(7, info.SessionCount);
    }

    [Fact]
    public void 全角冒号与小节标题变体_也能解析()
    {
        var info = WslcCli.ParseSystemInfo(
            "客户端：\nWSL 版本：3.0.1.0\n服务器：\n会话：4\n");

        Assert.Equal("3.0.1.0", info.WslVersion);
        Assert.Equal(4, info.SessionCount);
    }

    [Fact]
    public void 未知键与无冒号行_被跳过不影响其余字段()
    {
        var info = WslcCli.ParseSystemInfo(
            "客户端:\n" +
            "某个未来版本才有的键: whatever\n" +
            "WSL 版本: 3.0.1.0\n" +
            "这一行完全没有冒号\n" +
            "Windows 版本: 10.0.26300.9550\n");

        Assert.Equal("3.0.1.0", info.WslVersion);
        Assert.Equal("10.0.26300.9550", info.WindowsVersion);
    }

    [Fact]
    public void 值为空_不覆盖成空串以外的形态且不影响后续行()
    {
        var info = WslcCli.ParseSystemInfo(
            "客户端:\nWSL 版本:\n内核版本: 6.18.40.1-1\n");

        // 空值行被跳过（不写入），后面正常行仍要解析成功。
        Assert.Equal("", info.WslVersion);
        Assert.Equal("6.18.40.1-1", info.KernelVersion);
    }

    [Fact]
    public void 客户端段的会话表不会被误当作服务器段数据()
    {
        // 「会话: 1」出现在客户端段时不应产出 WslcSessionInfo。
        var info = WslcCli.ParseSystemInfo(
            "客户端:\nWSL 版本: 3.0.1.0\n会话: 1\n" +
            "ID   创建者 PID   显示名称\n" +
            "9    1234     bogus\n");

        Assert.Equal(1, info.SessionCount);
        Assert.Empty(info.Sessions);
    }

    [Fact]
    public void 会话PID非数字_记为0而不丢弃该行()
    {
        var info = WslcCli.ParseSystemInfo(
            "服务器:\n会话: 1\n" +
            "ID   创建者 PID   显示名称\n" +
            "1    abc     some-session\n");

        // PID 只用于排查，读不出时 0；整行仍要保留（ID 与名字有用）。
        var session = Assert.Single(info.Sessions);
        Assert.Equal(0, session.CreatorPid);
        Assert.Equal("1", session.Id);
        Assert.Equal("some-session", session.DisplayName);
    }

    /// <summary>
    /// 回归：会话表数据行<b>不能按表头列位</b>切片。
    /// 当某行 PID 比表头 caption 窄（3 位的 <c>abc</c> 撞上 8 字符宽的
    /// <c>创建者 PID</c>）时整行相对列位左移，按列位切片会把显示名称
    /// 啃掉开头几个字母 —— 这里曾把 <c>some-session</c> 读成 <c>me-session</c>。
    /// </summary>
    [Fact]
    public void PID窄于表头列宽_显示名称不被截断()
    {
        var info = WslcCli.ParseSystemInfo(
            "服务器:\n会话: 1\n" +
            "ID   创建者 PID   显示名称\n" +
            "1    abc     some-session\n");

        Assert.Equal("some-session", Assert.Single(info.Sessions).DisplayName);
    }

    /// <summary>
    /// 回归：英文 locale 下会话表同样要能整行解析，且名称不能被列宽截断
    /// （<c>CREATOR PID</c> 有 11 字符宽，比中文表头更宽，更容易错位）。
    /// </summary>
    [Fact]
    public void 英文会话表_名称不被列宽截断()
    {
        var info = WslcCli.ParseSystemInfo(
            "Server:\nSessions: 1\n" +
            "ID   CREATOR PID   DISPLAY NAME\n" +
            "1    36352     wslc-cli-admin\n");

        var session = Assert.Single(info.Sessions);
        Assert.Equal("36352", session.CreatorPid.ToString());
        Assert.Equal("wslc-cli-admin", session.DisplayName);
    }

    [Fact]
    public void 显示名称含空格_整体保留()
    {
        // 按「2+ 空格」切 3 段，末段整体保留 —— 名称内部的单空格不能被当成列分隔。
        var info = WslcCli.ParseSystemInfo(
            "服务器:\n会话: 1\n" +
            "ID   创建者 PID   显示名称\n" +
            "1    36352     my session name\n");

        Assert.Equal("my session name", Assert.Single(info.Sessions).DisplayName);
    }

    [Fact]
    public void 版本号里的点与短横线原样保留()
    {
        // 刻意不做「拆成数值」：四段式版本与带 build 后缀的版本是给人看的，
        // 拆数会丢信息（1.611.1-81528511 的后缀拆不出来）。
        var info = WslcCli.ParseSystemInfo(
            "客户端:\nDirect3D 版本: 1.611.1-81528511\nDXCore 版本: 10.0.26100.1-240331-1435.ge-release\n");

        Assert.Equal("1.611.1-81528511", info.Direct3DVersion);
        Assert.Equal("10.0.26100.1-240331-1435.ge-release", info.DxCoreVersion);
    }
}