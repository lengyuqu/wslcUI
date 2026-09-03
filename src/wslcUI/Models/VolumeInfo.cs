namespace wslcUI.Models;

using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

/// <summary>A wslc volume (guest/vhd backend). Mirrors `wslc volume list` output.</summary>
public class VolumeInfo : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private string _name = "";
    public string Name { get => _name; set { if (_name == value) return; _name = value; Raise(); } }

    private string _driver = "";
    public string Driver { get => _driver; set { if (_driver == value) return; _driver = value; Raise(); } }

    private string _mountpoint = "";
    public string Mountpoint { get => _mountpoint; set { if (_mountpoint == value) return; _mountpoint = value; Raise(); } }

    private IReadOnlyList<string> _usedBy = System.Array.Empty<string>();
    /// <summary>
    /// 已加载的容器中哪些引用了此卷（按 mount.Name 匹配 volume.Name 命中）。
    /// **只覆盖已 inspect 过的容器**——点过容器详情面板才会被 inspect 拉取。
    /// 选中卷时由 MainViewModel 扫描 Containers 集合回填。
    /// 实现 INPC 让 XAML OneWay 绑定（ItemsControl）正确刷新。
    /// </summary>
    public IReadOnlyList<string> UsedBy
    {
        get => _usedBy;
        set { if (ReferenceEquals(_usedBy, value)) return; _usedBy = value ?? System.Array.Empty<string>(); Raise(); Raise(nameof(HasUsedBy)); Raise(nameof(UsedBySummary)); }
    }

    public bool HasUsedBy => _usedBy.Count > 0;

    /// <summary>"2 个容器" / ""。空时让 UI 走空态文案而非显示 0。</summary>
    public string UsedBySummary => _usedBy.Count switch
    {
        0 => "",
        1 => "1 个容器",
        _ => $"{_usedBy.Count} 个容器",
    };
}


