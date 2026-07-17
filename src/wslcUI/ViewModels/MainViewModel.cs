using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using wslcUI.Models;
using wslcUI.Services;

namespace wslcUI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IWslcClient _client;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "就绪";
    [ObservableProperty] private ObservableCollection<ContainerInfo> _containers = new();
    [ObservableProperty] private ObservableCollection<ImageInfo> _images = new();
    [ObservableProperty] private ObservableCollection<NetworkInfo> _networks = new();
    [ObservableProperty] private ObservableCollection<VolumeInfo> _volumes = new();
    [ObservableProperty] private ContainerInfo? _selectedContainer;
    [ObservableProperty] private ImageInfo? _selectedImage;
    [ObservableProperty] private NetworkInfo? _selectedNetwork;
    [ObservableProperty] private VolumeInfo? _selectedVolume;
    [ObservableProperty] private string _pullReference = "alpine:latest";
    [ObservableProperty] private string _newNetworkName = "";
    [ObservableProperty] private string _newVolumeName = "";
    [ObservableProperty] private string _logs = "";

    public MainViewModel(IWslcClient client) => _client = client;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        Status = "刷新中…";
        try
        {
            var containers = await _client.ListContainersAsync();
            Containers = new ObservableCollection<ContainerInfo>(containers);

            var images = await _client.ListImagesAsync();
            Images = new ObservableCollection<ImageInfo>(images);

            var networks = await _client.ListNetworksAsync();
            Networks = new ObservableCollection<NetworkInfo>(networks);

            var volumes = await _client.ListVolumesAsync();
            Volumes = new ObservableCollection<VolumeInfo>(volumes);

            Status = $"已加载 {Containers.Count} 容器 / {Images.Count} 镜像 / {Networks.Count} 网络 / {Volumes.Count} 卷";
        }
        catch (System.Exception ex)
        {
            Status = $"错误: {ex.Message}";
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
        IsBusy = true;
        Status = $"启动 {SelectedContainer.Name} …";
        try
        {
            await _client.StartAsync(SelectedContainer.Name);
            await RefreshAsync();
        }
        catch (System.Exception ex)
        {
            Status = $"错误: {ex.Message}";
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
        IsBusy = true;
        Status = $"停止 {SelectedContainer.Name} …";
        try
        {
            await _client.StopAsync(SelectedContainer.Name);
            await RefreshAsync();
        }
        catch (System.Exception ex)
        {
            Status = $"错误: {ex.Message}";
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
        IsBusy = true;
        Status = $"拉取 {reference} …";
        try
        {
            var progress = new Progress<(string Status, long Current, long Total)>(
                p => Status = $"拉取 {p.Status} ({p.Current}/{p.Total})");
            await _client.PullImageAsync(reference, progress);
            await RefreshAsync();
        }
        catch (System.Exception ex)
        {
            Status = $"错误: {ex.Message}";
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
        IsBusy = true;
        Status = $"删除容器 {SelectedContainer.Name} …";
        try
        {
            await _client.DeleteContainerAsync(SelectedContainer.Name);
            await RefreshAsync();
        }
        catch (System.Exception ex)
        {
            Status = $"错误: {ex.Message}";
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
        IsBusy = true;
        var reference = $"{SelectedImage.Repository}:{SelectedImage.Tag}";
        Status = $"删除镜像 {reference} …";
        try
        {
            await _client.DeleteImageAsync(reference);
            await RefreshAsync();
        }
        catch (System.Exception ex)
        {
            Status = $"错误: {ex.Message}";
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
        IsBusy = true;
        Status = $"读取 {SelectedContainer.Name} 日志 …";
        try
        {
            Logs = await _client.GetLogsAsync(SelectedContainer.Name);
            Status = $"{SelectedContainer.Name} 日志已加载";
        }
        catch (System.Exception ex)
        {
            Status = $"错误: {ex.Message}";
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
        IsBusy = true;
        Status = $"创建网络 {name} …";
        try
        {
            await _client.CreateNetworkAsync(name);
            NewNetworkName = "";
            await RefreshNetworksAsync();
        }
        catch (System.Exception ex)
        {
            Status = $"错误: {ex.Message}";
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
        IsBusy = true;
        Status = $"删除网络 {SelectedNetwork.Name} …";
        try
        {
            await _client.RemoveNetworkAsync(SelectedNetwork.Name);
            await RefreshNetworksAsync();
        }
        catch (System.Exception ex)
        {
            Status = $"错误: {ex.Message}";
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
        IsBusy = true;
        Status = $"创建卷 {name} …";
        try
        {
            await _client.CreateVolumeAsync(name);
            NewVolumeName = "";
            await RefreshVolumesAsync();
        }
        catch (System.Exception ex)
        {
            Status = $"错误: {ex.Message}";
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
        IsBusy = true;
        Status = $"删除卷 {SelectedVolume.Name} …";
        try
        {
            await _client.RemoveVolumeAsync(SelectedVolume.Name);
            await RefreshVolumesAsync();
        }
        catch (System.Exception ex)
        {
            Status = $"错误: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshNetworksAsync()
    {
        var n = await _client.ListNetworksAsync();
        Networks = new ObservableCollection<NetworkInfo>(n);
    }

    private async Task RefreshVolumesAsync()
    {
        var v = await _client.ListVolumesAsync();
        Volumes = new ObservableCollection<VolumeInfo>(v);
    }
}
