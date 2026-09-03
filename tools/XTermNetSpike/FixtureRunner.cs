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

using System.Reflection;
using XTerm;
using XTerm.Options;

// R3 API 探针模式：dotnet run -- --api 枚举 BufferCell / Terminal 成员表面，
// 为渲染层取色/字符/双宽做准备。
if (args.Contains("--api"))
{
    var t0 = new XTerm.Terminal();
    t0.Write("\x1b[31m红\x1b[0m plain \x1b[1;44mbold-on-blue\x1b[0m 中文宽");
    var cellType = typeof(XTerm.Terminal).Assembly.GetTypes()
        .FirstOrDefault(x => x.Name.Contains("Cell") && x.IsValueType)
        ?? throw new InvalidOperationException("BufferCell struct not found");
    Console.WriteLine($"cell type: {cellType.FullName}");
    foreach (var f in cellType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        Console.WriteLine($"  field: {f.FieldType.Name} {f.Name}");
    foreach (var p in cellType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        Console.WriteLine($"  prop:  {p.PropertyType.Name} {p.Name}");
    Console.WriteLine("--- Terminal members of interest ---");
    foreach (var m in typeof(XTerm.Terminal).GetMembers(BindingFlags.Public | BindingFlags.Instance))
        if (m.Name.Contains("Buffer") || m.Name.Contains("Line") || m.Name.Contains("Resiz") ||
            m.Name.Contains("Cols") || m.Name.Contains("Rows") || m.Name.Contains("Key") || m.Name.Contains("Feed"))
            Console.WriteLine($"  {m.MemberType} {m.Name}");

    Console.WriteLine("--- AttributeData ---");
    var attrType = typeof(XTerm.Terminal).Assembly.GetTypes()
        .FirstOrDefault(x => x.Name == "AttributeData");
    if (attrType is not null)
    {
        foreach (var f in attrType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            Console.WriteLine($"  field: {f.FieldType.Name} {f.Name}");
        foreach (var p in attrType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            Console.WriteLine($"  prop: {p.PropertyType.Name} {p.Name}");
    }
    Console.WriteLine("--- BufferCell Attributes 值示例 ---");
    var line = t0.GetVisibleLines();
    Console.WriteLine($"  GetVisibleLines -> {line?.Length} lines");
    Console.WriteLine("--- GetVisibleLines signature ---");
    Console.WriteLine("  " + typeof(XTerm.Terminal).GetMethod("GetVisibleLines")?.ToString());
    var m2 = typeof(XTerm.Terminal).GetMethods().Where(x => x.Name == "GenerateKeyInput").ToList();
    m2.ForEach(x => Console.WriteLine("  GI: " + x));
    Console.WriteLine("--- Buffer type surface ---");
    var bufType = t0.Buffer.GetType();
    foreach (var p in bufType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        Console.WriteLine($"  prop: {p.PropertyType.Name} {p.Name}");
    foreach (var m in bufType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        Console.WriteLine($"  method: {m}");
    // 行对象表面（Buffer.Lines 元素类型）
    var linesProp = bufType.GetProperty("Lines");
    if (linesProp is not null)
    {
        var lineType = linesProp.PropertyType.GetElementType() ?? linesProp.PropertyType.GetGenericArguments().FirstOrDefault();
        Console.WriteLine($"  Lines element: {lineType}");
        if (lineType is not null && lineType != typeof(string))
            foreach (var p in lineType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                Console.WriteLine($"    line prop: {p.PropertyType.Name} {p.Name}");
    }
    Console.WriteLine("--- 双宽字符（CJK）cell 布局探测 ---");
    {
        var tw = new XTerm.Terminal();
        tw.Write("中A文");
        for (var x = 0; x < 5; x++)
        {
            var cc = tw.Buffer.Lines[0][x];
            Console.WriteLine($"  [{x}] CodePoint=U+{(int)cc.CodePoint:X4} Content='{cc.Content}' (len={(cc.Content?.ToString() ?? "null").Length}) ext=0x{cc.Attributes.Extended:X}");
        }
        Console.WriteLine($"  GetLine(0) = \"{tw.GetLine(0)}\"");
    }
    Console.WriteLine("--- Wcwidth 包 API 探测 ---");
    {
        var wc = typeof(XTerm.Terminal).Assembly.GetReferencedAssemblies()
            .FirstOrDefault(a => a.Name == "Wcwidth");
        Console.WriteLine($"  referenced: {wc}");
        var wcAsm = System.Reflection.Assembly.Load(wc!);
        foreach (var t in wcAsm.GetExportedTypes())
        {
            Console.WriteLine($"  type: {t.FullName}");
            foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly).Take(10))
                Console.WriteLine($"    {m}");
        }
    }
    Console.WriteLine("--- AttributeFlags 位验证（精确：每属性独立 Terminal，取首个 cell） ---");
    foreach (var (label, sgr) in new[] { ("bold", "\x1b[1m"), ("dim", "\x1b[2m"),
             ("underline", "\x1b[4m"), ("inverse", "\x1b[7m"), ("italic", "\x1b[3m"), ("blink", "\x1b[5m") })
    {
        var tt = new XTerm.Terminal();
        tt.Write(sgr + "X");
        var cc = tt.Buffer.Lines[0][0];
        Console.WriteLine($"  {label,-10}: ch={cc.Content} ext=0x{cc.Attributes.Extended:X} fg={cc.Attributes.Fg} bg={cc.Attributes.Bg}");
    }
    Console.WriteLine("--- 颜色编码验证（默认/16 色/256 色/真彩色） ---");
    foreach (var (label, sgr) in new[] { ("default", "\x1b[39m"), ("red31", "\x1b[31m"),
             ("blue44bg", "\x1b[44m"), ("256c", "\x1b[38;5;208m"), ("truecolor", "\x1b[38;2;10;20;30m") })
    {
        var tt = new XTerm.Terminal();
        tt.Write(sgr + "X");
        var cc = tt.Buffer.Lines[0][0];
        Console.WriteLine($"  {label,-10}: fg={cc.Attributes.Fg} bg={cc.Attributes.Bg} ext=0x{cc.Attributes.Extended:X}");
    }
    Console.WriteLine("--- BufferChangedEventArgs 命名空间与事件签名 ---");
    var evt = typeof(XTerm.Terminal).GetEvent("BufferChanged");
    Console.WriteLine($"  event handler type: {evt?.EventHandlerType?.FullName}");
    var argType = evt?.EventHandlerType?.GetGenericArguments().FirstOrDefault();
    Console.WriteLine($"  args type: {argType?.FullName}");
    return 0;
}

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
