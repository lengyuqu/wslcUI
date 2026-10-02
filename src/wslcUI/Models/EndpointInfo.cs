namespace wslcUI.Models;

/// <summary>
/// 一个已发布到宿主机的端口（端点面板的一行）。
/// 由 <see cref="ContainerInfo.Ports"/> 的原始字符串解析而来。
/// </summary>
public class EndpointInfo
{
    /// <summary>所属容器名（用于显示与跳转）。</summary>
    public string ContainerName { get; set; } = "";

    /// <summary>宿主侧监听地址，如 <c>127.0.0.1</c>；未显式绑定时 wslc 输出 <c>0.0.0.0</c>。</summary>
    public string HostIp { get; set; } = "";

    /// <summary>宿主侧端口（浏览器里要用的那个）。</summary>
    public int HostPort { get; set; }

    /// <summary>容器侧端口。</summary>
    public int ContainerPort { get; set; }

    /// <summary>传输协议（<c>tcp</c> / <c>udp</c>）。</summary>
    public string Protocol { get; set; } = "tcp";

    /// <summary>可直接在浏览器打开的地址；UDP 端口没有可打开的 URL，返回空串。</summary>
    public string Url => Protocol == "udp" ? "" : $"http://{HostIp}:{HostPort}/";

    /// <summary>端点摘要，如 <c>127.0.0.1:18096 → 80/tcp</c>。</summary>
    public string Summary => $"{HostIp}:{HostPort} → {ContainerPort}/{Protocol}";

    /// <summary>是否绑定在回环地址（决定能否从局域网访问）。</summary>
    public bool IsLoopback => HostIp is "127.0.0.1" or "localhost" or "::1";
}
