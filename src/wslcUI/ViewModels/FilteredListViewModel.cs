using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using wslcUI.Services;

namespace wslcUI.ViewModels;

/// <summary>
/// 「可搜索 / 可排序 / 保持选中」的列表页基类。
///
/// <para>
/// 镜像、网络、卷、统计四页在拆分前共用 <c>MainViewModel.ApplyFilter()</c> —— 一次遍历
/// 五个集合做「过滤 → 排序 → 差量同步 → 选中项按业务键重指 → HasXxx」。那套逻辑本身
/// 是对的，只是不该住在一个类里：任何一页要加字段都得改同一个方法。
/// </para>
///
/// <para>
/// 把这段机制收进基类后，每个列表页只剩四件事要回答：数据源是谁、怎么匹配搜索、
/// 每列按什么排序、哪一列是「业务键」（刷新后靠它把选中项指回新实例）。
/// </para>
///
/// <para>
/// ⚠️ <see cref="Items"/> 刻意用差量同步（<see cref="MainViewModel.SyncInView{T}"/>）而不是
/// 整体替换：ListView 的 SelectedItem 是 TwoWay 绑定，整体换实例会让选中闪烁、
/// 滚动位置回到顶部。
/// </para>
/// </summary>
public abstract partial class FilteredListViewModel<T> : ObservableObject where T : class
{
    /// <summary>视图集合（列表绑定它，原始集合只作数据源）。</summary>
    [ObservableProperty] public partial ObservableCollection<T> Items { get; set; } = new();

    [ObservableProperty] public partial bool HasItems { get; set; }

    [ObservableProperty] public partial string SearchText { get; set; } = "";

    [ObservableProperty] public partial string SortedColumn { get; set; } = "";

    [ObservableProperty] public partial bool SortDescending { get; set; }

    /// <summary>当前选中行。刷新后按 <see cref="BusinessKeyOf"/> 指回新实例。</summary>
    [ObservableProperty] public partial T? SelectedItem { get; set; }

    /// <summary>排序摘要（工具条上的「排序 名称 ↑」）。</summary>
    [ObservableProperty] public partial string SortSummary { get; set; } = "默认";

    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>是否有选中行（XAML 里做按钮/详情可见性判断用，不能写表达式）。</summary>
    public bool HasSelected => SelectedItem is not null;

    // ---- 子类回答的四个问题 ----

    /// <summary>数据源（通常是 <see cref="IResourceAccess"/> 上的只读视图）。</summary>
    protected abstract IEnumerable<T> Source { get; }

    /// <summary>搜索匹配。query 已 Trim 且非空。</summary>
    protected abstract bool IsMatch(T item, string query);

    /// <summary>取某一列的排序键。<paramref name="column"/> 是表头按钮传来的列名。</summary>
    protected abstract string SortKeyOf(T item, string column);

    /// <summary>该列是否按「量级」排序（单位感知，见 <see cref="NaturalSortKey"/>）。</summary>
    protected virtual bool IsNumericColumn(string column) => false;

    /// <summary>业务键：刷新会换成新模型实例，靠它在视图里把选中项指回去。</summary>
    protected abstract string BusinessKeyOf(T item);

    // ---- 钩子 ----

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsSearching));
        Refresh();
    }

    partial void OnSortDescendingChanged(bool value) => Refresh();

    partial void OnSelectedItemChanged(T? value)
    {
        OnPropertyChanged(nameof(HasSelected));
        OnSelectionChanged(value);
    }

    /// <summary>选中项变化。子类可在此重算「按钮是否可点」。</summary>
    protected virtual void OnSelectionChanged(T? value) { }

    /// <summary>每次重建视图后回调（子类可在此更新计数文案）。</summary>
    protected virtual void OnRefreshed() { }

    /// <summary>
    /// 重建视图集合。集合变了、搜索词变了、排序列变了都走这里。
    /// 幂等 —— 调用者不必关心「是不是已经刷过」。
    /// </summary>
    public void Refresh()
    {
        var q = (SearchText ?? "").Trim();

        // 同步前记下选中项的业务键：每轮刷新都是**新模型实例**，同步后旧实例
        // 必然已不在视图里，只能靠键重新指回；指不到才清空（该行真消失了）。
        var keep = SelectedItem is null ? null : BusinessKeyOf(SelectedItem);

        var visible = OrderBy(
            Source.Where(x => IsMatch(x, q)),
            x => SortKeyOf(x, SortedColumn),
            IsNumericColumn(SortedColumn),
            SortDescending);

        MainViewModel.SyncInView(Items, visible);

        if (SelectedItem is null || !Items.Contains(SelectedItem))
            SelectedItem = keep is null
                ? null
                : visible.FirstOrDefault(x => BusinessKeyOf(x) == keep);

        HasItems = Items.Count > 0;
        OnRefreshed();
    }

    /// <summary>表头点击：同一列切换升降序，换列则从升序开始。</summary>
    [RelayCommand]
    private void SortBy(string? column)
    {
        if (string.IsNullOrWhiteSpace(column)) return;

        if (string.Equals(SortedColumn, column, StringComparison.OrdinalIgnoreCase))
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortedColumn = column;
            SortDescending = false;
        }

        SortSummary = $"{LabelOf(column)} {(SortDescending ? "↓" : "↑")}";
        Refresh();
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    // ---- 与 MainViewModel 同名同实现的工具（拆出来时逐字搬，避免两套行为）----

    private static List<TItem> OrderBy<TItem>(IEnumerable<TItem> source, Func<TItem, string> selector,
                                              bool numeric, bool descending)
    {
        var list = source.ToList();
        var factor = descending ? -1 : 1;
        list.Sort((a, b) =>
        {
            var x = selector(a) ?? "";
            var y = selector(b) ?? "";
            // 单位感知折算："451MB" 与 "1.5GB"、"2 周前" 与 "3 个月前" 都必须按真实
            // 量级比，只取前导数字会把两者都排反（见 NaturalSortKey 的类注释）。
            var cmp = numeric
                ? NaturalSortKey.Rank(x).CompareTo(NaturalSortKey.Rank(y))
                : string.Compare(x, y, CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
            // List.Sort 不稳定：数值相等时必须再按原文比一次，
            // 否则同值行的次序每刷一次换一个样。
            if (cmp == 0 && numeric)
                cmp = string.Compare(x, y, CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
            return cmp * factor;
        });
        return list;
    }

    private static string LabelOf(string column) => column switch
    {
        "Name" => "名称",
        "Image" => "镜像",
        "Status" => "状态",
        "Ports" => "端口",
        "Cpu" => "CPU",
        "CreatedAt" => "创建",
        "MountListCell" => "卷",
        "Repository" => "仓库",
        "Tag" => "标签",
        "Size" => "大小",
        "Driver" => "驱动",
        "Scope" => "作用域",
        "Mountpoint" => "挂载点",
        "Container" => "容器",
        "Mem" => "内存",
        "MemPercent" => "内存%",
        "NetIo" => "网络 I/O",
        "Pids" => "PID",
        _ => column,
    };
}
