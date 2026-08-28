using System.Text.Json;
using Microsoft.UI.Xaml;

namespace wslcUI.Services;

/// <summary>
/// 用户偏好持久化。存到 <c>%LOCALAPPDATA%\wslcUI\settings.json</c>。
/// 用文件而不是 <c>ApplicationData.Current.LocalSettings</c>，因为 unpackaged
/// WinAppSDK 模式下后者需要包身份（package identity），未上架的本地构建拿不到。
///
/// Theme 字段存 <see cref="ApplicationTheme"/> 枚举字符串（"Light" / "Dark"），
/// 不用 <see cref="ElementTheme"/>：App 级别的 <c>RequestedTheme</c> 类型是
/// <c>ApplicationTheme</c>，<c>ElementTheme.Default</c> 在 Application 层级没有对应值。
/// </summary>
public static class UserSettings
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "wslcUI");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    /// <summary>读取上次保存的主题。文件不存在或解析失败时返回 null。</summary>
    /// <param name="pathOverride">测试用：覆盖默认 <c>%LOCALAPPDATA%\wslcUI\settings.json</c>。</param>
    public static ApplicationTheme? LoadTheme(string? pathOverride = null)
    {
        var path = pathOverride ?? FilePath;
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            var doc = JsonDocument.Parse(fs);
            if (doc.RootElement.TryGetProperty("theme", out var t) &&
                Enum.TryParse<ApplicationTheme>(t.GetString(), ignoreCase: true, out var theme))
                return theme;
        }
        catch
        {
            // 损坏 / 权限问题 / 编码异常 → 视为无保存值，下次写入会覆盖。
        }
        return null;
    }

    /// <summary>保存主题。写文件失败也不抛异常（IO 失败不应阻塞 UI 流程）。</summary>
    /// <param name="pathOverride">测试用：覆盖默认 <c>%LOCALAPPDATA%\wslcUI\settings.json</c>。</param>
    public static void SaveTheme(ApplicationTheme theme, string? pathOverride = null)
    {
        var path = pathOverride ?? FilePath;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(new { theme = theme.ToString() }, JsonOpts);
            // 原子写入：先写临时文件再替换，避免崩溃半途留下损坏的 settings.json
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // 静默失败：磁盘满 / 权限问题 / 杀软拦截 → 不影响本次会话运行
        }
    }
}

