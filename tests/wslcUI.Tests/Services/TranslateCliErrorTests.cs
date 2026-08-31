using wslcUI.Services;
using Xunit;

namespace wslcUI.Tests.Services;

/// <summary>
/// WslcCli.TranslateCliError 纯函数测试：错误码提取、映射命中/未命中、
/// 关键词兜底。报错样例取自真机 verify V3/V4 的实际 stderr。
/// </summary>
public class TranslateCliErrorTests
{
    [Fact]
    public void KnownCode_ContainerNotFound_TranslatesToHint()
    {
        // 真机 V3 实测样例
        var msg = "wslc rm 失败: 找不到容器 '__wslcui_verify_not_exist__'。\n错误代码: WSLC_E_CONTAINER_NOT_FOUND";
        var result = WslcCli.TranslateCliError(msg);
        Assert.NotNull(result);
        Assert.Contains("找不到该容器", result);
        Assert.Contains("WSLC_E_CONTAINER_NOT_FOUND", result);
    }

    [Fact]
    public void KnownCode_E_INVALIDARG_TranslatesToHint()
    {
        // 真机 V4 实测样例
        var msg = "wslc logs 失败: 无效名称: 'no such name'\n错误代码: E_INVALIDARG";
        var result = WslcCli.TranslateCliError(msg);
        Assert.NotNull(result);
        Assert.Contains("参数无效", result);
    }

    [Fact]
    public void KnownCode_TrailingPeriod_StrippedBeforeLookup()
    {
        var msg = "错误代码: E_INVALIDARG。";
        var result = WslcCli.TranslateCliError(msg);
        Assert.NotNull(result);
        Assert.Contains("参数无效", result);
    }

    [Fact]
    public void UnknownCode_ReturnsNull()
    {
        var msg = "wslc 失败: 某种错误\n错误代码: WSLC_E_SOMETHING_NEW";
        Assert.Null(WslcCli.TranslateCliError(msg));
    }

    [Fact]
    public void NoCode_PlainMessage_ReturnsNull()
    {
        Assert.Null(WslcCli.TranslateCliError("wslc 失败: 未知错误"));
    }

    [Fact]
    public void NullOrEmpty_ReturnsNull()
    {
        Assert.Null(WslcCli.TranslateCliError(""));
        Assert.Null(WslcCli.TranslateCliError(""));
    }

    [Fact]
    public void LongMessage_IsTruncatedInBrief()
    {
        var longStderr = new string('x', 300);
        var msg = $"wslc rm 失败: {longStderr}\n错误代码: WSLC_E_CONTAINER_NOT_FOUND";
        var result = WslcCli.TranslateCliError(msg);
        Assert.NotNull(result);
        // 摘要截断到 80 字 + 省略号，整条翻译仍是单行可读长度
        Assert.True(result!.Length < 200);
        Assert.EndsWith("）", result);
    }

    [Fact]
    public void WslInstallHint_NoCode_MatchesByKeyword()
    {
        var msg = "wslc list 失败: WSL is not installed. Run 'wsl --install' to continue.";
        var result = WslcCli.TranslateCliError(msg);
        Assert.NotNull(result);
        Assert.Contains("wsl --install", result);
    }

    [Fact]
    public void EnglishErrorCodeLabel_AlsoRecognized()
    {
        var msg = "wslc stop 失败: container not found\nerror code: WSLC_E_CONTAINER_NOT_FOUND";
        var result = WslcCli.TranslateCliError(msg);
        Assert.NotNull(result);
        Assert.Contains("找不到该容器", result);
    }

    [Fact]
    public void KnownCode_ResourceInUseHints_Translate()
    {
        var net = WslcCli.TranslateCliError("错误代码: WSLC_E_NETWORK_IN_USE");
        Assert.NotNull(net);
        Assert.Contains("网络", net);

        var vol = WslcCli.TranslateCliError("错误代码: WSLC_E_VOLUME_IN_USE");
        Assert.NotNull(vol);
        Assert.Contains("卷", vol);

        var img = WslcCli.TranslateCliError("错误代码: WSLC_E_IMAGE_IN_USE");
        Assert.NotNull(img);
        Assert.Contains("镜像", img);
    }
}
