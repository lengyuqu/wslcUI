using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;
using wslcUI.Services;

namespace wslcUI.ViewModels;

/// <summary>
/// 构建页的页面级 ViewModel（<c>wslc build -t</c>）。
///
/// <para>
/// 从 <see cref="MainViewModel"/> 拆出来的第二个子 VM。构建流程只需要
/// <see cref="IWslcClient.BuildImageAsync"/> 一条命令和一个取消令牌，
/// 与容器 / 镜像 / 网络 / 卷的状态完全无关。
/// </para>
///
/// <para>
/// 与外壳的耦合全部走上抛：忙碌联动进度条（<see cref="BusyChanged"/>）、
/// 状态栏文案（<see cref="StatusRequested"/>）、错误格式化（<see cref="ErrorRaised"/>）、
/// 成功时收起提示条（<see cref="Succeeded"/>）。子 VM 不碰 InfoBar 的具体状态。
/// </para>
/// </summary>
public sealed partial class BuildViewModel : ObservableObject
{
    private readonly IWslcClient _client;

    /// <summary>忙碌状态变化（联动外壳的进度条与全局命令可用性）。</summary>
    public event EventHandler<bool>? BusyChanged;

    /// <summary>请求写状态栏文本。</summary>
    public event EventHandler<string>? StatusRequested;

    /// <summary>请求收起提示条（操作成功）。</summary>
    public event EventHandler? Succeeded;

    /// <summary>请求按统一规则报错。</summary>
    public event EventHandler<Exception>? ErrorRaised;

    /// <summary>Owner window handle，FolderPicker 必须挂在窗口上（WinUI 3 unpackaged）。</summary>
    public IntPtr OwnerHandle { get; set; }

    [ObservableProperty] public partial string BuildContext { get; set; } = "";
    [ObservableProperty] public partial string BuildTag { get; set; } = "myimage:latest";
    [ObservableProperty] public partial string BuildOutput { get; set; } = "";

    [ObservableProperty] public partial bool HasBuildOutput { get; set; }
    [ObservableProperty] public partial bool CanBuild { get; set; }
    [ObservableProperty] public partial bool IsBuilding { get; set; }

    // 构建自己的取消链：与主刷新互不干扰（同主 VM 的 BeginOp 语义）。
    private CancellationTokenSource? _cts;

    public BuildViewModel(IWslcClient client) => _client = client;

    partial void OnBuildOutputChanged(string value) => HasBuildOutput = !string.IsNullOrEmpty(value);

    partial void OnBuildContextChanged(string value) => RecomputeCanBuild();

    partial void OnBuildTagChanged(string value) => RecomputeCanBuild();

    partial void OnIsBuildingChanged(bool value) => RecomputeCanBuild();

    private void RecomputeCanBuild() =>
        CanBuild = !IsBuilding
                   && !string.IsNullOrWhiteSpace(BuildContext)
                   && !string.IsNullOrWhiteSpace(BuildTag);

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

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsBuilding = true;
        BusyChanged?.Invoke(this, true);
        StatusRequested?.Invoke(this, $"构建 {tag} …");
        BuildOutput = "";
        try
        {
            var progress = new Progress<string>(line =>
            {
                BuildOutput += line + "\n";
                StatusRequested?.Invoke(this, $"构建 {tag}: {line}");
            });
            await _client.BuildImageAsync(ctx, tag, progress, ct);
            StatusRequested?.Invoke(this, $"镜像 {tag} 构建完成");
            Succeeded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ErrorRaised?.Invoke(this, ex);
        }
        finally
        {
            IsBuilding = false;
            BusyChanged?.Invoke(this, false);
        }
    }
}
