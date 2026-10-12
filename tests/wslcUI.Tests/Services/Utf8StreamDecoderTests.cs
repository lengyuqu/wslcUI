using System.Linq;
using System.Text;
using wslcUI.Services;
using Xunit;

namespace wslcUI.Tests.Services;

/// <summary>
/// PTY 字节流的跨块 UTF-8 解码（<see cref="Utf8StreamDecoder"/>）。
///
/// <para>
/// 这组用例是 verify V7「ConPTY 中文跨块解码」的离线替身：V7 要真起 PTY，而本机
/// ConPTY 是机器级故障（V5~V7 长期记 NA），"终端里的中文会不会花屏"因此一直
/// 没有可跑的验证。切块边界本身是纯数据问题，在这里钉住就够了。
/// </para>
/// </summary>
public class Utf8StreamDecoderTests
{
    // 混合文本：CJK（3 字节）、ASCII（1 字节）、emoji（4 字节）、制表与换行。
    private const string Sample = "容器 web-1 已启动\t状态:运行中🙂\n完毕";

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>单实例上按 chunkSize 切块依次解码后拼接。</summary>
    private static string DecodeInChunks(byte[] data, int chunkSize)
    {
        var d = new Utf8StreamDecoder();
        var sb = new StringBuilder();
        for (var i = 0; i < data.Length; i += chunkSize)
            sb.Append(d.Decode(data.Skip(i).Take(chunkSize).ToArray()));
        return sb.ToString();
    }

    [Theory]
    [InlineData(1)]      // 最坏情况：3/4 字节序列被反复停在"前缀"状态
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(7)]      // 与任何 UTF-8 序列长度都不对齐
    [InlineData(16)]
    public void ArbitraryChunkSizesRoundTrip(int chunkSize)
    {
        Assert.Equal(Sample, DecodeInChunks(Bytes(Sample), chunkSize));
    }

    [Fact]
    public void IncompletePrefixYieldsEmptyStringRatherThanReplacementChar()
    {
        var d = new Utf8StreamDecoder();
        var zh = Bytes("中");             // E4 B8 AD
        Assert.Equal("", d.Decode(zh.Take(2).ToArray()));    // 只收到前缀 → 不产出字符
        Assert.Equal("中", d.Decode(zh.Skip(2).ToArray()));  // 补齐后一起解出
    }

    [Fact]
    public void StatelessPerChunkDecodingIsTheBugBeingFixed()
    {
        // 反向对照：无状态地逐块 GetString 必然在半截序列处产出 U+FFFD。
        // 这条不测本类，只是把"为什么需要它"固化下来，防止有人改回一行流写法。
        var bytes = Bytes("中文日志");
        const int cut = 5;                // 落在第二个汉字中间
        var naive = Encoding.UTF8.GetString(bytes.Take(cut).ToArray())
                  + Encoding.UTF8.GetString(bytes.Skip(cut).ToArray());
        Assert.Contains((char)0xFFFD, naive);   // 半截序列解成替换字符 —— 无状态解码的失效指纹
    }

    [Fact]
    public void AnsiControlSequenceSplitAcrossChunksSurvives()
    {
        // ANSI 序列被切开也必须原样拼回，否则颜色指令丢失
        Assert.Equal("\u001b[31m红色\u001b[0m", DecodeInChunks(Bytes("\u001b[31m红色\u001b[0m"), 3));
    }

    [Fact]
    public void TwoStreamsDoNotShareState()
    {
        var a = new Utf8StreamDecoder();
        var b = new Utf8StreamDecoder();
        var zh = Bytes("中");

        Assert.Equal("", a.Decode(zh.Take(1).ToArray()));   // a 手上压着 1 个前缀字节
        Assert.Equal("中", b.Decode(zh));                   // b 完全不受影响
        Assert.Equal("中", a.Decode(zh.Skip(1).ToArray())); // a 补齐后才产出
    }

    [Fact]
    public void EmptyAndNullChunksAreHarmless()
    {
        var d = new Utf8StreamDecoder();
        Assert.Equal("", d.Decode(System.Array.Empty<byte>()));
        Assert.Equal("", d.Decode(null!));
        Assert.Equal("ok", d.Decode(Bytes("ok")));          // 之后仍能正常解码
    }
}
