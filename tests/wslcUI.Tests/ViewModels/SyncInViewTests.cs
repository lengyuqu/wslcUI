using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using wslcUI.ViewModels;
using Xunit;

namespace wslcUI.Tests.ViewModels;

/// <summary>
/// 视图集合的差量同步（<c>MainViewModel.SyncInView</c>）。
///
/// <para>
/// 「刷新不丢选中」整套行为都压在这个函数上：给 <c>Filtered*</c> 赋一个新集合实例，
/// ListView 就会清空 SelectedItem，而它是 TwoWay 绑定 —— null 回写进 ViewModel 后
/// 详情面板空白、<c>LogTarget</c> 被清、inspect 的取消令牌被回收，用户每刷一次
/// 都要重新点一遍行。这里钉住三条：
/// ① 内容严格跟随 desired；② 活下来的项<b>还是同一个实例</b>（绑定与选中才不断）；
/// ③ 不出现 Clear/Reset 这种逼列表整体重建的通知。
/// </para>
/// </summary>
public class SyncInViewTests
{
    private sealed class Row
    {
        public string Key { get; init; } = "";
        public override string ToString() => Key;
    }

    private static Row R(string key) => new() { Key = key };

    /// <summary>记录集合收到的通知种类。Reset 正是老写法（整体替换）的指纹。</summary>
    private static List<NotifyCollectionChangedAction> Track(ObservableCollection<Row> view)
    {
        var actions = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)view).CollectionChanged += (_, e) => actions.Add(e.Action);
        return actions;
    }

    [Fact]
    public void NoChangeProducesNoNotification()
    {
        var (a, b, c) = (R("a"), R("b"), R("c"));
        var view = new ObservableCollection<Row> { a, b, c };
        var actions = Track(view);

        MainViewModel.SyncInView(view, new[] { a, b, c });

        Assert.Empty(actions);
        Assert.Equal(new[] { "a", "b", "c" }, view.Select(v => v.Key));
    }

    [Fact]
    public void RemovedRowDropsOutAndSurvivorsKeepTheirInstances()
    {
        var (a, b, c) = (R("a"), R("b"), R("c"));
        var view = new ObservableCollection<Row> { a, b, c };
        var actions = Track(view);

        MainViewModel.SyncInView(view, new[] { a, c });

        Assert.Equal(new[] { a, c }, view);   // 集合相等按引用比较（Row 未重写 Equals）
        Assert.Single(actions);
        Assert.Equal(NotifyCollectionChangedAction.Remove, actions[0]);
    }

    [Fact]
    public void NewRowLandsAtItsSortedPosition()
    {
        var (a, b) = (R("a"), R("b"));
        var view = new ObservableCollection<Row> { b };

        MainViewModel.SyncInView(view, new[] { a, b });

        Assert.Same(a, view[0]);
        Assert.Same(b, view[1]);
    }

    [Fact]
    public void ReorderMovesInPlaceWithoutRebuilding()
    {
        var (a, b, c) = (R("a"), R("b"), R("c"));
        var view = new ObservableCollection<Row> { a, b, c };
        var actions = Track(view);

        MainViewModel.SyncInView(view, new[] { c, b, a });

        Assert.Same(c, view[0]);
        Assert.Same(b, view[1]);
        Assert.Same(a, view[2]);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);
        // 全量重排也不该出现 Add+Remove 的"删了再加"，那等于重建行。
        Assert.All(actions, act => Assert.NotEqual(NotifyCollectionChangedAction.Add, act));
    }

    [Fact]
    public void EmptyDesiredEmptiesTheView()
    {
        var (a, b) = (R("a"), R("b"));
        var view = new ObservableCollection<Row> { a, b };

        MainViewModel.SyncInView(view, Array.Empty<Row>());

        Assert.Empty(view);
    }

    [Fact]
    public void SelectedRowSurvivesAReorderedRefresh()
    {
        // 模拟一次正常刷新：容器实例由 UpdateContainersInPlace 按名复用，
        // 只有排序变了（比如 CPU 列降序）。选中项必须还在视图里。
        var (a, b, c) = (R("a"), R("b"), R("c"));
        var view = new ObservableCollection<Row> { a, b, c };
        var selected = a;

        MainViewModel.SyncInView(view, new[] { c, a, b });

        Assert.Contains(selected, view);
        Assert.Same(a, view[1]);
    }

    [Fact]
    public void SortFlipKeepsEveryRowIdentical()
    {
        var (a, b, c) = (R("a"), R("b"), R("c"));
        var view = new ObservableCollection<Row> { a, b, c };

        MainViewModel.SyncInView(view, new[] { c, b, a });
        MainViewModel.SyncInView(view, new[] { a, b, c });

        Assert.Equal(new[] { a, b, c }, view);
    }
}
