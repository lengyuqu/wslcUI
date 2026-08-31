using wslcUI.Terminal;
using Xunit;

namespace wslcUI.Tests.Services;

/// <summary>
/// PseudoConsole.ProbeHealth 真机行为测试（Win32 依赖，无法脱离 Windows 跑）：
/// 本机（Win11 26200.9278）ConPTY attach 存在系统故障，预期返回 Healthy=false
/// 且退出码为 0xC0000142。健康机器上该测试同样成立（退出码 0）——
/// 断言的是「探测结果与真实退出码自洽」而非硬编码故障值。
/// </summary>
public class ConPtyHealthTests
{
    [Fact]
    public void ProbeHealth_ReturnsSelfConsistentResult()
    {
        var (healthy, exitCode) = PseudoConsole.ProbeHealth();

        // 自洽性：healthy 当且仅当退出码为 0。
        Assert.Equal(healthy, exitCode == 0);

        // 缓存生效：第二次调用命中缓存，返回相同结果。
        var (healthy2, exitCode2) = PseudoConsole.ProbeHealth();
        Assert.Equal(healthy, healthy2);
        Assert.Equal(exitCode, exitCode2);
    }
}
