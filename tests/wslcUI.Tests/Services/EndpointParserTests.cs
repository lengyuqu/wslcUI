using wslcUI.Services;
using Xunit;

namespace wslcUI.Tests.Services;

/// <summary>
/// <see cref="EndpointParser"/> 单测。夹具取自 wslc 3.0.1 真机输出。
/// </summary>
public class EndpointParserTests
{
    [Fact]
    public void 真机多端口_解析出两条端点()
    {
        // 真机 3.0.1 实测（wslcui-port-probe，-p 18096:80 -p 18095:443）
        var text = "127.0.0.1:18096->80/tcp, 127.0.0.1:18095->443/tcp";

        var eps = EndpointParser.Parse(text, "probe");

        Assert.Equal(2, eps.Count);
        Assert.Equal(18096, eps[0].HostPort);
        Assert.Equal(80, eps[0].ContainerPort);
        Assert.Equal(443, eps[1].ContainerPort);
        Assert.Equal("tcp", eps[1].Protocol);
        Assert.Equal("probe", eps[0].ContainerName);
    }

    [Fact]
    public void 单端口_解析出一条()
    {
        var eps = EndpointParser.Parse("0.0.0.0:8080->80/tcp", "web");
        var ep = Assert.Single(eps);
        Assert.Equal("0.0.0.0", ep.HostIp);
        Assert.Equal(8080, ep.HostPort);
        Assert.Equal("http://0.0.0.0:8080/", ep.Url);
    }

    [Fact]
    public void 空输入_返回空列表而非抛异常()
    {
        // 已停止的容器端口列为空；整页刷新不能因此炸掉。
        Assert.Empty(EndpointParser.Parse(null, "x"));
        Assert.Empty(EndpointParser.Parse("", "x"));
        Assert.Empty(EndpointParser.Parse("   ", "x"));
    }

    [Fact]
    public void 脏数据_跳过坏项但保留好项()
    {
        // 混入无法解析的片段，只应丢掉那一项。
        var eps = EndpointParser.Parse(
            "127.0.0.1:8080->80/tcp, garbage, nonsense->80/tcp, 0.0.0.0:9090->90/udp",
            "web");

        Assert.Equal(2, eps.Count);
        Assert.Equal(8080, eps[0].HostPort);
        Assert.Equal(9090, eps[1].HostPort);
        Assert.Equal("udp", eps[1].Protocol);
    }

    [Fact]
    public void 畸形IP_不因冒号而误吞整段()
    {
        // `::not-a-port->80/tcp` 里IP 段含非法字符，Regex 的 IP 字符类不接受
        // 字母，**不应**被当成合法端点（端口号也非数字）。
        var eps = EndpointParser.Parse("::not-a-port->80/tcp", "web");
        Assert.Empty(eps);
    }

    [Fact]
    public void 端口号越界_跳过而非抛FormatException()
    {
        // 直接 int.Parse 会抛，整页刷新被一行展示数据炸掉。
        var eps = EndpointParser.Parse("127.0.0.1:99999999999->80/tcp", "web");
        Assert.Empty(eps);
    }

    [Fact]
    public void IPv6_去方括号且识别为回环()
    {
        var eps = EndpointParser.Parse("[::1]:8080->80/tcp", "v6");
        var ep = Assert.Single(eps);
        Assert.Equal("::1", ep.HostIp);
        Assert.True(ep.IsLoopback);
    }

    [Fact]
    public void Udp_没有可打开的Url()
    {
        var ep = Assert.Single(EndpointParser.Parse("0.0.0.0:53->53/udp", "dns"));
        Assert.Equal("udp", ep.Protocol);
        Assert.Equal("", ep.Url);
    }

    [Fact]
    public void 省略主机IP_默认按通配地址处理()
    {
        var ep = Assert.Single(EndpointParser.Parse("8080->80/tcp", "web"));
        Assert.Equal("0.0.0.0", ep.HostIp);
        Assert.False(ep.IsLoopback);
    }

    [Fact]
    public void 协议大小写归一()
    {
        var ep = Assert.Single(EndpointParser.Parse("127.0.0.1:8080->80/TCP", "web"));
        Assert.Equal("tcp", ep.Protocol);
    }

    [Fact]
    public void Summary_展示宿主到容器的映射()
    {
        var ep = Assert.Single(EndpointParser.Parse("127.0.0.1:18096->80/tcp", "probe"));
        Assert.Equal("127.0.0.1:18096 → 80/tcp", ep.Summary);
    }

    [Fact]
    public void 分隔符前后空格_不影响解析()
    {
        // wslc 用 ", " 分隔，TrimEntries 必须生效。
        var eps = EndpointParser.Parse("  127.0.0.1:8080->80/tcp ,  127.0.0.1:8081->80/tcp  ", "web");
        Assert.Equal(2, eps.Count);
    }
}
