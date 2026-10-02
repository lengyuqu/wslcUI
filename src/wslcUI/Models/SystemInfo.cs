namespace wslcUI.Models;

/// <summary>
/// <c>wslc system info</c> 的输出（wslc 3.0.1 GA 真机形态，2026-10-02 抓取）。
///
/// <para>
/// 原文是**两个带标题的段落 + 一张小表**，不是表格也不是 JSON：
/// </para>
/// <code>
/// 客户端:
/// WSL 版本: 3.0.1.0
/// 内核版本: 6.18.40.1-1
/// Direct3D 版本: 1.611.1-81528511
/// DXCore 版本: 10.0.26100.1-240331-1435.ge-release
/// Windows 版本: 10.0.26300.9550
/// 设置文件: C:\Users\&lt;user&gt;\AppData\Local\wslc\settings.yaml
///
/// 服务器:
/// 会话管理器版本: 3.0.1
/// 会话: 1
/// ID   创建者 PID   显示名称
/// 1    36352     wslc-cli-admin-Administrator
/// </code>
///
/// <para>
/// 值一律保留<strong>原始字符串</strong>（版本号里的四段式 <c>3.0.1.0</c>、
/// 带 build 后缀的 <c>1.611.1-81528511</c> 都是给人看的，自行拆数反而会丢信息）。
/// 未出现的字段留空串，<b>不填假值</b>——与 <see cref="StatInfo"/> 一致。
/// </para>
/// </summary>
public class SystemInfo
{
    // ---- 客户端段 ----

    /// <summary>WSL 客户端版本，如 <c>3.0.1.0</c>。</summary>
    public string WslVersion { get; set; } = "";

    /// <summary>WSL 内核版本，如 <c>6.18.40.1-1</c>。</summary>
    public string KernelVersion { get; set; } = "";

    /// <summary>Direct3D 版本（GUI 加速相关）。</summary>
    public string Direct3DVersion { get; set; } = "";

    /// <summary>DXCore 版本（GUI 加速相关）。</summary>
    public string DxCoreVersion { get; set; } = "";

    /// <summary>宿主 Windows 版本，如 <c>10.0.26300.9550</c>。</summary>
    public string WindowsVersion { get; set; } = "";

    /// <summary>
    /// <c>settings.yaml</c> 的绝对路径（如
    /// <c>C:\Users\&lt;user&gt;\AppData\Local\wslc\settings.yaml</c>）。
    /// wslc 的全局配置都在这里；仅供展示与「打开所在目录」，<b>不要在 UI 里改写它</b>。
    /// </summary>
    public string SettingsFile { get; set; } = "";

    // ---- 服务器段 ----

    /// <summary>会话管理器（session manager）版本，如 <c>3.0.1</c>。</summary>
    public string SessionManagerVersion { get; set; } = "";

    /// <summary>
    /// 活跃会话数（原文的 <c>会话: N</c>）。
    /// 用可空是因为「解析不到」与「真的是 0」必须能区分——前者不能谎报成 0。
    /// </summary>
    public int? SessionCount { get; set; }

    /// <summary>会话明细表（原文 <c>ID / 创建者 PID / 显示名称</c> 三列）。</summary>
    public IReadOnlyList<WslcSessionInfo> Sessions { get; set; } =
        Array.Empty<WslcSessionInfo>();

    /// <summary>是否至少解析到一项客户端信息（供 UI 决定要不要显示整块）。</summary>
    public bool HasAnyInfo =>
        WslVersion.Length > 0 || KernelVersion.Length > 0 || WindowsVersion.Length > 0;
}

/// <summary>
/// <c>wslc system info</c> 服务器段会话表的一行。
/// 形态：<c>1    36352     wslc-cli-admin-Administrator</c>。
/// </summary>
public class WslcSessionInfo
{
    /// <summary>会话 ID（原文表格第 1 列，短数字）。</summary>
    public string Id { get; set; } = "";

    /// <summary>
    /// 创建者进程 ID（原文 <c>创建者 PID</c> 列）。
    /// 解析失败时为 0——PID 只用于「是谁开的会话」这类排查，0 表示读不出来。
    /// </summary>
    public int CreatorPid { get; set; }

    /// <summary>显示名称，如 <c>wslc-cli-admin-Administrator</c>。</summary>
    public string DisplayName { get; set; } = "";
}