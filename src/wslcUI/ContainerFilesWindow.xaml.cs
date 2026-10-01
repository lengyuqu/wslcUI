using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using WinRT.Interop;
using wslcUI.Models;
using wslcUI.Services;
using wslcUI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Windows.System;

namespace wslcUI;

/// <summary>
/// 容器内文件浏览窗口（Container File Explorer 的**子集**：浏览 / 上传 / 下载 / 删除）。
///
/// <para>
/// 独立窗口而不是嵌进主窗口，理由与 <see cref="TerminalWindow"/> 相同：文件浏览有自己的
/// 导航状态与生命周期，塞进主窗口的三栏布局会把「选中容器」和「选中文件」两套选择纠缠在一起。
/// </para>
///
/// <para>
/// 对话框与文件选择器都**必须挂本窗口**：unpackaged WinUI 3 里 ContentDialog 不设 XamlRoot
/// 会直接抛，FileOpenPicker/FileSavePicker 不 InitializeWithWindow 也是。注意不能用
/// <c>DialogService</c> —— 它取的是 <c>App.MainWindow</c> 的 XamlRoot。
/// </para>
/// </summary>
public sealed partial class ContainerFilesWindow : Window
{
    public ContainerFilesViewModel ViewModel { get; }

    public ContainerFilesWindow(string container)
    {
        this.InitializeComponent();
        Title = $"文件 — {container}";
        this.AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 620));

        ViewModel = new ContainerFilesViewModel(
            App.Services.GetRequiredService<IWslcClient>(), container)
        {
            // 用本窗口自己的 XamlRoot 弹确认框（DialogService 会挂到主窗口上）
            ConfirmAsync = ConfirmAsync,
        };

        // Window 没有 Loaded 事件，用 Activated（并在首次触发后解绑）
        this.Activated += ContainerFilesWindow_Activated;
    }

    private async void ContainerFilesWindow_Activated(object sender, WindowActivatedEventArgs e)
    {
        this.Activated -= ContainerFilesWindow_Activated;
        await ViewModel.RefreshCommand.ExecuteAsync(null);
    }

    private IntPtr Hwnd => WindowNative.GetWindowHandle(this);

    /// <summary>本窗口范围内的确认对话框。</summary>
    private async Task<bool> ConfirmAsync(string title, string message)
    {
        if (Content is not FrameworkElement fe || fe.XamlRoot is null) return true;
        var dialog = new ContentDialog
        {
            XamlRoot = fe.XamlRoot,
            Title = title,
            Content = message,
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void PathBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        if (ViewModel.NavigateCommand.CanExecute(null))
            ViewModel.NavigateCommand.Execute(null);
    }

    /// <summary>双击：目录进入；文件不处理（下载走按钮，避免误触弹保存框）。</summary>
    private async void EntriesList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel.OpenSelectedCommand.CanExecute(null))
            await ViewModel.OpenSelectedCommand.ExecuteAsync(null);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.DeleteCommand.CanExecute(null))
            await ViewModel.DeleteCommand.ExecuteAsync(null);
    }

    /// <summary>上传到**当前目录**：wslc 的 cp 不能指定目标文件名（见 ViewModel 注释）。</summary>
    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, Hwnd);
        picker.ViewMode = PickerViewMode.List;
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add("*");

        var files = await picker.PickMultipleFilesAsync();
        if (files is null || files.Count == 0) return;
        await ViewModel.UploadAsync(files.Select(f => f.Path).ToList());
    }

    /// <summary>
    /// 下载：文件走「另存为」，目录走「选文件夹」（目录名沿用原名字，
    /// 因为 `container cp` 只认目标路径本身，不认改名）。
    /// </summary>
    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedEntry is not { } entry) return;

        if (entry.IsDirectory)
        {
            var folderPicker = new FolderPicker();
            InitializeWithWindow.Initialize(folderPicker, Hwnd);
            folderPicker.FileTypeFilter.Add("*");
            var folder = await folderPicker.PickSingleFolderAsync();
            if (folder is null) return;
            await ViewModel.DownloadAsync(entry, System.IO.Path.Combine(folder.Path, entry.Name));
            return;
        }

        var savePicker = new FileSavePicker();
        InitializeWithWindow.Initialize(savePicker, Hwnd);
        savePicker.SuggestedStartLocation = PickerLocationId.Downloads;
        savePicker.SuggestedFileName = entry.Name;
        savePicker.FileTypeChoices.Add("所有文件", new List<string> { "." });
        var file = await savePicker.PickSaveFileAsync();
        if (file is null) return;
        await ViewModel.DownloadAsync(entry, file.Path);
    }
}
