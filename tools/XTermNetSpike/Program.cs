// =============================================================================
// P4-R2 Spike B：XTerm.NET 可行性验证。
//
// 用与 Spike A 完全相同的四个真实 VT 流夹具（WSL script 采集），验证：
//   1. 在仓库工具链（net10）下编译通过；
//   2. 正确渲染 ls / grep / vi / top（与 Spike A 相同的断言口径）；
//   3. 顺带体验 API 表面（Terminal / Buffer / GetLine …）。
//
// 运行（仓库根目录）：
//   dotnet run --project tools/XTermNetSpike -c Debug
// 退出码：0 = 全部夹具渲染正确；1 = 有失败。
// =============================================================================

using XTerm;
using XTerm.Options;

var fixtures = new (string Name, int Cols, int Rows, Func<Terminal, bool> Check)[]
{
    ("ls.vt",   80, 24, t =>
        t.GetLine(0).Contains("bin") && t.GetLine(0).Contains("opt")),
    ("grep.vt", 80, 24, t =>
        string.Join('\n', Lines(t, 24)).Contains("1:root:x:0:0:root:/root:/bin/sh")),
    ("vi.vt",   80, 30, t =>
        t.GetLine(0).Contains("line three") && t.GetLine(29).Contains("3/3 100%")),
    ("top.vt",  80, 45, t =>
        t.GetLine(0).Contains("Mem:") && t.GetLine(1).Contains("CPU:")),
};

var fixtureDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
    "..", "..", "..", "..", "..", "tests", "wslcUI.Tests", "TestData"));

var failed = 0;
foreach (var (name, cols, rows, check) in fixtures)
{
    var path = Path.Combine(fixtureDir, name);
    if (!File.Exists(path)) { Console.WriteLine($"[MISS] {name}: 夹具不存在 {path}"); failed++; continue; }

    try
    {
        var terminal = new Terminal(new TerminalOptions { Cols = cols, Rows = rows, Scrollback = 100 });
        terminal.Write(File.ReadAllText(path));
        var ok = check(terminal);
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}");
        if (!ok)
        {
            failed++;
            Console.WriteLine("  --- 屏幕前 6 行 ---");
            foreach (var line in Lines(terminal, Math.Min(6, rows)))
                Console.WriteLine($"  | {line}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ERR ] {name}: {ex.GetType().Name}: {ex.Message}");
        failed++;
    }
}

Console.WriteLine(failed == 0 ? "\n全部夹具渲染正确" : $"\n{failed} 个夹具失败");
return failed == 0 ? 0 : 1;

static IEnumerable<string> Lines(Terminal t, int count)
{
    for (var i = 0; i < count; i++) yield return t.GetLine(i);
}
