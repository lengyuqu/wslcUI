using System;
using System.Text;

namespace wslcUI.Services;

/// <summary>
/// 跨块有状态 UTF-8 解码器。
///
/// <para>
/// PTY / CLI 的字节流是按块到达的（ConPTY 读缓冲 4 KB），一个 CJK 字符的 3 个字节
/// 可能被切断在两块边界上。对单块调 <c>Encoding.UTF8.GetString</c>（<b>无状态</b>）会把
/// 残缺前缀直接替换成 U+FFFD，终端里的中文日志/vim 就花屏。本类包一个
/// <see cref="Decoder"/>，未完成的前缀字节留在解码器里，下一次调用一并补上。
/// </para>
///
/// <para>
/// <see cref="Decoder"/> 没有 <c>GetString</c>（.NET Core 起就没了，只剩
/// <c>GetCharCount</c> / <c>GetChars</c> / <c>Convert</c>），所以这里走 char 缓冲。
/// 尺寸按 <c>data.Length + 4</c> 取上界：<b>不能</b>按字节数取，因为非 BMP 字符（emoji）
/// 4 字节产出<b>一个代理对 = 2 个 char</b>，且上一块压着的残留前缀会在本块补出字符；
/// 也<b>不能</b>先调 <c>GetCharCount</c> 再 <c>GetChars</c> —— 实测（.NET 10 /
/// DecoderNLS）GetCharCount 会把未完成的尾序列当非法字节结算，破坏跨块状态，
/// 正好毁掉本类存在的意义。上界推导：n 个新字节最多产出 n 个 char（1 字节 → 1 char
/// 已是 UTF-8 最高产出率），残留前缀最多 3 字节、补出至多 1 个字符（代理对算 2 个），
/// 故 n + 2 足够，取 n + 4 留余量。
/// </para>
///
/// <para><b>有状态</b>：一个流必须一个实例，绝不能做成 static 共享（两条流会互相
/// 啃掉对方的半截字符）。本类不内部加锁 —— 每个调用点各自在固定线程上使用。</para>
/// </summary>
internal sealed class Utf8StreamDecoder
{
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private char[] _chars = Array.Empty<char>();

    /// <summary>解码一块字节。整块都落在未完成的前缀里时返回空串（不是失败）。</summary>
    internal string Decode(byte[] data)
    {
        if (data is null || data.Length == 0) return "";

        var capacity = data.Length + 4;   // 上界证明见类型注释
        if (_chars.Length < capacity) _chars = new char[capacity];

        // flush 必须显式传 false：所有 Convert 重载都带这个参数，传 true 会把未完成的
        // 尾序列立刻结算成 U+FFFD，跨块保持状态的意义就没了。Convert 缓冲不足时只是
        // 少产出并报告 completed=false，不像 GetChars 那样抛 ArgumentException。
        _decoder.Convert(data, _chars.AsSpan(0, capacity), flush: false,
                         out _, out var charsUsed, out _);
        return charsUsed == 0 ? "" : new string(_chars, 0, charsUsed);
    }
}
