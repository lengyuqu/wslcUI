using wslcUI.Models;
using wslcUI.ViewModels;
using Xunit;

namespace wslcUI.Tests.ViewModels;

/// <summary>
/// 反向映射核心算法的纯函数单测（卷 → 哪些容器在用）。
/// MainViewModel.RecomputeUsedBy 仅做"分配结果 + 通知 hint"，
/// 真正的命中规则走这里。可加更多规则仅改本测试，不动 VM。
/// </summary>
public class VolumeReverseMappingTests
{
    [Fact]
    public void EmptyContainers_NoMatches()
    {
        var (usedBy, notInspected) = VolumeReverseMapping.Match(
            new List<ContainerInfo>(), "v1");
        Assert.Empty(usedBy);
        Assert.Equal(0, notInspected);
    }

    [Fact]
    public void MountedContainer_MatchesByName()
    {
        var c = MakeContainer("web", mounts: new[]
        {
            new ContainerMount { Name = "v1", Type = "volume", Destination = "/d" },
        });

        var (usedBy, notInspected) = VolumeReverseMapping.Match(new[] { c }, "v1");
        Assert.Equal(new[] { "web" }, usedBy);
        Assert.Equal(0, notInspected);
    }

    [Fact]
    public void CaseSensitive_DoesNotMatch()
    {
        // wslc 自身大小写敏感；若不区分，"Pgdata" 会被误判为 "pgdata"。
        var c = MakeContainer("web", mounts: new[]
        {
            new ContainerMount { Name = "PGDATA", Type = "volume" },
        });

        var (usedBy, _) = VolumeReverseMapping.Match(new[] { c }, "pgdata");
        Assert.Empty(usedBy);
    }

    [Fact]
    public void UnloadedContainer_CountsAsNotInspected_NotAsUsedBy()
    {
        // MountsLoaded==false 应归到"未查"，不能算 0 引用——避免误导用户去删卷。
        var c = new ContainerInfo { Name = "web" };   // MountsLoaded 默认 false

        var (usedBy, notInspected) = VolumeReverseMapping.Match(new[] { c }, "v1");
        Assert.Empty(usedBy);
        Assert.Equal(1, notInspected);
    }

    [Fact]
    public void BindMount_WithEmptyName_DoesNotMatchByVolumeName()
    {
        // bind 类型 mount.Name 为空，不能与某个具名卷匹配——Source 是宿主机路径。
        var c = MakeContainer("web", mounts: new[]
        {
            new ContainerMount { Name = "", Type = "bind", Source = "/host/path", Destination = "/d" },
        });

        var (usedBy, notInspected) = VolumeReverseMapping.Match(new[] { c }, "v1");
        Assert.Empty(usedBy);
        Assert.Equal(0, notInspected);
    }

    [Fact]
    public void MultipleMounts_FirstMatchWins_NoDuplication()
    {
        // 同容器多挂载：只计一次（break 行为）。
        var c = MakeContainer("db", mounts: new[]
        {
            new ContainerMount { Name = "v1", Type = "volume" },
            new ContainerMount { Name = "v1", Type = "volume", Destination = "/elsewhere" },
        });

        var (usedBy, _) = VolumeReverseMapping.Match(new[] { c }, "v1");
        Assert.Equal(new[] { "db" }, usedBy);
    }

    [Fact]
    public void MultipleContainers_AggregatesAndCountsUnloadedSeparately()
    {
        var loaded = MakeContainer("a", mounts: new[]
        {
            new ContainerMount { Name = "v1" },
        });
        var notLoaded = new ContainerInfo { Name = "b" };   // MountsLoaded false
        var otherLoaded = MakeContainer("c", mounts: new[]
        {
            new ContainerMount { Name = "v2" },   // 不命中
        });

        var (usedBy, notInspected) = VolumeReverseMapping.Match(
            new[] { loaded, notLoaded, otherLoaded }, "v1");

        Assert.Equal(new[] { "a" }, usedBy);
        Assert.Equal(1, notInspected);
    }

    [Fact]
    public void PreservationOfInputOrder()
    {
        // 命中顺序按输入 IEnumerable 顺序，调用方期望按"刷新时刻容器列表顺序"展示。
        var c1 = MakeContainer("c", mounts: new[] { new ContainerMount { Name = "v1" } });
        var c2 = MakeContainer("a", mounts: new[] { new ContainerMount { Name = "v1" } });
        var c3 = MakeContainer("b", mounts: new[] { new ContainerMount { Name = "v1" } });

        var (usedBy, _) = VolumeReverseMapping.Match(new[] { c1, c2, c3 }, "v1");
        Assert.Equal(new[] { "c", "a", "b" }, usedBy);
    }

    private static ContainerInfo MakeContainer(string name, ContainerMount[] mounts)
    {
        var c = new ContainerInfo { Name = name };
        c.Mounts = mounts;
        c.MountsLoaded = true;
        return c;
    }
}
