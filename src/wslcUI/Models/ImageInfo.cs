namespace wslcUI.Models;

/// <summary>UI-facing projection of a wslc image.</summary>
public class ImageInfo
{
    public string Id { get; set; } = "";
    public string Repository { get; set; } = "";
    public string Tag { get; set; } = "";
    public string Size { get; set; } = "";
}
