using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using wslcUI.Models;
using wslcUI.Services;

namespace wslcUI.ViewModels;

/// <summary>
/// 镜像页的页面级 ViewModel：列表 / 搜索 / 排序 / 拉取 / 删除 + **出向闭环**
/// （仓库登录、打标签、推送）。
///
/// <para>
/// 这是所有页面里最重的一块 —— 六条命令横跨「进」（pull）与「出」（tag / push /
/// login / logout）。拆出来后 MainViewModel 只剩容器相关的核心逻辑。
/// </para>
///
/// <para>
/// ⚠️ 密码一律走 <c>RegistryLoginAsync</c> 的 <c>--password-stdin</c> 通道，
/// **不存盘、不进日志、不进任何绑定目标**。密码由 UI 层的
/// <see cref="PromptPassword"/> 回调在栈上取到，用完即丢。
/// </para>
/// </summary>
public sealed partial class ImagesViewModel : FilteredListViewModel<ImageInfo>
{
    private readonly IWslcClient _client;
    private readonly IResourceAccess _res;
    private readonly IDialogService? _dialogs;

    /// <summary>忙碌状态变化（联动外壳进度条与其它命令）。</summary>
    public event EventHandler<bool>? BusyChanged;

    /// <summary>请求写状态栏文本。</summary>
    public event EventHandler<string>? StatusRequested;

    /// <summary>请求收起提示条（操作成功）。</summary>
    public event EventHandler? Succeeded;

    /// <summary>请求按统一规则报错。</summary>
    public event EventHandler<Exception>? ErrorRaised;

    /// <summary>请求弹提示条（Warning / Informational，非错误）。</summary>
    public event EventHandler<(string Message, Microsoft.UI.Xaml.Controls.InfoBarSeverity Severity)>? NotifyRequested;

    /// <summary>
    /// 密码输入回调。PasswordBox 无法双向绑定，只能由 UI 层弹 ContentDialog 取值；
    /// 回调必须是 <c>Task&lt;string?&gt;</c> —— 同步等待会当场死锁整个窗口。
    /// </summary>
    public Func<Task<string?>>? PromptPassword { get; set; }

    // ---- 拉取 ----
    [ObservableProperty] public partial string PullReference { get; set; } = "alpine:latest";

    // ---- 出向：仓库登录 / 打标签 / 推送 ----
    [ObservableProperty] public partial string RegistryServer { get; set; } = "";
    [ObservableProperty] public partial string RegistryUser { get; set; } = "";
    [ObservableProperty] public partial string TagTarget { get; set; } = "";

    /// <summary>登录结果提示（不显示密码）。</summary>
    [ObservableProperty] public partial string RegistryStatusText { get; set; } = "";

    [ObservableProperty] public partial string PushOutput { get; set; } = "";
    [ObservableProperty] public partial bool HasPushOutput { get; set; }

    // ---- 按钮可用性 ----
    [ObservableProperty] public partial bool CanPull { get; set; }
    [ObservableProperty] public partial bool CanAct { get; set; }
    [ObservableProperty] public partial bool CanPush { get; set; }
    [ObservableProperty] public partial bool CanTag { get; set; }
    [ObservableProperty] public partial bool IsWorking { get; set; }

    private CancellationTokenSource? _cts;
    private bool _globalBusy;

    public ImagesViewModel(IResourceAccess res, IWslcClient client, IDialogService? dialogs)
    {
        _res = res;
        _client = client;
        _dialogs = dialogs;
    }

    // ---- 基类要的四个答案 ----

    protected override IEnumerable<ImageInfo> Source => _res.Images;

    protected override bool IsMatch(ImageInfo item, string query) =>
        item.Repository.Contains(query, StringComparison.OrdinalIgnoreCase)
        || item.Tag.Contains(query, StringComparison.OrdinalIgnoreCase)
        || item.Size.Contains(query, StringComparison.OrdinalIgnoreCase)
        || item.Reference.Contains(query, StringComparison.OrdinalIgnoreCase);

    protected override string SortKeyOf(ImageInfo item, string column) => column switch
    {
        "Tag" => item.Tag,
        "Size" => item.Size,
        _ => item.Repository,
    };

    protected override bool IsNumericColumn(string column) => column == "Size";

    protected override string BusinessKeyOf(ImageInfo item) => item.Reference;

    // ---- 可用状态 ----

    protected override void OnSelectionChanged(ImageInfo? value) => RecomputeCanStates();

    partial void OnPullReferenceChanged(string value) => RecomputeCanStates();

    partial void OnTagTargetChanged(string value) => RecomputeCanStates();

    partial void OnIsWorkingChanged(bool value) => RecomputeCanStates();

    private void RecomputeCanStates()
    {
        var idle = !_globalBusy && !IsWorking;
        CanPull = idle && !string.IsNullOrWhiteSpace(PullReference);
        CanAct = idle && SelectedItem is not null;
        CanPush = idle && SelectedItem is not null;
        CanTag = idle && SelectedItem is not null && !string.IsNullOrWhiteSpace(TagTarget);
    }

    /// <summary>由壳同步全局忙碌（主刷新 / 其它页长操作期间不允许并发操作）。</summary>
    public void SetGlobalBusy(bool busy)
    {
        _globalBusy = busy;
        RecomputeCanStates();
    }

    // ---- 操作基础设施 ----

    private CancellationToken BeginOp()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        IsWorking = true;
        BusyChanged?.Invoke(this, true);
        return _cts.Token;
    }

    private void EndOp()
    {
        IsWorking = false;
        BusyChanged?.Invoke(this, false);
    }

    private static string AppendLine(string text, string line) =>
        string.IsNullOrEmpty(text) ? line : text + "\n" + line;

    // ---- 命令：进 ----

    [RelayCommand]
    private async Task PullAsync()
    {
        var reference = PullReference.Trim();
        if (string.IsNullOrWhiteSpace(reference)) return;

        var ct = BeginOp();
        StatusRequested?.Invoke(this, $"拉取 {reference} …");
        try
        {
            var progress = new Progress<(string Status, long Current, long Total)>(
                p => StatusRequested?.Invoke(this, $"拉取 {p.Status} ({p.Current}/{p.Total})"));
            await _client.PullImageAsync(reference, progress, ct);
            await _res.RefreshImagesAsync();
            Refresh();
            Succeeded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ErrorRaised?.Invoke(this, ex);
        }
        finally
        {
            EndOp();
        }
    }

    [RelayCommand]
    private async Task DeleteImageAsync()
    {
        if (SelectedItem is null) return;
        var reference = SelectedItem.Reference;
        if (_dialogs is not null &&
            !await _dialogs.ConfirmAsync("删除镜像", $"确定删除镜像「{reference}」吗？此操作不可撤销。"))
            return;

        var ct = BeginOp();
        StatusRequested?.Invoke(this, $"删除镜像 {reference} …");
        try
        {
            await _client.DeleteImageAsync(reference, ct);
            SelectedItem = null;
            await _res.RefreshImagesAsync();
            Refresh();
        }
        catch (Exception ex)
        {
            ErrorRaised?.Invoke(this, ex);
        }
        finally
        {
            EndOp();
        }
    }

    // ---- 命令：出 ----

    [RelayCommand]
    private async Task RegistryLoginAsync()
    {
        if (_dialogs is not null && !await _dialogs.ConfirmAsync(
                "登录镜像仓库",
                $"将以用户「{(string.IsNullOrWhiteSpace(RegistryUser) ? "<空>" : RegistryUser)}」" +
                $"登录{(string.IsNullOrWhiteSpace(RegistryServer) ? "默认服务器" : RegistryServer)}。密码只经 stdin 传入，不保存。"))
            return;

        // 回调为 null 视为「UI 层没准备好」，与「用户取消」同等处理。
        var prompt = PromptPassword;
        var password = prompt is null ? null : await prompt();
        if (string.IsNullOrEmpty(password))
        {
            NotifyRequested?.Invoke(this,
                ("未输入密码，已取消登录。", Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational));
            return;
        }

        var ct = BeginOp();
        try
        {
            await _client.RegistryLoginAsync(
                string.IsNullOrWhiteSpace(RegistryServer) ? null : RegistryServer.Trim(),
                string.IsNullOrWhiteSpace(RegistryUser) ? null : RegistryUser.Trim(),
                password, ct);
            password = null; // 尽早脱离本方法的局部变量
            RegistryStatusText = $"已登录 {RegistryServer}";
            StatusRequested?.Invoke(this, "仓库登录成功");
            Succeeded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ErrorRaised?.Invoke(this, ex);
        }
        finally
        {
            EndOp();
        }
    }

    [RelayCommand]
    private async Task RegistryLogoutAsync()
    {
        var ct = BeginOp();
        try
        {
            await _client.RegistryLogoutAsync(
                string.IsNullOrWhiteSpace(RegistryServer) ? null : RegistryServer.Trim(), ct);
            RegistryStatusText = "已注销";
            StatusRequested?.Invoke(this, "仓库已注销");
            Succeeded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ErrorRaised?.Invoke(this, ex);
        }
        finally
        {
            EndOp();
        }
    }

    [RelayCommand]
    private async Task PushImageAsync()
    {
        if (SelectedItem is null) return;
        var reference = SelectedItem.Reference;
        if (string.IsNullOrWhiteSpace(reference))
        {
            NotifyRequested?.Invoke(this,
                ("该镜像没有可用的引用（reference 为空），无法推送。",
                 Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning));
            return;
        }

        if (_dialogs is not null && !await _dialogs.ConfirmAsync(
                "推送镜像", $"将 {reference} 推送到镜像仓库。未登录的仓库会失败。"))
            return;

        var ct = BeginOp();
        StatusRequested?.Invoke(this, $"正在推送 {reference}…");
        PushOutput = "";
        HasPushOutput = true;
        try
        {
            var progress = new Progress<string>(line =>
                PushOutput = AppendLine(PushOutput, line));
            await _client.PushImageAsync(reference, allTags: false, quiet: false, progress, ct);
            StatusRequested?.Invoke(this, $"已推送 {reference}");
            Succeeded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            PushOutput = AppendLine(PushOutput, $"失败：{ex.Message}");
            ErrorRaised?.Invoke(this, ex);
        }
        finally
        {
            EndOp();
        }
    }

    [RelayCommand]
    private async Task TagImageAsync()
    {
        if (SelectedItem is null) return;
        var target = TagTarget.Trim();
        if (string.IsNullOrEmpty(target))
        {
            NotifyRequested?.Invoke(this,
                ("请先填写目标标签（如 registry.example.com/app:v1）。",
                 Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational));
            return;
        }

        var ct = BeginOp();
        try
        {
            await _client.TagImageAsync(SelectedItem.Reference, target, ct);
            await _res.RefreshImagesAsync();
            Refresh();
            StatusRequested?.Invoke(this, $"已打标签 {target}");
            Succeeded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ErrorRaised?.Invoke(this, ex);
        }
        finally
        {
            EndOp();
        }
    }
}
