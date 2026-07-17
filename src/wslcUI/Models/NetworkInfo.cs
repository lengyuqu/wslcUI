namespace wslcUI.Models;

/// <summary>A wslc network (bridge/overlay...). Mirrors `wslc network list` output.</summary>
public class NetworkInfo
{
    public string Name { get; set; } = "";
    public string Driver { get; set; } = "";
    public string Scope { get; set; } = "";
}
