namespace wslcUI.ViewModels;

using System;
using System.Collections.Generic;
using wslcUI.Models;

/// <summary>
/// Reverse-mapping helper for volume → containers.
/// 纯函数：与 MainViewModel 状态解耦，可独立单测。
/// 命名空间挂 ViewModels 是因为这是 VM 业务逻辑而非通用服务（不让 Services 沉）。
/// </summary>
internal static class VolumeReverseMapping
{
    /// <summary>
    /// 扫描容器集合，找出哪些容器以 <paramref name="volumeName"/> 为挂载卷名。
    ///   <c>usedBy</c>：所有 <c>MountsLoaded==true</c> 且 <c>mount.Name</c> 命中
    ///     <paramref name="volumeName"/> 的容器名（按集合顺序）。
    ///   <c>notInspected</c>：<c>MountsLoaded==false</c> 的容器数量，给 UI 用作
    ///     "还有 N 个未查看"提示计数。
    ///
    /// 匹配规则（与 CLI/docker 一致）：
    ///   • <c>mount.Name</c> 非空且与 <paramref name="volumeName"/> **Ordinal 相等**。
    ///   • 大小写敏感（wslc 自身大小写敏感；不能因文件名差异而漏判）。
    ///   • 容器内同一容器多次挂载同卷：只计一次（break）。
    ///   • bind/tmpfs 等 Name 为空的挂载：跳过（无法反向定位卷）。
    /// </summary>
    public static (IReadOnlyList<string> UsedBy, int NotInspected) Match(
        IEnumerable<ContainerInfo> containers,
        string volumeName)
    {
        var usedBy = new List<string>();
        var notInspected = 0;
        foreach (var c in containers)
        {
            if (!c.MountsLoaded) { notInspected++; continue; }
            foreach (var m in c.Mounts)
            {
                if (!string.IsNullOrEmpty(m.Name) &&
                    string.Equals(m.Name, volumeName, StringComparison.Ordinal))
                {
                    usedBy.Add(c.Name);
                    break;
                }
            }
        }
        return (usedBy, notInspected);
    }
}
