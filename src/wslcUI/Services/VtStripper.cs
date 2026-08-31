using System.Text;

namespace wslcUI.Services;

/// <summary>一个文本段的样式：SGR 派生的前景/背景色 + 粗细。全 false = 默认样式。</summary>
/// <param name="Foreground">前景色（null = 默认）。</param>
/// <param name="Background">背景色（null = 默认）。</param>
/// <param name="Bold">粗体。</param>
/// <param name="Dim">暗淡。</param>
public readonly record struct SpanStyle(byte? Foreground, byte? Background, bool Bold, bool Dim);

/// <summary>渲染文本段：文本 + 起始样式。</summary>
/// <param name="Text">段文本（不含转义序列）。</param>
/// <param name="Style">该段的 SGR 样式快照。</param>
public readonly record struct StyledSpan(string Text, SpanStyle Style);

/// <summary>
/// 轻量 VT 转义序列解析器（P4-R1：SGR 颜色子集）。
///
/// 输入已解码的字符流（TerminalWindow 侧先经 UTF-8 Decoder），输出
/// <see cref="StyledSpan"/> 序列：文本内容 + SGR 颜色/粗细状态。
///
/// 处理范围：
///  - CSI 序列（ESC [ params final）：仅 SGR（m）参数被消费进状态机，
///    其余（光标移动/擦除等）整段丢弃——MVP 不支持光标定位；
///  - OSC 序列（ESC ] ... BEL / ST）：整段丢弃（窗口标题等）；
///  - 其他 ESC（ESC c、ESC 7 等单字符/两字符转义）：丢弃。
///
/// SGR 支持子集（覆盖 ls/grep/常见 CLI 工具的着色输出）：
///  - 0 重置 / 1 粗体 / 2 暗淡 / 22 取消粗细；
///  - 30-37 / 39 / 90-97 前景（标准 8 色 + 亮色 + 默认）；
///  - 40-47 / 49 / 100-107 背景；
///  - 38;5;n / 48;5;n 256 色调色板（映射到调色板索引，由渲染层决定呈现）。
/// 不支持（静默忽略）：下划线/斜体/反显等修饰、38;2;r;g;b 真彩色（少见）。
///
/// 跨块安全：有状态实例，Feed 分块调用也正确（转义序列跨块边界时暂存）。
/// 纯逻辑无 Win32 依赖，可完全单测。
/// </summary>
public sealed class VtStripper
{
    private enum State { Text, Esc, Csi, Osc }

    private State _state = State.Text;
    private readonly StringBuilder _csiParams = new();
    private readonly StringBuilder _oscBuffer = new();

    // 当前 SGR 状态
    private byte? _fg, _bg;
    private bool _bold, _dim;

    private readonly StringBuilder _pending = new();

    /// <summary>
    /// 喂入一段已解码文本，返回其中完整可渲染的段（可能为空——文本被
    /// 转义序列完全隔开时）。未完结的转义序列 / 尾部残段留在内部状态，
    /// 下一次 Feed 继续拼接。
    /// </summary>
    public IReadOnlyList<StyledSpan> Feed(string text)
    {
        var spans = new List<StyledSpan>();
        foreach (var ch in text)
        {
            switch (_state)
            {
                case State.Text:
                    if (ch == '\x1b') { FlushPending(spans); _state = State.Esc; }
                    else _pending.Append(ch);
                    break;

                case State.Esc:
                    if (ch == '[') { _state = State.Csi; _csiParams.Clear(); }
                    else if (ch == ']') { _state = State.Osc; _oscBuffer.Clear(); }
                    else _state = State.Text; // ESC c / ESC 7 等：转义字符本身也被吞掉
                    break;

                case State.Csi:
                    if (char.IsDigit(ch) || ch == ';' || ch == '?' || ch == ':')
                        _csiParams.Append(ch);
                    else
                    {
                        // final byte：'m' = SGR，其余 CSI（光标/擦除）丢弃。
                        if (ch == 'm') ApplySgr(_csiParams.ToString());
                        _state = State.Text;
                    }
                    break;

                case State.Osc:
                    if (ch == '\x07') _state = State.Text;            // BEL 结尾
                    else if (ch == '\x1b') _state = State.Esc;        // 可能是 ESC \ (ST)
                    else _oscBuffer.Append(ch);
                    break;
            }
        }
        FlushPending(spans);
        return spans;
    }

    private void FlushPending(List<StyledSpan> spans)
    {
        if (_pending.Length == 0) return;
        spans.Add(new StyledSpan(_pending.ToString(), new SpanStyle(_fg, _bg, _bold, _dim)));
        _pending.Clear();
    }

    private void ApplySgr(string paramString)
    {
        // 空 SGR（ESC[m）等价于 ESC[0m。
        if (paramString.Length == 0 || paramString.Contains('?') || paramString.Contains(':'))
        {
            // 私有序列 / 冒号分隔的子参数（如 4:3 曲线）不支持，忽略。
            if (paramString.Length == 0) ResetStyle();
            return;
        }

        var parts = paramString.Split(';');
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out var p)) continue;
            switch (p)
            {
                case 0: ResetStyle(); break;
                case 1: _bold = true; break;
                case 2: _dim = true; break;
                case 22: _bold = false; _dim = false; break;
                case >= 30 and <= 37: _fg = (byte)(p - 30); break;
                case 39: _fg = null; break;
                case >= 40 and <= 47: _bg = (byte)(p - 40); break;
                case 49: _bg = null; break;
                case >= 90 and <= 97: _fg = (byte)(p - 90 + 8); break;   // 亮色 → 调色板 8-15
                case >= 100 and <= 107: _bg = (byte)(p - 100 + 8); break;
                case 38 or 48:
                    // 38;5;n / 48;5;n（256 色）。true-color (38;2;r;g;b) 跳过整个组。
                    if (i + 2 < parts.Length && int.TryParse(parts[i + 1], out var mode) && mode == 5)
                    {
                        if (int.TryParse(parts[i + 2], out var idx) && idx is >= 0 and <= 255)
                        {
                            if (p == 38) _fg = (byte)idx; else _bg = (byte)idx;
                        }
                        i += 2;
                    }
                    else if (i + 4 < parts.Length && int.TryParse(parts[i + 1], out var m2) && m2 == 2)
                        i += 4; // 真彩色：不支持，跳过 38;2;r;g;b
                    break;
                // 其余修饰（3 斜体 / 4 下划线 / 7 反显…）：忽略，继续解析后续参数
            }
        }
    }

    private void ResetStyle()
    {
        _fg = null; _bg = null; _bold = false; _dim = false;
    }
}
