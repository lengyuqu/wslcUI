namespace wslcUI.Models;

/// <summary>容器内一个路径条目的类型（取自 `ls -la` 首字符）。</summary>
public enum ContainerFileKind
{
    /// <summary>`d`</summary>
    Directory,
    /// <summary>`-`</summary>
    File,
    /// <summary>`l`</summary>
    Link,
    /// <summary>块设备/字符设备/管道/socket 等（`b c p s`）</summary>
    Other,
}

/// <summary>
/// 容器内一条文件系统记录，来自 `wslc exec &lt;name&gt; ls -la &lt;path&gt;`
/// （见 <c>WslcCli.ParseDirectoryListing</c>）。真实输出形态（alpine/busybox 与
/// GNU coreutils 一致的前 8 列）：
/// <code>
/// total 16
/// drwxr-xr-x    2 root     root          4096 Oct  1 16:44 adir
/// lrwxrwxrwx    1 root     root            13 Oct  1 16:44 link1 -&gt; /etc/hostname
/// -rw-r--r--    1 root     root             3 Oct  1 16:44 name with space.txt
/// -rw-r--r--    1 root     root            89 Mar 24  2026 fstab
/// </code>
/// 注意第 8 列**可能是时刻也可能是年份**（`16:44` vs `2026`），两者都是单 token。
/// </summary>
public class ContainerFileEntry
{
    public string Name { get; set; } = "";

    /// <summary>10 字符权限串，如 <c>-rw-r--r--</c>（保留原文，UI 直接展示）。</summary>
    public string Permissions { get; set; } = "";

    public string Owner { get; set; } = "";
    public string Group { get; set; } = "";

    /// <summary>原始大小（字节数字符串）。目录的这个值是无意义的 inode 大小。</summary>
    public long SizeBytes { get; set; }

    /// <summary>`ls` 里的日期原文，如 <c>Oct 1 16:44</c> / <c>Mar 24 2026</c>。</summary>
    public string Modified { get; set; } = "";

    /// <summary>符号链接目标（仅当 <see cref="Kind"/> 为 Link 时有值）。</summary>
    public string LinkTarget { get; set; } = "";

    public ContainerFileKind Kind { get; set; } = ContainerFileKind.File;

    public bool IsDirectory => Kind == ContainerFileKind.Directory;
    public bool IsLink => Kind == ContainerFileKind.Link;

    /// <summary>目录的大小在 `ls` 里是 inode 大小，展示出来只会误导 —— 统一显示破折号。</summary>
    public string SizeDisplay =>
        IsDirectory ? "—" : Services.SizeParser.Format(SizeBytes);

    /// <summary>Segoe Fluent Icons 字形（文件夹 / 文档 / 链接）。</summary>
    public string Glyph => Kind switch
    {
        ContainerFileKind.Directory => "\uE8B7",
        ContainerFileKind.Link => "\uE71B",
        ContainerFileKind.Other => "\uE7C3",
        _ => "\uE8A5",
    };

    /// <summary>详情行：权限 · 属主 · 修改时间（链接附 → 目标）。</summary>
    public string Detail
    {
        get
        {
            var baseText = $"{Permissions}  {Owner}:{Group}  {Modified}";
            return IsLink && LinkTarget.Length > 0 ? $"{baseText}  →  {LinkTarget}" : baseText;
        }
    }
}
