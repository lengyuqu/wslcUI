using Microsoft.UI.Xaml;
using wslcUI.Services;
using Xunit;

namespace wslcUI.Tests.Services;

/// <summary>
/// UserSettings 边界覆盖。用 pathOverride 把 settings.json 写到 tmp 目录，
/// 避免污染 %LOCALAPPDATA% 或被其它 case 串扰。
/// </summary>
public class UserSettingsTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _tmpFile;

    public UserSettingsTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "wslcui-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _tmpFile = Path.Combine(_tmpDir, "settings.json");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tmpDir)) Directory.Delete(_tmpDir, recursive: true); }
        catch { /* tmp 清理失败不致命 */ }
    }

    [Fact]
    public void LoadTheme_file_missing_returns_null()
    {
        var result = UserSettings.LoadTheme(_tmpFile);
        Assert.Null(result);
    }

    [Fact]
    public void LoadTheme_valid_dark_returns_dark()
    {
        File.WriteAllText(_tmpFile, """{"theme":"Dark"}""");
        Assert.Equal(ApplicationTheme.Dark, UserSettings.LoadTheme(_tmpFile));
    }

    [Fact]
    public void LoadTheme_valid_light_returns_light()
    {
        File.WriteAllText(_tmpFile, """{"theme":"Light"}""");
        Assert.Equal(ApplicationTheme.Light, UserSettings.LoadTheme(_tmpFile));
    }

    [Fact]
    public void LoadTheme_case_insensitive()
    {
        // toString 总是规范大小写，但解析应容错
        File.WriteAllText(_tmpFile, """{"theme":"dark"}""");
        Assert.Equal(ApplicationTheme.Dark, UserSettings.LoadTheme(_tmpFile));
    }

    [Fact]
    public void LoadTheme_corrupt_json_returns_null()
    {
        File.WriteAllText(_tmpFile, "{not json");
        // 解析失败应被 catch 吞掉，返回 null 而不是抛
        Assert.Null(UserSettings.LoadTheme(_tmpFile));
    }

    [Fact]
    public void LoadTheme_empty_object_returns_null()
    {
        File.WriteAllText(_tmpFile, "{}");
        Assert.Null(UserSettings.LoadTheme(_tmpFile));
    }

    [Fact]
    public void LoadTheme_invalid_enum_value_returns_null()
    {
        File.WriteAllText(_tmpFile, """{"theme":"Neon"}""");
        Assert.Null(UserSettings.LoadTheme(_tmpFile));
    }

    [Fact]
    public void SaveTheme_writes_valid_json()
    {
        UserSettings.SaveTheme(ApplicationTheme.Dark, _tmpFile);
        var content = File.ReadAllText(_tmpFile);
        Assert.Contains("\"theme\"", content);
        Assert.Contains("\"Dark\"", content);
    }

    [Fact]
    public void SaveTheme_round_trip_dark()
    {
        UserSettings.SaveTheme(ApplicationTheme.Dark, _tmpFile);
        Assert.Equal(ApplicationTheme.Dark, UserSettings.LoadTheme(_tmpFile));
    }

    [Fact]
    public void SaveTheme_round_trip_light()
    {
        UserSettings.SaveTheme(ApplicationTheme.Light, _tmpFile);
        Assert.Equal(ApplicationTheme.Light, UserSettings.LoadTheme(_tmpFile));
    }

    [Fact]
    public void SaveTheme_overwrites_existing_file()
    {
        UserSettings.SaveTheme(ApplicationTheme.Dark, _tmpFile);
        UserSettings.SaveTheme(ApplicationTheme.Light, _tmpFile);
        Assert.Equal(ApplicationTheme.Light, UserSettings.LoadTheme(_tmpFile));
    }

    [Fact]
    public void SaveTheme_creates_directory_if_missing()
    {
        var nested = Path.Combine(_tmpDir, "sub", "dir", "settings.json");
        Assert.False(Directory.Exists(Path.GetDirectoryName(nested)!));
        UserSettings.SaveTheme(ApplicationTheme.Dark, nested);
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void SaveTheme_invalid_path_does_not_throw()
    {
        // 路径含无效字符 → 应被 catch 吞掉，不抛到 UI 流程
        var invalid = Path.Combine(_tmpDir, "?\0/illegal");
        var ex = Record.Exception(() => UserSettings.SaveTheme(ApplicationTheme.Dark, invalid));
        Assert.Null(ex);
    }

    [Fact]
    public void SaveTheme_does_not_leave_tmp_file()
    {
        UserSettings.SaveTheme(ApplicationTheme.Dark, _tmpFile);
        // 原子写入：先 .tmp 后 Move 替换，结束后 .tmp 不应残留
        Assert.False(File.Exists(_tmpFile + ".tmp"),
            "atomic write should consume the .tmp file via File.Move overwrite");
    }

    [Fact]
    public void SaveTheme_indented_json_is_human_readable()
    {
        UserSettings.SaveTheme(ApplicationTheme.Dark, _tmpFile);
        var content = File.ReadAllText(_tmpFile);
        // WriteIndented=true 应让文件含换行 / 缩进，方便用户 / 支持人员手改
        Assert.Contains("\n", content);
    }
}
