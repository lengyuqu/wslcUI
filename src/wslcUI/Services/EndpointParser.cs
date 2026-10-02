using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using wslcUI.Models;

namespace wslcUI.Services;

/// <summary>
/// 解析 <c>wslc list</c> 端口列，产出端点面板的行。
///
/// <para>
/// 端口列形如 <c>127.0.0.1:18096-&gt;80/tcp, 127.0.0.1:18095-&gt;443/tcp</c>
/// （真机 3.0.1 实测，多端口用 <c>", "</c> 分隔；未显式绑定 IP 时主机侧为
/// <c>0.0.0.0</c>，停止的容器该列为空）。已停止的容器没有端点，
/// 但调用方应把「容器是否运行」一并判断，不要只看端口列。
/// </para>
///
/// <para>
/// 纯函数：不含UI 类型、不读文件、不起进程，因此可单测 ——
/// 这与本仓库 <c>ParseContainerList</c> / <c>ParseStatsJson</c> 的分层一致。
/// </para>
/// </summary>
internal static class EndpointParser
{
    /// <summary>
    /// 单个端点：<c>[hostIp:]hostPort-&gt;containerPort/protocol</c>。
    /// hostIp 可省略（wslc 一般会给出），端口允许 <c>0</c>（随机端口已分配但未就绪）。
    /// </summary>
    private static readonly Regex EndpointPattern = new(
        @"^(?:(?<ip>\[[0-9A-Fa-f:]+\]|[0-9A-Fa-f:.]+):)?(?<hp>\d+)->(?<cp>\d+)/(?<proto>[a-zA-Z]+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 把一个容器的端口列解析成端点列表。
    /// <paramref name="portsText"/> 为空/空白（已停止的容器）时返回空列表，
    /// **不抛异常** —— 端口列是可选展示，脏数据不该中断整页刷新。
    /// </summary>
    internal static IReadOnlyList<EndpointInfo> Parse(string? portsText, string containerName)
    {
        var result = new List<EndpointInfo>();
        if (string.IsNullOrWhiteSpace(portsText)) return result;

        foreach (var raw in portsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = EndpointPattern.Match(raw);
            if (!m.Success) continue;

            // 端口号用 int.TryParse 而非直接 int.Parse：越界值（32768+、超长数字）
            // 会让整页刷新抛 FormatException。端口列是展示数据，跳过坏项比崩掉好。
            if (!int.TryParse(m.Groups["hp"].Value, out var hostPort)) continue;
            if (!int.TryParse(m.Groups["cp"].Value, out var containerPort)) continue;

            // IPv6 在 wslc 输出里带方括号（[::1]:8080），展示时去掉括号。
            var ip = m.Groups["ip"].Value.Trim('[', ']');
            if (ip.Length == 0) ip = "0.0.0.0";

            result.Add(new EndpointInfo
            {
                ContainerName = containerName,
                HostIp = ip,
                HostPort = hostPort,
                ContainerPort = containerPort,
                Protocol = m.Groups["proto"].Value.ToLowerInvariant(),
            });
        }

        return result;
    }
}
