namespace wslcUI.Models;

/// <summary>
/// 单条容器挂载映射。来自 <c>wslc inspect &lt;name&gt; --format json</c> 的
/// <c>Mounts</c> 数组（wslc 2.9.9 实测）。覆盖三种类型：
///   <c>volume</c> — <c>Name</c> + <c>Source</c> = 卷名（如 <c>pgdata</c>）；
///   <c>bind</c>   — <c>Name</c> 为空，<c>Source</c> = 宿主机目录绝对路径；
///   <c>tmpfs</c>  — <c>Name</c> 为空，<c>Source</c> 缺省。
/// <see cref="Display"/> 把这三种压成 UI 一行："src → dest"，
/// 卷名优先，无名挂载显示为 "(匿名)"。
/// </summary>
public class ContainerMount
{
    /// <summary>卷名（volume 类型）；bind/tmpfs 类型为空字符串。</summary>
    public string Name { get; set; } = "";

    /// <summary>容器内路径，如 <c>/var/lib/postgresql/data</c>。</summary>
    public string Destination { get; set; } = "";

    /// <summary>宿主机路径。volume 类型是卷内部路径（如 <c>pgdata</c>），bind 是绝对路径。</summary>
    public string Source { get; set; } = "";

    /// <summary>volume / bind / tmpfs。</summary>
    public string Type { get; set; } = "";

    public bool ReadWrite { get; set; }

    /// <summary>挂载的"头部标识"——volume 取 Name，否则取 Source，最差 "∅" 单字符占位（紧凑用于列表单元格）。</summary>
    public string Head =>
        !string.IsNullOrEmpty(Name) ? Name :
        !string.IsNullOrEmpty(Source) ? Source :
        "∅";

    /// <summary>显示用：左边取最有意义的标识符，右边挂载点。空 destination 时只剩左半边。</summary>
    public string Display
    {
        get
        {
            var left = !string.IsNullOrEmpty(Name) ? Name
                     : !string.IsNullOrEmpty(Source) ? Source
                     : "(匿名)";   // 详情面板空间宽，保留可读的全文本
            return string.IsNullOrEmpty(Destination) ? left : $"{left} → {Destination}";
        }
    }

    /// <summary>RW / RO 标签，给徽章着色用（Empty 类型灰色）。</summary>
    public string ModeText => ReadWrite ? "RW" : "RO";
}
