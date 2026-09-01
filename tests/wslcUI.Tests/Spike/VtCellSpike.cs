namespace wslcUI.Tests.Spike;

/// <summary>
/// P4-R2 Spike A：自研 cell-buffer 终端解释器原型。
///
/// 目的：验证「基于 VtStripper 的状态机思路扩展为屏幕模型」的可行性，
/// 并用真实 VT 流（tests/TestData/*.vt，WSL script 命令采集自 vi/top/ls/grep）
/// 度量工作量。这是调研代码，不是产品代码——若 R2 结论选自研路线，它会成为
/// R3 VtInterpreter 的种子；若选第三方库则废弃。
///
/// 支持的最小集（按采集样本的实际需要）：
///   CSI：CUP(H/f)、ED(J)、EL(K)、CUU/CUD/CUF/CUB(A/B/C/D)、SGR(m)、
///        DECSET/DECRST ?1049（备用屏——spike 简化为清屏）；
///   C0：CR、LF、BS、TAB；
///   OSC：丢弃（BEL / ST 结尾）。
/// </summary>
internal sealed class VtCellSpike
{
    public sealed record Cell(char Ch = ' ', byte? Fg = null, byte? Bg = null, bool Bold = false, bool Dim = false);

    private readonly int _cols, _rows;
    private readonly Cell[,] _cells;
    private int _cx, _cy;

    // 当前 SGR 状态（与 VtStripper 同款逻辑）
    private byte? _fg, _bg;
    private bool _bold, _dim;

    private enum State { Text, Esc, Csi, Osc }
    private State _state = State.Text;
    private readonly System.Text.StringBuilder _csi = new();
    private bool _csiPrivate; // '?' 前缀

    // VT100 延迟换行（deferred wrap / last-column flag）：写到末列后光标停在
    // 末列，下一个可打印字符到达才真正换行。若写满即换行，紧跟的 \r\n 会让
    // 每个 80 列满宽行多吃一行（top/htop 的行恰好满宽，spike 实测踩中）。
    private bool _pendingWrap;

    public VtCellSpike(int cols = 80, int rows = 24)
    {
        _cols = cols; _rows = rows;
        _cells = new Cell[rows, cols];
        for (var y = 0; y < rows; y++)
            for (var x = 0; x < cols; x++)
                _cells[y, x] = new Cell();
    }

    public Cell[,] Screen => _cells;
    public (int X, int Y) Cursor => (_cx, _cy);

    public void Feed(string text)
    {
        foreach (var ch in text)
        {
            switch (_state)
            {
                case State.Text:
                    if (ch == '\x1b') _state = State.Esc;
                    else if (ch == '\r') { _cx = 0; _pendingWrap = false; }
                    else if (ch == '\n') { _cy++; ScrollIfNeeded(); _pendingWrap = false; }
                    else if (ch == '\b') { if (_cx > 0) _cx--; _pendingWrap = false; }
                    else if (ch == '\t') { _cx = System.Math.Min(_cols - 1, (_cx / 8 + 1) * 8); _pendingWrap = false; }
                    else Put(ch);
                    break;

                case State.Esc:
                    if (ch == '[') { _state = State.Csi; _csi.Clear(); _csiPrivate = false; }
                    else if (ch == ']') _state = State.Osc;
                    else _state = State.Text; // 两字符转义：吞掉
                    break;

                case State.Csi:
                    if (ch >= 0x30 && ch <= 0x3f) // 数字 ; : ? > < = 等
                    {
                        if (ch == '?') _csiPrivate = true;
                        else _csi.Append(ch);
                    }
                    else if (ch >= 0x20 && ch <= 0x2f) { /* 中间字节，忽略 */ }
                    else // final byte @-~
                    {
                        DispatchCsi(ch);
                        _state = State.Text;
                    }
                    break;

                case State.Osc:
                    if (ch == '\x07') _state = State.Text;
                    else if (ch == '\x1b') _state = State.Esc;
                    break;
            }
        }
    }

    private void Put(char ch)
    {
        if (_pendingWrap) { _cx = 0; _cy++; ScrollIfNeeded(); _pendingWrap = false; }
        _cells[_cy, _cx] = new Cell(ch, _fg, _bg, _bold, _dim);
        if (_cx >= _cols - 1) _pendingWrap = true; // 延迟换行：停在末列
        else _cx++;
    }

    private void ScrollIfNeeded()
    {
        if (_cy < _rows) return;
        // 上滚一行：整体上移，末行清空
        for (var y = 1; y < _rows; y++)
            for (var x = 0; x < _cols; x++)
                _cells[y - 1, x] = _cells[y, x];
        for (var x = 0; x < _cols; x++)
            _cells[_rows - 1, x] = new Cell();
        _cy = _rows - 1;
    }

    private void DispatchCsi(char final)
    {
        var parts = _csi.Length == 0
            ? System.Array.Empty<string>()
            : _csi.ToString().Split(';');
        int P(int i, int def) =>
            parts.Length > i && int.TryParse(parts[i], out var v) && v != 0 ? v : def;

        if (_csiPrivate)
        {
            // ?1049h/l：备用屏进出。spike 简化：清屏 + 光标归零。
            // （真实实现需保存/恢复主屏，见决策文档的 R3 范围。）
            if (final == 'h' || final == 'l') { if (P(0, 1049) == 1049) ClearScreen(); }
            return;
        }

        switch (final)
        {
            case 'H' or 'f': // CUP：1 基行列
                _cy = System.Math.Clamp(P(0, 1) - 1, 0, _rows - 1);
                _cx = System.Math.Clamp(P(1, 1) - 1, 0, _cols - 1);
                _pendingWrap = false;
                break;
            case 'A': _cy = System.Math.Max(0, _cy - P(0, 1)); _pendingWrap = false; break;
            case 'B': _cy = System.Math.Min(_rows - 1, _cy + P(0, 1)); _pendingWrap = false; break;
            case 'C': _cx = System.Math.Min(_cols - 1, _cx + P(0, 1)); _pendingWrap = false; break;
            case 'D': _cx = System.Math.Max(0, _cx - P(0, 1)); _pendingWrap = false; break;
            case 'J': // ED
                if (P(0, 0) == 2) ClearScreen();
                else if (P(0, 0) == 0) EraseRegion(_cy, _cx, _rows - 1, _cols - 1);
                break;
            case 'K': // EL
                if (P(0, 0) == 0) EraseRegion(_cy, _cx, _cy, _cols - 1);
                else if (P(0, 0) == 2) EraseRegion(_cy, 0, _cy, _cols - 1);
                break;
            case 'm': ApplySgr(parts); break;
            // 其余 CSI（滚动区/字符集…）：spike 忽略
        }
    }

    private void EraseRegion(int y0, int x0, int y1, int x1)
    {
        for (var y = y0; y <= y1; y++)
        {
            var xStart = y == y0 ? x0 : 0;
            var xEnd = y == y1 ? x1 : _cols - 1;
            for (var x = xStart; x <= xEnd; x++)
                _cells[y, x] = new Cell();
        }
    }

    private void ClearScreen()
    {
        for (var y = 0; y < _rows; y++)
            for (var x = 0; x < _cols; x++)
                _cells[y, x] = new Cell();
        _cx = _cy = 0;
    }

    private void ApplySgr(string[] parts)
    {
        if (parts.Length == 0) { Reset(); return; }
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out var p)) continue;
            switch (p)
            {
                case 0: Reset(); break;
                case 1: _bold = true; break;
                case 2: _dim = true; break;
                case 22: _bold = false; _dim = false; break;
                case >= 30 and <= 37: _fg = (byte)(p - 30); break;
                case 39: _fg = null; break;
                case >= 40 and <= 47: _bg = (byte)(p - 40); break;
                case 49: _bg = null; break;
                case >= 90 and <= 97: _fg = (byte)(p - 90 + 8); break;
                case >= 100 and <= 107: _bg = (byte)(p - 100 + 8); break;
                case 38 or 48:
                    if (i + 2 < parts.Length && int.TryParse(parts[i + 1], out var mode) && mode == 5)
                    {
                        if (int.TryParse(parts[i + 2], out var idx) && idx is >= 0 and <= 255)
                        {
                            if (p == 38) _fg = (byte)idx; else _bg = (byte)idx;
                        }
                        i += 2;
                    }
                    else if (i + 4 < parts.Length && int.TryParse(parts[i + 1], out var m2) && m2 == 2)
                        i += 4;
                    break;
            }
        }
        void Reset() { _fg = null; _bg = null; _bold = false; _dim = false; }
    }

    // ---------------- 断言辅助（测试用） ----------------

    /// <summary>按行渲染文本（去掉行尾空格），供断言。</summary>
    public string[] RenderLines()
    {
        var lines = new string[_rows];
        for (var y = 0; y < _rows; y++)
        {
            var sb = new System.Text.StringBuilder();
            for (var x = 0; x < _cols; x++) sb.Append(_cells[y, x].Ch);
            lines[y] = sb.ToString().TrimEnd();
        }
        return lines;
    }

    /// <summary>找到包含指定文本的行，返回该行首个字符处的 cell（样式断言用）。</summary>
    public (int Row, Cell Cell)? Find(string text)
    {
        for (var y = 0; y < _rows; y++)
            for (var x = 0; x + text.Length <= _cols; x++)
            {
                var ok = true;
                for (var i = 0; i < text.Length; i++)
                    if (_cells[y, x + i].Ch != text[i]) { ok = false; break; }
                if (ok) return (y, _cells[y, x]);
            }
        return null;
    }
}
