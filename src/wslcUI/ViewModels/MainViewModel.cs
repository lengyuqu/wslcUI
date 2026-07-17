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
    [ObservableProperty] private ContainerInfo? _selectedContainer;

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

            Status = $"已加载 {Containers.Count} 个容器 / {Images.Count} 个镜像";
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
        IsBusy = true;
        Status = "拉取 alpine:latest …";
        try
        {
            var progress = new Progress<(string Status, long Current, long Total)>(
                p => Status = $"拉取 {p.Status} ({p.Current}/{p.Total})");
            await _client.PullImageAsync("alpine:latest", progress);
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
}
