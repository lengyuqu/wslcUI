using wslcUI.Services;
using Xunit;

namespace wslcUI.Tests.Services;

/// <summary>
/// 卷列表 json 解析（wslc 2.9.9.0 实测样例）。table 格式只有
/// DRIVER/VOLUME NAME 两列，挂载点必须走 --format json。
/// </summary>
public class VolumeListJsonTests
{
    [Fact]
    public void ParsesMountpointFromJsonOutput()
    {
        const string json = """
            {"Availability":"N/A","Driver":"guest","Group":"N/A","Labels":"","Links":"N/A","Mountpoint":"/var/lib/docker/volumes/pgdata/_data","Name":"pgdata","Scope":"local","Size":"N/A","Status":"N/A"}
            {"Availability":"N/A","Driver":"guest","Group":"N/A","Labels":"","Links":"N/A","Mountpoint":"/var/lib/docker/volumes/testvol/_data","Name":"testvol","Scope":"local","Size":"N/A","Status":"N/A"}
            """;
        var vols = WslcCli.ParseVolumeListJson(json);
        Assert.Equal(2, vols.Count);
        Assert.Equal("pgdata", vols[0].Name);
        Assert.Equal("guest", vols[0].Driver);
        Assert.Equal("/var/lib/docker/volumes/pgdata/_data", vols[0].Mountpoint);
        Assert.Equal("testvol", vols[1].Name);
    }

    [Fact]
    public void EmptyOutput_ReturnsEmptyList()
    {
        Assert.Empty(WslcCli.ParseVolumeListJson(""));
        Assert.Empty(WslcCli.ParseVolumeListJson("   \n"));
    }

    [Fact]
    public void NonStringFields_AreTolerated()
    {
        // Labels: null 等非字符串字段不应导致解析失败
        const string json = """{"Driver":"guest","Mountpoint":"/mnt/x","Name":"v1","Labels":null}""";
        var vols = WslcCli.ParseVolumeListJson(json);
        Assert.Single(vols);
        Assert.Equal("/mnt/x", vols[0].Mountpoint);
    }
}
