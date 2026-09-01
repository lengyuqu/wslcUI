# 终端渲染技术选型决策（P4-R2）

> 日期：2026-09-01 ｜ 状态：**已决** ｜ 决策：**采用 XTerm.NET 作为终端核心，自研 WinUI 3 渲染层**
> 调研产物：Spike A（自研原型 + 真实 VT 流夹具测试 9/9）、Spike B（XTerm.NET 同夹具验证 4/4）

---

## 一、背景

P4-R1 已交付 SGR 颜色渲染（`VtStripper` + `RichTextBlock`，`011c928`），两个已知限制（背景色、光标定位/全屏程序）需要 cell-buffer 级方案解决。R2 的任务：为 R3（全屏 cell 渲染）在「自研解释器」与「第三方库」之间做选型。

候选（调研发现原计划中的 XtermSharp 已于 2023 年归档，替换为活跃维护的同类库）：

| 候选 | 状态 | 说明 |
|------|------|------|
| **自研**（扩展 VtStripper 为屏幕模型） | — | 已有 41 个单测覆盖的 SGR 状态机基础 |
| **XTerm.NET**（tomlm/XTerm.NET） | 活跃（近期仍有提交） | MIT、headless、无 UI 依赖、nuget `XTerm.NET` 1.1.2（net6+，net10 可用）；repo 2.0 线目标 .NET 10 |
| ~~XtermSharp~~ | **已归档**（xamarin fork 2023-02 停更） | 排除 |
| ~~TermControl（microsoft/terminal）~~ | C++/WinRT 依赖重 | 排除 |

## 二、Spike 方法：同一组真实 VT 流双盲验证

用 WSL `script` 命令（Linux PTY，不受本机 Windows ConPTY 故障影响）采集四个真实程序的输出流作为夹具（`tests/wslcUI.Tests/TestData/*.vt`）：

| 夹具 | 特征 | 考察点 |
|------|------|--------|
| `ls.vt` | SGR 颜色（1;34 目录粗体蓝）+ 多列布局 | 颜色状态机 |
| `grep.vt` | 纯文本（busybox grep 不发色码）+ `-n` 行号 | 基础文本 |
| `vi.vt` | `?1049h` 备用屏 + CUP 定位 + EL + 增量重绘（状态栏两次重写 1/3→3/3） | 全屏程序核心路径 |
| `top.vt` | `ESC[H ESC[J` 整屏重绘 + 反显表头（`ESC[7m`）+ **恰好 80 列满宽行** | 满宽行/延迟换行 |

两路 spike 用**相同断言口径**跑同一组夹具，保证可比性。

## 三、Spike A：自研原型（可行，但坑比预期多）

产物：`tests/wslcUI.Tests/Spike/VtCellSpike.cs`（约 200 行）+ 9 个夹具测试全过。

**实测踩中的坑**（都发生在区区四个夹具上）：

1. **VT100 延迟换行（deferred wrap）**：写到末列后光标必须停在末列，下一个可打印字符到达才真正换行。若写满立即换行，紧跟的 `\r\n` 会让每个满宽行多吃一行——`top.vt` 的行恰好 80 列满宽，原型初版因此整体错位（光标 (0,44) 而非 (0,30)）。**这是自研路线的标志性风险样本**：VT 规范里这类语义细节还有几十处（滚动区 DECSTBM、宽字符、字符集切换…），每个都是静默错位的隐患。
2. **LF 不回列首**：VT 的 `\n` 只下移，程序须显式发 `\r\n`——测试预期与真实语义的两处对齐消耗。
3. 夹具工程本身也有坑（`script` 头/尾注记行超宽换行污染网格、busybox grep `--color` 静默无效、vi 把 stdin EOF 解释成跳末行命令），说明**验证环境本身也需要投入**。

**未完成清单**（到 R3 可用还需）：备用屏真正的保存/恢复（spike 简化为清屏）、滚动区、CJK 双宽字符（本项目中文场景刚需！）、组合字符、真彩色、IL/DL 行插入删除、DECAWM 换行模式……估计再需数百行 + 每项的边界测试。**渲染层（Win2D 网格 + 等宽字体度量，含中文双宽列）另计，且这部分两路线成本相同。**

## 四、Spike B：XTerm.NET（开箱即全对）

产物：`tools/XTermNetSpike/`（约 70 行调用代码）。

结果：**四个夹具全部渲染正确**——包括 vi 的备用屏增量重绘和 top 的满宽行（说明其延迟换行实现正确）。API 表面干净（`Terminal`/`Write`/`GetLine`/`Resize`），与自研原型的调用形态几乎同构，接入成本低。

功能面远超所需：完整 VT/ANSI、双缓冲 + 回滚、Unicode 宽字符（含 CJK）、256 色/真彩色、键盘/鼠标输入序列生成（`GenerateKeyEvent` 等，R3 的输入侧也省了）、事件系统，甚至 Sixel/Kitty 图形。

## 五、对比与结论

| 维度 | 自研 | XTerm.NET |
|------|------|-----------|
| 夹具正确性 | 9/9（修完延迟换行等坑后） | 4/4（零修改） |
| 解释器工作量 | 数百行 + 数十处 VT 语义坑（延迟换行只是样本） | **零** |
| CJK 双宽 | 自行实现列宽表 | 已实现（wcwidth 对齐测试） |
| 输入序列生成 | 自行实现 | 已实现 |
| 渲染层工作量 | 相同（Win2D/网格 + 字体度量） | 相同 |
| 依赖风险 | 无 | 单一 MIT 小库；源码可得可 fork 自保；版本锁定 |
| 已有代码关系 | VtStripper 演进 | VtStripper 保留（R1 MVP 渲染仍用），互不冲突 |

**决策：R3 采用 XTerm.NET 作为终端核心（解析 + 屏幕模型 + 输入生成），自研 WinUI 3 渲染层。**

理由：终端的大成本在 VT 状态机的正确性——spike 已实证这类坑的密度（延迟换行一例即造成整屏错位）——而 XTerm.NET 用同一组真实流零修改验证通过，功能覆盖含 CJK 双宽（本项目中文刚需）。渲染层成本两路线相同，买了解释器不吃亏。风险面（单维护者）用「MIT + 源码 + 锁版本」控制，必要时可 vendoring。

## 六、对 R3 的影响（范围修订）

1. ~~VtInterpreter 自研~~ → 改为 **XTerm.NET 集成**：`PseudoConsole.OutputReceived` 字节流 → `Terminal.Write`；渲染层遍历 `Buffer` 画 cell。
2. 渲染层（不变）：Win2D `CanvasControl` 或等宽网格控件；`PseudoConsole.Resize` 接线。
3. 输入（简化）：`Terminal.GenerateKeyEvent` 替代手拼转义序列，`InputBox` → 真键盘事件转发可后置。
4. R1 的 `VtStripper` + `RichTextBlock` 路径**保留不动**：作为轻量 MVP 与 XTerm.NET 集成完成前的过渡，两条路径不共存于同一窗口。
5. 保留物：Spike A 原型与夹具测试（回归资产 + 备选种子）、`tools/XTermNetSpike`（集成验证种子）、夹具采集/清洗脚本（`capture-vi.sh` / `clean-fixtures.sh`，可再生成）。

## 七、验证证据

- Spike A：`dotnet test --filter VtCellSpike` → **9/9 通过**（2026-09-01）
- Spike B：`dotnet run --project tools/XTermNetSpike` → **4/4 夹具渲染正确**（2026-09-01，XTerm.NET 1.1.2 / net10）
