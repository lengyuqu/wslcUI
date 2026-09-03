using Windows.System;

namespace wslcUI.Terminal;

/// <summary>
/// 键盘输入 → 终端输入序列 映射（纯静态，可单测，无 UI 依赖）。
///
/// 分工（XTerm.NET 1.1.2 实测探针结论，见 FixtureRunner --api 输入侧探测）：
///  - 特殊键（方向/功能键/Enter/Tab/Backspace/Esc/Home/End/PgUp/PgDn/Ins/Del）
///    经 <c>Terminal.GenerateKeyInput</c> 生成规范序列：
///    \r、\x7f、ESC[A、ESC[1;5C（Ctrl+Right）、ESC[Z（Shift+Tab）、ESCOP、ESC[3~…
///    GenerateKeyInput 只走返回值、不触发 DataReceived——无双发风险；
///  - <b>Ctrl+字母</b>：Key 枚举没有 A-Z，按 C0 控制码自行合成（Ctrl+C=\x03 …）；
///  - <b>Alt+任意</b>：实测 MetaSendsEscape 不影响 GenerateKeyInput（Alt-Enter
///    始终返回裸 \r），ESC 前缀自行合成；
///  - 普通可打印字符返回 null——由 CharacterReceived 路径发送（含 IME/中文），
///    避免 KeyDown 与 CharacterReceived 双发。
/// </summary>
internal static class TerminalInputMapper
{
    private static readonly Dictionary<VirtualKey, XTerm.Input.Key> Special = new()
    {
        [VirtualKey.Enter] = XTerm.Input.Key.Enter,
        [VirtualKey.Tab] = XTerm.Input.Key.Tab,
        [VirtualKey.Back] = XTerm.Input.Key.Backspace,
        [VirtualKey.Escape] = XTerm.Input.Key.Escape,
        [VirtualKey.Up] = XTerm.Input.Key.UpArrow,
        [VirtualKey.Down] = XTerm.Input.Key.DownArrow,
        [VirtualKey.Left] = XTerm.Input.Key.LeftArrow,
        [VirtualKey.Right] = XTerm.Input.Key.RightArrow,
        [VirtualKey.Home] = XTerm.Input.Key.Home,
        [VirtualKey.End] = XTerm.Input.Key.End,
        [VirtualKey.PageUp] = XTerm.Input.Key.PageUp,
        [VirtualKey.PageDown] = XTerm.Input.Key.PageDown,
        [VirtualKey.Insert] = XTerm.Input.Key.Insert,
        [VirtualKey.Delete] = XTerm.Input.Key.Delete,
        [VirtualKey.F1] = XTerm.Input.Key.F1,
        [VirtualKey.F2] = XTerm.Input.Key.F2,
        [VirtualKey.F3] = XTerm.Input.Key.F3,
        [VirtualKey.F4] = XTerm.Input.Key.F4,
        [VirtualKey.F5] = XTerm.Input.Key.F5,
        [VirtualKey.F6] = XTerm.Input.Key.F6,
        [VirtualKey.F7] = XTerm.Input.Key.F7,
        [VirtualKey.F8] = XTerm.Input.Key.F8,
        [VirtualKey.F9] = XTerm.Input.Key.F9,
        [VirtualKey.F10] = XTerm.Input.Key.F10,
        [VirtualKey.F11] = XTerm.Input.Key.F11,
        [VirtualKey.F12] = XTerm.Input.Key.F12,
    };

    /// <summary>
    /// KeyDown 映射。返回要发送的输入序列；null = 不消费（可打印字符交
    /// CharacterReceived 路径）。generateKey 传 <c>Terminal.GenerateKeyInput</c>。
    /// </summary>
    public static string? KeyDown(
        VirtualKey key,
        VirtualKeyModifiers mods,
        Func<XTerm.Input.Key, XTerm.Input.KeyModifiers, string>? generateKey)
    {
        var ctrl = mods.HasFlag(VirtualKeyModifiers.Control);
        var alt = mods.HasFlag(VirtualKeyModifiers.Menu);
        var shift = mods.HasFlag(VirtualKeyModifiers.Shift);

        // Ctrl+字母 → C0 控制码（Ctrl+C=\x03、Ctrl+D=\x04、Ctrl+L=\x0c …）
        if (ctrl && key is >= VirtualKey.A and <= VirtualKey.Z)
            return WithAltPrefix(((char)(key - VirtualKey.A + 1)).ToString(), alt);

        // 特殊键 → GenerateKeyInput（Shift/Ctrl 组合由库编码，Alt 由我们前置 ESC）
        if (Special.TryGetValue(key, out var xkey))
        {
            var xmods = XTerm.Input.KeyModifiers.None;
            if (ctrl) xmods |= XTerm.Input.KeyModifiers.Control;
            if (shift) xmods |= XTerm.Input.KeyModifiers.Shift;
            var seq = generateKey?.Invoke(xkey, xmods);
            if (seq is null) return null;
            return WithAltPrefix(seq, alt);
        }

        // Alt+字母 → ESC + 小写字母（Alt 直接改写字母键时 CharacterReceived
        // 不触发，需在此拦截；小写是 xterm 惯例）
        if (alt && key is >= VirtualKey.A and <= VirtualKey.Z)
            return "\x1b" + (char)('a' + (key - VirtualKey.A));

        return null; // 普通可打印键（字母/数字/空格/标点）：CharacterReceived 路径
    }

    private static string WithAltPrefix(string seq, bool alt) => alt ? "\x1b" + seq : seq;
}
