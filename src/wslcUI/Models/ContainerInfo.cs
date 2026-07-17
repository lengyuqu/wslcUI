namespace wslcUI.Models;

/// <summary>UI-facing projection of a wslc container.</summary>
public class ContainerInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Image { get; set; } = "";
    public string Status { get; set; } = "";
}
