using System.Threading.Tasks;

namespace wslcUI.Services;

/// <summary>
/// 轻量确认对话框抽象，让 ViewModel 能在不依赖具体 UI 类型的前提下
/// 请求用户确认（破坏性操作前）。由 <see cref="DialogService"/> 用 WinUI
/// ContentDialog 实现。
/// </summary>
public interface IDialogService
{
    /// <summary>弹出确认框，返回 true 表示用户点了「确定」。</summary>
    Task<bool> ConfirmAsync(string title, string message, string? primaryText = null, string? closeText = null);
}
