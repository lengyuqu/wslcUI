using System.Collections.Generic;
using Windows.System;
using wslcUI.Terminal;
using Xunit;

namespace wslcUI.Tests.Terminal;

/// <summary>
/// R4 真键盘输入映射的离线验证（纯函数，无 UI 依赖）。
/// 序列期望值以 XTerm.NET 1.1.2 实测探针结论为准
/// （tools/XTermNetSpike --api 输入侧探测：\r、\x7f、ESC[A、ESC[1;5C、ESC[Z…）。
/// </summary>
public class TerminalInputMapperTests
{
    /// <summary>假 GenerateKeyInput：记录调用并返回确定序列，隔离 XTerm 库。</summary>
    private static string Gen(XTerm.Input.Key key, XTerm.Input.KeyModifiers mods) =>
        $"<{key}|{mods}>";

    private static string? Map(VirtualKey key, VirtualKeyModifiers mods = 0) =>
        TerminalInputMapper.KeyDown(key, mods, Gen);

    // ---- 特殊键 → GenerateKeyInput 委托 ----

    [Fact]
    public void Enter_PassesThroughGenerator()
    {
        Assert.Equal("<Enter|None>", Map(VirtualKey.Enter));
    }

    [Fact]
    public void Backspace_PassesThroughGenerator()
    {
        Assert.Equal("<Backspace|None>", Map(VirtualKey.Back));
    }

    [Fact]
    public void ArrowUp_CarriesNoModifier()
    {
        Assert.Equal("<UpArrow|None>", Map(VirtualKey.Up));
    }

    [Fact]
    public void CtrlRightArrow_EncodesControlModifier()
    {
        Assert.Equal("<RightArrow|Control>", Map(VirtualKey.Right, VirtualKeyModifiers.Control));
    }

    [Fact]
    public void ShiftTab_EncodesShiftModifier()
    {
        Assert.Equal("<Tab|Shift>", Map(VirtualKey.Tab, VirtualKeyModifiers.Shift));
    }

    [Fact]
    public void FunctionKey_MapsF1ToF12()
    {
        Assert.Equal("<F1|None>", Map(VirtualKey.F1));
        Assert.Equal("<F12|None>", Map(VirtualKey.F12));
    }

    [Fact]
    public void PageUpDeleteHomeEnd_Map()
    {
        Assert.Equal("<PageUp|None>", Map(VirtualKey.PageUp));
        Assert.Equal("<PageDown|None>", Map(VirtualKey.PageDown));
        Assert.Equal("<Delete|None>", Map(VirtualKey.Delete));
        Assert.Equal("<Insert|None>", Map(VirtualKey.Insert));
        Assert.Equal("<Home|None>", Map(VirtualKey.Home));
        Assert.Equal("<End|None>", Map(VirtualKey.End));
    }

    // ---- Alt：ESC 前缀自行合成（MetaSendsEscape 实测无效）----

    [Fact]
    public void AltSpecialKey_PrefixesEsc()
    {
        Assert.Equal("\x1b<Enter|None>", Map(VirtualKey.Enter, VirtualKeyModifiers.Menu));
    }

    [Fact]
    public void AltLetter_IsEscPlusLowercase()
    {
        Assert.Equal("\x1bx", Map(VirtualKey.X, VirtualKeyModifiers.Menu));
    }

    // ---- Ctrl+字母：C0 控制码自合成 ----

    [Fact]
    public void CtrlC_IsControlChar0x03()
    {
        Assert.Equal("\x03", Map(VirtualKey.C, VirtualKeyModifiers.Control));
    }

    [Fact]
    public void CtrlA_IsControlChar0x01()
    {
        Assert.Equal("\x01", Map(VirtualKey.A, VirtualKeyModifiers.Control));
    }

    [Fact]
    public void CtrlL_IsControlChar0x0C()
    {
        Assert.Equal("\x0c", Map(VirtualKey.L, VirtualKeyModifiers.Control));
    }

    [Fact]
    public void CtrlShiftLetter_StillControlChar()
    {
        Assert.Equal("\x03", Map(VirtualKey.C, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift));
    }

    // ---- 可打印键：不消费，交给 CharacterReceived ----

    [Fact]
    public void PlainLetter_ReturnsNull()
    {
        Assert.Null(Map(VirtualKey.A));
        Assert.Null(Map(VirtualKey.Z, VirtualKeyModifiers.Shift));
    }

    [Fact]
    public void Space_ReturnsNull()
    {
        Assert.Null(Map(VirtualKey.Space));
    }

    [Fact]
    public void Digit_ReturnsNull()
    {
        Assert.Null(Map(VirtualKey.Number1));
    }

    // ---- 防御：generateKey 为 null 时特殊键不崩 ----

    [Fact]
    public void NullGenerator_SpecialKeyReturnsNull()
    {
        Assert.Null(TerminalInputMapper.KeyDown(VirtualKey.Enter, 0, null));
    }

    // ---- 真库集成：映射器 + XTerm.NET 生成的序列符合探针实测 ----

    [Fact]
    public void RealTerminal_EnterGeneratesCarriageReturn()
    {
        var t = new XTerm.Terminal();
        Assert.Equal("\r", TerminalInputMapper.KeyDown(VirtualKey.Enter, 0, t.GenerateKeyInput));
    }

    [Fact]
    public void RealTerminal_UpGeneratesCsiA()
    {
        var t = new XTerm.Terminal();
        Assert.Equal("\x1b[A", TerminalInputMapper.KeyDown(VirtualKey.Up, 0, t.GenerateKeyInput));
    }

    [Fact]
    public void RealTerminal_CtrlRightGeneratesModifierSequence()
    {
        var t = new XTerm.Terminal();
        Assert.Equal("\x1b[1;5C",
            TerminalInputMapper.KeyDown(VirtualKey.Right, VirtualKeyModifiers.Control, t.GenerateKeyInput));
    }

    [Fact]
    public void RealTerminal_ShiftTabGeneratesBackTab()
    {
        var t = new XTerm.Terminal();
        Assert.Equal("\x1b[Z",
            TerminalInputMapper.KeyDown(VirtualKey.Tab, VirtualKeyModifiers.Shift, t.GenerateKeyInput));
    }
}
