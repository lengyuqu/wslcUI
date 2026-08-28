namespace wslcUI.Models;

/// <summary>UI-facing projection of a wslc image.</summary>
public class ImageInfo
{
    public string Id { get; set; } = "";
    public string Repository { get; set; } = "";
    public string Tag { get; set; } = "";
    public string Size { get; set; } = "";

    /// <summary>
    /// sha256 摘要。仅 SDK 侧镜像可拿到（`Session.GetImages()` 有 Sha256）；
    /// CLI 侧 `wslc images` 只给短 IMAGE ID，此时为空。
    /// </summary>
    public string Digest { get; set; } = "";

    /// <summary>详情面板显示用。拿不到摘要时显示「—」，不用假数据填充。</summary>
    public string DigestDisplay =>
        string.IsNullOrWhiteSpace(Digest) ? "—"
        : Digest.Length <= 19 ? Digest
        : Digest[..19] + "…";

    /// <summary>完整引用 `repo:tag`，命令与确认对话框用。</summary>
    public string Reference =>
        string.IsNullOrWhiteSpace(Tag) ? Repository : $"{Repository}:{Tag}";
}
