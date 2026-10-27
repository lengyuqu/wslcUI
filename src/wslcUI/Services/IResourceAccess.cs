using wslcUI.Models;

namespace wslcUI.Services;

/// <summary>
/// 资源集合的只读访问 + 局部刷新能力。
///
/// <para>
/// 页面级 ViewModel（维护 / 端点等）需要看到容器、镜像、网络、卷的当前内容，
/// 有时还需要「只刷某一类资源」。它们不应该依赖整个 <c>MainViewModel</c>
/// —— 那会把壳的 80 多个成员全暴露给子 VM，也让子 VM 无法单测。
/// </para>
///
/// <para>
/// 刻意只给**只读**视图：子 VM 只能通过 <see cref="RefreshContainersAsync"/> 这类
/// 方法让壳去刷新，不能直接改集合。集合的所有权始终在壳手里，
/// 避免「两个 VM 各自 new 一个集合」这种最难查的漂移。
/// </para>
/// </summary>
public interface IResourceAccess
{
    IReadOnlyList<ContainerInfo> Containers { get; }
    IReadOnlyList<ImageInfo> Images { get; }
    IReadOnlyList<NetworkInfo> Networks { get; }
    IReadOnlyList<VolumeInfo> Volumes { get; }
    IReadOnlyList<StatInfo> Stats { get; }

    /// <summary>只刷容器列表（prune 后用，不必拉全套）。</summary>
    Task RefreshContainersAsync();

    /// <summary>只刷镜像列表。</summary>
    Task RefreshImagesAsync();

    /// <summary>只刷网络列表。</summary>
    Task RefreshNetworksAsync();

    /// <summary>只刷卷列表。</summary>
    Task RefreshVolumesAsync();
}
