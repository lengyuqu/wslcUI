using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using wslcUI.Models;
using wslcUI.Services;

namespace wslcUI.ViewModels;

/// <summary>
/// 容器内文件浏览窗口的 ViewModel（对标 Docker Desktop 的 Container File Explorer，
/// 是它的**子集**：浏览 / 上传 / 下载 / 删除；不含在线编辑）。
///
/// <para>
/// 确认对话框**不注入 <see cref="IDialogService"/>**：那个实现从 <c>App.MainWindow</c>
/// 取 XamlRoot，而本窗口是独立窗口，挂上去会弹到主窗口甚至静默失败。
/// 改由窗口在构造时设置 <see cref="ConfirmAsync"/>（用它自己的 <c>Content.XamlRoot</c>）。
/// </para>
/// </summary>
public partial class ContainerFilesViewModel : ObservableObject
{
    private readonly IWslcClient _client;
    private CancellationTokenSource? _opCts;

    public string ContainerName { get; }

    /// <summary>由窗口注入的确认回调（标题, 正文）→ 是否继续。null 时直接继续。</summary>
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    [ObservableProperty] public partial string CurrentPath { get; set; } = "/";
    [ObservableProperty] public partial ObservableCollection<ContainerFileEntry> Entries { get; set; } = new();
    [ObservableProperty] public partial ContainerFileEntry? SelectedEntry { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "就绪";
    [ObservableProperty] public partial bool HasEntries { get; set; }
    [ObservableProperty] public partial bool HasSelectedEntry { get; set; }
    [ObservableProperty] public partial bool CanGoUp { get; set; }
    [ObservableProperty] public partial string ErrorMessage { get; set; } = "";
    [ObservableProperty] public partial bool IsErrorOpen { get; set; }

    public ContainerFilesViewModel(IWslcClient client, string containerName)
    {
        _client = client;
        ContainerName = containerName;
        CanGoUp = ParentOf(CurrentPath) != CurrentPath;
    }

    private CancellationToken BeginOp()
    {
        _opCts?.Cancel();
        _opCts = new CancellationTokenSource();
        return _opCts.Token;
    }

    partial void OnSelectedEntryChanged(ContainerFileEntry? value) =>
        HasSelectedEntry = value is not null;

    /// <summary>加载 <see cref="CurrentPath"/>。目录不存在时保留原路径并报错，不把 UI 卡在半路。</summary>
    private async Task LoadAsync(string path)
    {
        var ct = BeginOp();
        IsBusy = true;
        Status = $"读取 {path} …";
        try
        {
            var entries = await _client.ListDirectoryAsync(ContainerName, path, ct);
            CurrentPath = path;
            CanGoUp = ParentOf(path) != path;
            Entries = new ObservableCollection<ContainerFileEntry>(entries);
            HasEntries = Entries.Count > 0;
            SelectedEntry = null;
            IsErrorOpen = false;
            Status = $"{path} — {Entries.Count} 项";
        }
        catch (Exception ex)
        {
            // 路径无效时不切换 CurrentPath（用户仍看到旧目录），只报错。
            ErrorMessage = ex.Message;
            IsErrorOpen = true;
            Status = "读取失败";
            HasEntries = Entries.Count > 0;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(CurrentPath);

    /// <summary>导航到用户手输的路径（无效路径由 <see cref="LoadAsync"/> 报错并保持原目录）。</summary>
    [RelayCommand]
    private Task NavigateAsync() => LoadAsync(NormalizeInputPath(CurrentPath));

    [RelayCommand]
    private Task GoUpAsync()
    {
        var parent = ParentOf(CurrentPath);
        return parent == CurrentPath ? Task.CompletedTask : LoadAsync(parent);
    }

    /// <summary>双击 / 回车：目录则进入；文件则无操作（下载由按钮触发）。</summary>
    [RelayCommand]
    private Task OpenSelectedAsync()
    {
        if (SelectedEntry is not { IsDirectory: true } dir) return Task.CompletedTask;
        return LoadAsync(Combine(CurrentPath, dir.Name));
    }

    /// <summary>删除选中项（递归、不可撤销 —— 调用前必然已过确认）。</summary>
    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (SelectedEntry is not { } entry) return;
        var target = Combine(CurrentPath, entry.Name);

        if (ConfirmAsync is not null &&
            !await ConfirmAsync("删除",
                $"确定删除「{target}」吗？\n\n" +
                (entry.IsDirectory ? "这是**目录**，其下所有内容会被递归删除。" : "该文件会被删除。") +
                "\n此操作不可撤销。"))
            return;

        var ct = BeginOp();
        IsBusy = true;
        Status = $"删除 {target} …";
        try
        {
            await _client.DeletePathAsync(ContainerName, target, ct);
            await LoadAsync(CurrentPath);
            Status = $"已删除 {entry.Name}";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            IsErrorOpen = true;
            Status = "删除失败";
            IsBusy = false;
        }
    }

    /// <summary>
    /// 把本地路径上传到**当前目录**。因为 wslc 的 `container cp` 不支持指定目标文件名
    /// （见 <c>WslcCli.CopyToContainerAsync</c> 注释），文件名沿用本地文件名。
    /// </summary>
    public async Task UploadAsync(IReadOnlyList<string> localPaths)
    {
        if (localPaths.Count == 0) return;
        var ct = BeginOp();
        IsBusy = true;
        Status = $"上传 {localPaths.Count} 项到 {CurrentPath} …";
        try
        {
            foreach (var local in localPaths)
                await _client.CopyToContainerAsync(ContainerName, local, CurrentPath, ct);
            await LoadAsync(CurrentPath);
            Status = $"已上传 {localPaths.Count} 项到 {CurrentPath}";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            IsErrorOpen = true;
            Status = "上传失败";
            IsBusy = false;
        }
    }

    /// <summary>把容器内一个条目下载到本地**文件**路径（目录会被 <c>container cp</c> 递归拷出）。</summary>
    public async Task DownloadAsync(ContainerFileEntry entry, string localPath)
    {
        var ct = BeginOp();
        IsBusy = true;
        Status = $"下载 {entry.Name} …";
        try
        {
            await _client.CopyFromContainerAsync(
                ContainerName, Combine(CurrentPath, entry.Name), localPath, ct);
            Status = $"已下载到 {localPath}";
            IsErrorOpen = false;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            IsErrorOpen = true;
            Status = "下载失败";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---------------- 纯路径函数（可单测，无 UI 依赖）----------------

    /// <summary>
    /// 父目录。根 <c>/</c> 的父目录是自己（<see cref="CanGoUp"/> 据此为 false）。
    /// 容忍尾斜杠：<c>/a/b/</c> → <c>/a</c>。
    /// </summary>
    internal static string ParentOf(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/") return "/";
        var p = path.TrimEnd('/');
        if (p.Length == 0) return "/";
        var slash = p.LastIndexOf('/');
        return slash <= 0 ? "/" : p[..slash];
    }

    /// <summary>拼接目录与名字，避免出现 <c>//</c> 或漏掉分隔符。</summary>
    internal static string Combine(string dir, string name)
    {
        if (string.IsNullOrEmpty(dir)) return "/" + name;
        var d = dir.EndsWith('/') ? dir : dir + "/";
        return d + name;
    }

    /// <summary>
    /// 归一化用户手输的路径：去掉首尾空白、把连续斜杠压成一个、相对路径按根处理。
    /// 空串视为根。
    /// </summary>
    internal static string NormalizeInputPath(string? input)
    {
        var s = (input ?? "").Trim();
        if (s.Length == 0) return "/";
        if (!s.StartsWith('/')) s = "/" + s;
        while (s.Contains("//")) s = s.Replace("//", "/");
        if (s.Length == 1) return "/"; // 就是 "/"
        var trimmed = s.TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }
}
