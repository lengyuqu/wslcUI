# wslcUI 遗留问题修复计划（修订版 2）

> 修订日期：2026-09-01 ｜ 覆盖修订版 1（其 H1 / P4-R1 已完成；P4-R2 已完成，见下）
> 基线：`eea9b29` ｜ 环境：WinR9 / Win11 26200.9278 insider / wslc 2.9.9.0
> 质量门（每批次不变）：`dotnet build` 0 错误 0 警告 + `dotnet test` 50 用例全过 + 涉及 CLI/PTY 的批次补跑 verify

---

## 一、已完成项（追溯）

| 编号 | 内容 | Commit | 验证结果 |
|------|------|--------|----------|
| P3 | wslc 错误码 → 中文建议映射（`TranslateCliError` + `ShowError` 接入） | `56ec421` | 10 个新单测 |
| P1b | `PseudoConsole.ProbeHealth()` 预检（5 分钟缓存）+ 开窗兜底弹诊断 | `3b0f40a` | 故障机实测命中 0xC0000142 |
| P2 | verify V10 SDK 取消路径探针（`--sdk-cancel`） | `6b194af` | 真机 PASS：OCE 上浮 + session 存活 + 零残留 |
| H1 | 提交 docs、删一次性探针、恢复被 V10 误删的 CLI 侧 alpine | `0fcd873` | `git status` 干净、alpine 回列表 |
| P4-R1 | `VtStripper` SGR 解析器 + `RichTextBlock` 着色渲染（256 色调色板） | `011c928` | 15 个新单测，41/41 全过；verify 9/9 PASS |
| P4-R2 | 渲染选型调研（双盲 spike：自研原型 vs XTerm.NET，同一组真实 VT 流夹具） | `eea9b29` | 决策：**XTerm.NET 核心 + 自研 WinUI 渲染层**（详见 `docs/TERMINAL-RENDER-DECISION.md`）；spike 9/9 + 4/4 |
| P4-R3 | 终端全屏 cell 渲染：XTerm.NET 集成 + `TerminalView` 渲染层 + Resize 三级联动 | `004fb5b` | 7 个新集成测试（夹具驱动/颜色编码/CJK 双宽/跨块等价/Resize），57/57 全过；视觉验收待 P1a |

R3 交付说明：输入侧仍为 InputBox 整行发送（`\r`），`Terminal.GenerateKeyInput` 真键盘转发与 attach 已运行进程留作后续增强（AGENTS.md 路线图）。

R1 交付的两个已知限制在 R3 的处置情况：
- ~~光标定位 / 全屏程序~~ ✅ R3 已解决（XTerm.NET cell buffer + CUP/ED/EL/备用屏）。
- **背景色**：R3 v1（行级 TextBlock + run）仍无法渲染单 run 背景（WinUI `Run` 无 `Background` 属性）；inverse 通过前景/背景交换部分补偿。根治待渲染层升级 Win2D `CanvasControl` 自绘（接口已预留，见 `TerminalView` 头注释）。

---

## 二、剩余遗留问题（2 项 + 1 项被动）

| 编号 | 问题 | 类型 | 规模 | 前置依赖 |
|------|------|------|------|----------|
| P1a | ConPTY 机器级故障复验 | 外部依赖 | — | Windows 更新（被动） |
| P4-R3 | 终端全屏 cell 渲染（XTerm.NET 集成 + WinUI 渲染层） | 路线图 | 大 | P1a（联调部分受限，集成与渲染层可先行开发） |
| H2 | 错误码映射表持续积累 | 维护 | 小 | 随真机使用触发 |

### P1a：ConPTY 机器级故障复验（被动等待，随时可插）

**现状**：本机 Win11 26200.9278 任何 PTY 子进程 `0xC0000142` 启动即死（三重实验 + 宿主 A/B 对照定责系统）。产品侧 P1b 预检兜底已就位。**R1 渲染已就绪，P1a 修复后终端将首次以彩色形态真机可用**（V5/V6/V7 也会自动恢复判定）。

**方案**：
1. 触发条件：Windows 更新使 build revision 变化（基线 26200.9278）。
2. 复验命令：
   ```powershell
   dotnet run --project tools/wslcUI.Verify -p:Platform=x64 -c Debug
   ```
   预检行 `[预检] ConPTY attach 正常` + V5/V5b/V6/V7 全 PASS 即闭环（验收：**13 PASS / 0 FAIL / 0 NA**）。
3. 闭环收尾：AGENTS.md 第 5 节标注「ConPTY 已于 build xxxxx 真机验证通过」并删除「未联调」警示；顺带真机验收 R1 的彩色渲染（`ls --color` / `grep`）。
4. 可选加速：Insider 反馈中心提交复现（材料已在 `3b0f40a` commit message + 修复报告）；或尝试 `DISM /Online /Cleanup-Image /RestoreHealth`。

### P4-R3：终端全屏 cell 渲染（依 R2 决策：XTerm.NET 集成）

**范围**（按 `docs/TERMINAL-RENDER-DECISION.md` 结论展开）：
1. **集成**：`TerminalWindow` 增加 XTerm.NET 路径——`PseudoConsole.OutputReceived` 字节流 → `Terminal.Write`；输入侧用 `Terminal.GenerateKeyEvent` 替代手拼转义序列。
2. **渲染层**：WinUI 3 cell 渲染（Win2D `CanvasControl` 或等宽网格），遍历 `Terminal.Buffer` 画 cell（含 CJK 双宽列）；`PseudoConsole.Resize` 接线（窗口 resize → PTY resize）。
3. R1 的两个已知限制在此解决：背景色（cell 自带底色）、dim（cell 透明度）。
4. R1 的 `VtStripper` + `RichTextBlock` 保留为轻量过渡路径，两路径不共存于同一窗口。

**可先行开发**（不等 P1a）：集成层与渲染层可用 spike 夹具（`tests/TestData/*.vt`）+ `tools/XTermNetSpike` 做离线驱动验证；仅最终真机联调需 ConPTY 恢复。

**验收**：PTY 内跑 `vim` / `htop` 画面不碎、resize 正确、`ls --color` 背景色正常。

### H2：错误码映射表持续积累（随用随补）

**流程不变**：遇到未映射码（InfoBar 显示裸 `WSLC_E_...` 即信号）→ `ErrorHints` 补一行 + `TranslateCliErrorTests` 补一命中用例 → 单 commit 过质量门。
可选增强（若未映射码频繁出现）：`TranslateCliError` 返回 null 前加 `Debug.WriteLine(code)` 被动收集。

---

## 三、执行顺序建议

1. **P4-R3（下一步）**：XTerm.NET 集成 + WinUI 渲染层可离线先行开发（spike 夹具驱动），真机联调等 P1a。
2. **P1a（被动触发）**：Windows 更新到达即插队复验——它是 R3 联调和 R1 视觉验收的共同前置。
3. **H2（穿插）**：无排期。

提交策略沿用：每批次独立 commit，commit message 附验证证据（测试数 / verify 输出）。
