using wslcUI.Services;
using Xunit;

namespace wslcUI.Tests.Services;

/// <summary>
/// `wslc inspect &lt;name&gt; --format json` 输出解析（wslc 2.9.9.0 实测）。
/// 触发条件：详情面板选中容器，主流程会触发 inspect；table 格式没 Mounts 列，
/// 必须走 --format json。
/// </summary>
public class InspectContainerJsonTests
{
    /// <summary>wslc 实测输出：单元素数组，含 1 个 volume 挂载。</summary>
    [Fact]
    public void ParsesPgdataLikeMount()
    {
        const string json = """
            [{
              "Name": "wslc-pg",
              "Mounts": [
                {
                  "Destination": "/var/lib/postgresql/data",
                  "Name": "pgdata",
                  "ReadWrite": true,
                  "Source": "pgdata",
                  "Type": "volume"
                }
              ]
            }]
            """;
        var mounts = WslcCli.ParseContainerInspect(json);
        Assert.Single(mounts);
        var m = mounts[0];
        Assert.Equal("pgdata", m.Name);
        Assert.Equal("/var/lib/postgresql/data", m.Destination);
        Assert.Equal("pgdata", m.Source);
        Assert.Equal("volume", m.Type);
        Assert.True(m.ReadWrite);
        Assert.Equal("pgdata → /var/lib/postgresql/data", m.Display);
        Assert.Equal("RW", m.ModeText);
    }

    /// <summary>bind 挂载无 Name 字段，Source 是绝对路径；Display 应回退到 Source。</summary>
    [Fact]
    public void ParsesBindMountWithHostPath()
    {
        const string json = """
            [{
              "Name": "dev",
              "Mounts": [
                {
                  "Destination": "/etc/redis/conf.d",
                  "Name": "",
                  "ReadWrite": false,
                  "Source": "/Users/me/cfg/redis",
                  "Type": "bind"
                }
              ]
            }]
            """;
        var mounts = WslcCli.ParseContainerInspect(json);
        Assert.Single(mounts);
        var m = mounts[0];
        Assert.Equal("", m.Name);
        Assert.Equal("/etc/redis/conf.d", m.Destination);
        Assert.Equal("/Users/me/cfg/redis", m.Source);
        Assert.Equal("bind", m.Type);
        Assert.False(m.ReadWrite);
        Assert.Equal("/Users/me/cfg/redis → /etc/redis/conf.d", m.Display);
        Assert.Equal("RO", m.ModeText);
    }

    /// <summary>多元素数组：合并所有 Mounts（同指令 inspect 多个 name 时）。</summary>
    [Fact]
    public void MergesMountsAcrossMultipleInspectObjects()
    {
        const string json = """
            [
              { "Name": "a", "Mounts": [{ "Destination": "/data", "Name": "v1", "Source": "v1", "Type": "volume", "ReadWrite": true }] },
              { "Name": "b", "Mounts": [{ "Destination": "/cfg", "Name": "v2", "Source": "v2", "Type": "volume", "ReadWrite": false }] }
            ]
            """;
        var mounts = WslcCli.ParseContainerInspect(json);
        Assert.Equal(2, mounts.Count);
        Assert.Equal("v1", mounts[0].Name);
        Assert.Equal("/data", mounts[0].Destination);
        Assert.Equal("v2", mounts[1].Name);
        Assert.Equal("/cfg", mounts[1].Destination);
        Assert.False(mounts[1].ReadWrite);
    }

    /// <summary>空 Mounts（无挂载的容器）——返回空列表而非抛错。</summary>
    [Fact]
    public void EmptyMounts_ReturnsEmpty()
    {
        const string json = """[{"Name":"web","Mounts":[]}]""";
        Assert.Empty(WslcCli.ParseContainerInspect(json));
    }

    /// <summary>顶层非数组（理论上不会发生，但底层只能尽力解析）。</summary>
    [Fact]
    public void NonArrayRoot_ReturnsEmpty()
    {
        Assert.Empty(WslcCli.ParseContainerInspect("{\"Name\":\"x\",\"Mounts\":[]}"));
    }

    /// <summary>无 Destination 的非法 mount 会被跳过，避免 UI 显示空文本。</summary>
    [Fact]
    public void MissingDestination_IsSkipped()
    {
        const string json = """
            [{ "Name": "x", "Mounts": [
                { "Destination": "", "Name": "bad", "Source": "bad", "Type": "volume", "ReadWrite": true },
                { "Destination": "/ok", "Name": "ok", "Source": "ok", "Type": "volume", "ReadWrite": true }
            ]}]
            """;
        var mounts = WslcCli.ParseContainerInspect(json);
        Assert.Single(mounts);
        Assert.Equal("ok", mounts[0].Name);
    }

    /// <summary>ReadWrite 缺省时按 true 处理（docker-compose 也是默认 RW）。</summary>
    [Fact]
    public void MissingReadWrite_DefaultsToTrue()
    {
        const string json = """[{"Name":"x","Mounts":[{"Destination":"/d","Name":"n","Source":"n","Type":"volume"}]}]""";
        var mounts = WslcCli.ParseContainerInspect(json);
        Assert.Single(mounts);
        Assert.True(mounts[0].ReadWrite);
    }

    /// <summary>空输出/空白 → 空列表，不抛错。</summary>
    [Fact]
    public void EmptyOrWhitespace_ReturnsEmpty()
    {
        Assert.Empty(WslcCli.ParseContainerInspect(""));
        Assert.Empty(WslcCli.ParseContainerInspect("   \n"));
    }
}
