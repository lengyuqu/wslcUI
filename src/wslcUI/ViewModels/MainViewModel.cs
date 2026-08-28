using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using wslcUI.Models;
using wslcUI.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace wslcUI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IWslcClient _client;
    private readonly IDialogService? _dialogs;
    private CancellationTokenSource? _opCts;

    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "就绪";
    [ObservableProperty] public partial ObservableCollection<ContainerInfo> Containers { get; set; } = new();
    [ObservableProperty] public partial ObservableCollection<ImageInfo> Images { get; set; } = new();
    [ObservableProperty] public partial ObservableCollection<NetworkInfo> Networks { get; set; } = new();
    [ObservableProperty] public partial ObservableCollection<VolumeInfo> Volumes { get; set; } = new();
    [ObservableProperty] public partial ObservableCollection<StatInfo> Stats { get; set; } = new();
    [ObservableProperty] public partial ContainerInfo? SelectedContainer { get; set; }
    [ObservableProperty] public partial ImageInfo? SelectedImage { get; set; }
    [ObservableProperty] public partial NetworkInfo? SelectedNetwork { get; set; }
    [ObservableProperty] public partial VolumeInfo? SelectedVolume { get; set; }
    [ObservableProperty] public partial string PullReference { get; set; } = "alpine:latest";
    [ObservableProperty] public partial string NewNetworkName { get; set; } = "";
    [ObservableProperty] public partial string NewVolumeName { get; set; } = "";
    [ObservableProperty] public partial string Logs { get; set; } = "";
    [ObservableProperty] public partial string BuildContext { get; set; } = "";
    [ObservableProperty] public partial string BuildTag { get; set; } = "myimage:latest";
    [ObservableProperty] public partial string BuildOutput { get; set; } = "";

    // --- 错误 / 提示上屏（InfoBar）---
    [ObservableProperty] public partial bool IsInfoBarOpen { get; set; }
    [ObservableProperty] public partial string InfoMessage { get; set; } = "";
    [ObservableProperty] public partial InfoBarSeverity InfoSeverity { get; set; } = InfoBarSeverity.Informational;

    // --- 派生状态：列表是否有数据（空状态用）---
    [ObservableProperty] public partial bool HasContainers { get; set; }
    [ObservableProperty] public partial bool HasImages { get; set; }
    [ObservableProperty] public partial bool HasNetworks { get; set; }
    [ObservableProperty] public partial bool HasVolumes { get; set; }
    [ObservableProperty] public partial bool HasStats { get; set; }
    [ObservableProperty] public partial bool HasBuildOutput { get; set; }

    // --- 派生状态：是否有选中项（按钮启用用）---
    [ObservableProperty] public partial bool HasSelectedContainer { get; set; }
    [ObservableProperty] public partial bool HasSelectedImage { get; set; }
    [ObservableProperty] public partial bool HasSelectedNetwork { get; set; }
    [ObservableProperty] public partial bool HasSelectedVolume { get; set; }

    // --- 派生状态：按钮是否可点（选中 / 输入 / 空闲 的组合）---
    [ObservableProperty] public partial bool CanActOnContainer { get; set; }
    [ObservableProperty] public partial bool CanActOnImage { get; set; }
    [ObservableProperty] public partial bool CanActOnNetwork { get; set; }
    [ObservableProperty] public partial bool CanActOnVolume { get; set; }
    [ObservableProperty] public partial bool CanCreateNetwork { get; set; }
    [ObservableProperty] public partial bool CanCreateVolume { get; set; }
    [ObservableProperty] public partial bool CanPull { get; set; }
    [ObservableProperty] public partial bool CanBuild { get; set; }

    /// <summary>Owner window handle, set by MainWindow so the FolderPicker can parent.</summary>
    public IntPtr OwnerHandle { get; set; }

    public MainViewModel(IWslcClient client, IDialogService? dialogs = null)
    {
        _client = client;
        _dialogs = dialogs;
        RecomputeCanStates();
    }

    // ---------------- 属性变更钩子（派生状态联动）----------------

    partial void OnIsBusyChanged(bool value) => RecomputeCanStates();
    partial void OnSelectedContainerChanged(ContainerInfo? value) { HasSelectedContainer = value is not null; RecomputeCanStates(); }
    partial void OnSelectedImageChanged(ImageInfo? value) { HasSelectedImage = value is not null; RecomputeCanStates(); }
    partial void OnSelectedNetworkChanged(NetworkInfo? value) { HasSelectedNetwork = value is not null; RecomputeCanStates(); }
    partial void OnSelectedVolumeChanged(VolumeInfo? value) { HasSelectedVolume = value is not null; RecomputeCanStates(); }

    partial void OnContainersChanged(ObservableCollection<ContainerInfo> value) => HasContainers = value is { Count: > 0 };
    partial void OnImagesChanged(ObservableCollection<ImageInfo> value) => HasImages = value is { Count: > 0 };
    partial void OnNetworksChanged(ObservableCollection<NetworkInfo> value) => HasNetworks = value is { Count: > 0 };
    partial void OnVolumesChanged(ObservableCollection<VolumeInfo> value) => HasVolumes = value is { Count: > 0 };
    partial void OnStatsChanged(ObservableCollection<StatInfo> value) => HasStats = value is { Count: > 0 };
    partial void OnBuildOutputChanged(string value) => HasBuildOutput = !string.IsNullOrEmpty(value);

    partial void OnPullReferenceChanged(string value) => RecomputeCanStates();
    partial void OnNewNetworkNameChanged(string value) => RecomputeCanStates();
    partial void OnNewVolumeNameChanged(string value) => RecomputeCanStates();
    partial void OnBuildContextChanged(string value) => RecomputeCanStates();
    partial void OnBuildTagChanged(string value) => RecomputeCanStates();

    private void RecomputeCanStates()
    {
        CanActOnContainer = HasSelectedContainer && !IsBusy;
        CanActOnImage = HasSelectedImage && !IsBusy;
        CanActOnNetwork = HasSelectedNetwork && !IsBusy;
        CanActOnVolume = HasSelectedVolume && !IsBusy;
        CanCreateNetwork = !IsBusy && !string.IsNullOrWhiteSpace(NewNetworkName);
        CanCreateVolume = !IsBusy && !string.IsNullOrWhiteSpace(NewVolumeName);
        CanPull = !IsBusy && !string.IsNullOrWhiteSpace(PullReference);
        CanBuild = !IsBusy && !string.IsNullOrWhiteSpace(BuildContext) && !string.IsNullOrWhiteSpace(BuildTag);
    }

    // ---------------- 操作基础设施 ----------------

    /// <summary>开始一次新操作：取消上一次未完成的操作，并返回本次的取消令牌。</summary>
    private CancellationToken BeginOp()
    {
        _opCts?.Cancel();
        _opCts = new CancellationTokenSource();
        return _opCts.Token;
    }

    /// <summary>当前操作的取消令牌（供内部子刷新使用）。</summary>
    private CancellationToken CurrentToken => _opCts?.Token ?? CancellationToken.None;

    private void ShowError(System.Exception ex)
    {
        string msg;
        InfoBarSeverity severity;
        if (ex is OperationCanceledException)
        {
            msg = "操作已取消。";
            severity = InfoBarSeverity.Informational;
            Status = "已取消";
        }
        else
        {
            // 展开 AggregateException，拿到最有信息量的内层消息。
            var inner = ex is System.AggregateException ae && ae.InnerException is not null
                ? ae.InnerException
                : ex;
            msg = inner.Message;
            severity = InfoBarSeverity.Error;
            Status = "错误";
        }

        InfoMessage = msg;
        InfoSeverity = severity;
        IsInfoBarOpen = true;
    }

    [RelayCommand]
    private void DismissInfo() => IsInfoBarOpen = false;

    // ---------------- 命令 ----------------

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var ct = BeginOp();
        IsBusy = true;
        Status = "刷新中…";
        try
        {
            var containers = await _client.ListContainersAsync(ct);
            Containers = new ObservableCollection<ContainerInfo>(containers);

            var images = await _client.ListImagesAsync(ct);
            Images = new ObservableCollection<ImageInfo>(images);

            var networks = await _client.ListNetworksAsync(ct);
            Networks = new ObservableCollection<NetworkInfo>(networks);

            var volumes = await _client.ListVolumesAsync(ct);
            Volumes = new ObservableCollection<VolumeInfo>(volumes);

            Status = $"已加载 {Containers.Count} 容器 / {Images.Count} 镜像 / {Networks.Count} 网络 / {Volumes.Count} 卷";
            IsInfoBarOpen = false;
        }
        catch (System.Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (SelectedContainer is null) return;
        var name = SelectedContainer.Name;
        var ct = BeginOp();
        IsBusy = true;
        Status = $"启动 {name} …";
        try
        {
            await _client.StartAsync(name, ct);
            await RefreshAsync();
        }
        catch (System.Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (SelectedContainer is null) return;
        var name = SelectedContainer.Name;
        var ct = BeginOp();
        IsBusy = true;
        Status = $"停止 {name} …";
        try
        {
            await _client.StopAsync(name, ct);
            await RefreshAsync();
        }
        catch (System.Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task PullAsync()
    {
        var reference = PullReference.Trim();
        if (string.IsNullOrWhiteSpace(reference)) return;
        var ct = BeginOp();
        IsBusy = true;
        Status = $"拉取 {reference} …";
        try
        {
            var progress = new Progress<(string Status, long Current, long Total)>(
                p => Status = $"拉取 {p.Status} ({p.Current}/{p.Total})");
            await _client.PullImageAsync(reference, progress, ct);
            await RefreshAsync();
        }
        catch (System.Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteContainerAsync()
    {
        if (SelectedContainer is null) return;
        var name = SelectedContainer.Name;
        if (_dialogs is not null &&
            !await _dialogs.ConfirmAsync("删除容器", $"确定删除容器「{name}」吗？此操作不可撤销。"))
            return;

        var ct = BeginOp();
        IsBusy = true;
        Status = $"删除容器 {name} …";
        try
        {
            await _client.DeleteContainerAsync(name, ct);
            SelectedContainer = null;
            await RefreshAsync();
        }
        catch (System.Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteImageAsync()
    {
        if (SelectedImage is null) return;
        var reference = $"{SelectedImage.Repository}:{SelectedImage.Tag}";
        if (_dialogs is not null &&
            !await _dialogs.ConfirmAsync("删除镜像", $"确定删除镜像「{reference}」吗？此操作不可撤销。"))
            return;

        var ct = BeginOp();
        IsBusy = true;
        Status = $"删除镜像 {reference} …";
        try
        {
            await _client.DeleteImageAsync(reference, ct);
            SelectedImage = null;
            await RefreshAsync();
        }
        catch (System.Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task PickBuildContextAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        // WinUI 3 unpackaged: the picker must be parented to our window.
        if (OwnerHandle != IntPtr.Zero)
            InitializeWithWindow.Initialize(picker, OwnerHandle);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
            BuildContext = folder.Path;
    }

    [RelayCommand]
    private async Task BuildImageAsync()
    {
        var ctx = BuildContext.Trim();
        var tag = BuildTag.Trim();
        if (string.IsNullOrWhiteSpace(ctx) || string.IsNullOrWhiteSpace(tag)) return;
        var ct = BeginOp();
        IsBusy = true;
        Status = $"构建 {tag} …";
        BuildOutput = "";
        try
        {
            var progress = new Progress<string>(line =>
            {
                BuildOutput += line + "\n";
                Status = $"构建 {tag}: {line}";
            });
            await _client.BuildImageAsync(ctx, tag, progress, ct);
            Status = $"镜像 {tag} 构建完成";
            IsInfoBarOpen = false;
        }
        catch (System.Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ShowLogsAsync()
    {
        if (SelectedContainer is null) return;
        var ct = BeginOp();
        IsBusy = true;
        Status = $"读取 {SelectedContainer.Name} 日志 …";
        try
        {
            Logs = await _client.GetLogsAsync(SelectedContainer.Name, ct);
            Status = $"{SelectedContainer.Name} 日志已加载";
            IsInfoBarOpen = false;
        }
        catch (System.Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshStatsAsync()
    {
        var ct = BeginOp();
        IsBusy = true;
        Status = "读取资源统计…";
        try
        {
            // Stats 是一次性快照，刻意不并入主 RefreshCommand（避免潜在卡顿波及主刷新）。
            var stats = await _client.GetStatsAsync(ct);
            Stats = new ObservableCollection<StatInfo>(stats);
            Status = $"已加载 {Stats.Count} 个容器的资源统计";
            IsInfoBarOpen = false;
        }
        catch (System.Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CreateNetworkAsync()
    {
        var name = NewNetworkName.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        var ct = BeginOp();
        IsBusy = true;
        Status = $"创建网络 {name} …";
        try
        {
            await _client.CreateNetworkAsync(name, ct);
            NewNetworkName = "";
            await RefreshNetworksAsync();
            Status = $"网络 {name} 已创建";
            IsInfoBarOpen = false;
        }
        catch (System.Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RemoveNetworkAsync()
    {
        if (SelectedNetwork is null) return;
        var name = SelectedNetwork.Name;
        if (_dialogs is not null &&
            !await _dialogs.ConfirmAsync("删除网络", $"确定删除网络「{name}」吗？此操作不可撤销。"))
            return;

        var ct = BeginOp();
        IsBusy = true;
        Status = $"删除网络 {name} …";
        try
        {
            await _client.RemoveNetworkAsync(name, ct);
            SelectedNetwork = null;
            await RefreshNetworksAsync();
        }
        catch (System.Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CreateVolumeAsync()
    {
        var name = NewVolumeName.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        var ct = BeginOp();
        IsBusy = true;
        Status = $"创建卷 {name} …";
        try
        {
            await _client.CreateVolumeAsync(name, ct);
            NewVolumeName = "";
            await RefreshVolumesAsync();
            Status = $"卷 {name} 已创建";
            IsInfoBarOpen = false;
        }
        catch (System.Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RemoveVolumeAsync()
    {
        if (SelectedVolume is null) return;
        var name = SelectedVolume.Name;
        if (_dialogs is not null &&
            !await _dialogs.ConfirmAsync("删除卷", $"确定删除卷「{name}」吗？此操作不可撤销。"))
            return;

        var ct = BeginOp();
        IsBusy = true;
        Status = $"删除卷 {name} …";
        try
        {
            await _client.RemoveVolumeAsync(name, ct);
            SelectedVolume = null;
            await RefreshVolumesAsync();
        }
        catch (System.Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshNetworksAsync()
    {
        var n = await _client.ListNetworksAsync(CurrentToken);
        Networks = new ObservableCollection<NetworkInfo>(n);
    }

    private async Task RefreshVolumesAsync()
    {
        var v = await _client.ListVolumesAsync(CurrentToken);
        Volumes = new ObservableCollection<VolumeInfo>(v);
    }
}
