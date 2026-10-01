using wslcUI.ViewModels;
using Xunit;

namespace wslcUI.Tests.ViewModels;

/// <summary>
/// 文件浏览窗口的路径运算（<see cref="ContainerFilesViewModel"/> 的三个纯静态函数）。
/// 这些函数决定 UI 的"上级目录"能走到哪、上传落到哪个目录，错一格就会把用户
/// 带到想不到的位置，所以逐条钉住；同时也是"纯函数独立可测"的示例（不需要起窗口）。
/// </summary>
public class ContainerFilesPathTests
{
    // ---- ParentOf ----

    [Theory]
    [InlineData("/", "/")]          // 根的上层还是根 → CanGoUp=false
    [InlineData("", "/")]
    [InlineData("/a", "/")]
    [InlineData("/a/b", "/a")]
    [InlineData("/a/b/", "/a")]     // 容忍尾斜杠
    [InlineData("/a/b/c", "/a/b")]
    [InlineData("/a/b/c/", "/a/b")]
    public void ParentOfWalksUp(string input, string expected)
    {
        Assert.Equal(expected, ContainerFilesViewModel.ParentOf(input));
    }

    /// <summary>根目录是唯一的"走不上去"，UI 据此禁用「上级目录」。</summary>
    [Fact]
    public void RootHasNoParent()
    {
        Assert.Equal("/", ContainerFilesViewModel.ParentOf("/"));
    }

    // ---- Combine ----

    [Theory]
    [InlineData("/", "x", "/x")]
    [InlineData("/a", "x", "/a/x")]
    [InlineData("/a/", "x", "/a/x")]        // 不产生 //
    [InlineData("/a/b", "c d.txt", "/a/b/c d.txt")]
    [InlineData("", "x", "/x")]
    public void CombineJoinsPaths(string dir, string name, string expected)
    {
        Assert.Equal(expected, ContainerFilesViewModel.Combine(dir, name));
    }

    // ---- NormalizeInputPath ----

    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("   ", "/")]
    [InlineData("/", "/")]
    [InlineData("/etc", "/etc")]
    [InlineData("etc", "/etc")]             // 相对路径按根处理
    [InlineData("//a//b", "/a/b")]          // 压掉重复斜杠
    [InlineData("/a/", "/a")]               // 去掉尾斜杠
    [InlineData("  /a  ", "/a")]            // 去首尾空白
    [InlineData("/a/b/", "/a/b")]
    public void NormalizeInputPathCleansUserInput(string? input, string expected)
    {
        Assert.Equal(expected, ContainerFilesViewModel.NormalizeInputPath(input));
    }

    /// <summary>归一化后的路径再取父目录不应出现死循环（// 已被压掉）。</summary>
    [Fact]
    public void NormalizedPathParentIsStable()
    {
        var p = ContainerFilesViewModel.NormalizeInputPath("//a//b//");
        Assert.Equal("/a/b", p);
        Assert.Equal("/a", ContainerFilesViewModel.ParentOf(p));
    }
}
