namespace wslcUI.Models;

/// <summary>A wslc volume (guest/vhd backend). Mirrors `wslc volume list` output.</summary>
public class VolumeInfo
{
    public string Name { get; set; } = "";
    public string Driver { get; set; } = "";
    public string Mountpoint { get; set; } = "";
}
