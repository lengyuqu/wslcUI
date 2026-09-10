# wslcUI 遗留问题修复计划

> 修订日期：2026-09-10 ｜ 覆盖修订版 2（其 P4-R3 已完成，见下表）
> 基线：`eea9b29` ｜ 环境：WinR9 / Win11 26200.9278 insider / wslc 2.9.9.0
> 质量门（每批次不变）：`dotnet build` 0 错误 0 警告 + `dotnet test` 全过（当前 134 用例）+ 涉及 CLI/PTY 的批次补跑 verify

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
| R4 | 真键盘输入转发：`TerminalInputMapper` + `TerminalView` 聚焦/KeyDown/CharacterReceived/粘贴 → PTY stdin | `8c2fecd` | 映射器纯函数单测 + 集成测试；InputBox 收起为错误横幅 |
| R5 | 容器关联数据卷：`inspect --format json` → Mounts → 详情面板/列表卷列/卷页反向被引 | `7388c7b`、`d896ed6` | `InspectContainerJsonTests` 8 例 + 反向映射单测 |
| attach | 附加到运行中容器前台进程（`TerminalWindow.Mode { Exec, Attach }`，`wslc attach <name>`） | `d74280f` | `BuildCommand` 2 例单测（路径/容器名含空格）；视觉验收待 P1a |
| H2 | 官方错误码全集核对入库（15 个 `WSLC_E_*` + 4 个 HRESULT）+ 未映射码留痕 | `fc20fd6` | `TranslateCliErrorTests` 全过 |

R1 交付的两个已知限制在 R3 的处置情况：
- ~~光标定位 / 全屏程序~~ ✅ R3 已解决（XTerm.NET cell buffer + CUP/ED/EL/备用屏）。
- **背景色**：R3 v1（行级 TextBlock + run）仍无法渲染单 run 背景（WinUI `Run` 无 `Background` 属性）；inverse 通过前景/背景交换部分补偿。根治待渲染层升级 Win2D `CanvasControl` 自绘（接口已预留，见 `TerminalView` 头注释）。
- 输入侧限制已由 R4 解决（真键盘转发直达 PTY，不再是 InputBox 整行发送）。

---

## 二、剩余遗留问题（1 项 + 1 项被动）

| 编号 | 问题 | 类型 | 规模 | 前置依赖 |
|------|------|------|------|----------|
| P1a | ConPTY 机器级故障复验 | 外部依赖 | — | Windows 更新（被动） |
| H2 | 错误码映射表持续积累 | 维护 | 小 | 随真机使用触发 |

> P4-R3 已于 `004fb5b` 交付（XTerm.NET 集成 + `TerminalView` 渲染层 + Resize 三级联动），不再是遗留项。

### P1a：ConPTY 机器级故障复验（被动等待，随时可插）

**现状**：本机 Win11 26200.9278 任何 PTY 子进程 `0xC0000142` 启动即死（三重实验 + 宿主 A/B 对照定责系统）。产品侧 P1b 预检兜底已就位。**R3~R4 渲染与输入侧均已就绪，P1a 修复后终端将首次真机可用**（V5/V6/V7 也会自动恢复判定）——UI 逻辑已全部实现，卡住的只有"这台机器能不能起 PTY"。

**方案**：
1. 触发条件：Windows 更新使 build revision 变化（基线 26200.9278）。
2. 复验命令：
   ```powershell
   dotnet run --project tools/wslcUI.Verify -p:Platform=x64 -c Debug
   ```
   预检行 `[预检] ConPTY attach 正常` + V5/V5b/V6/V7 全 PASS 即闭环（验收：**13 PASS / 0 FAIL / 0 NA**）。
3. 闭环收尾：AGENTS.md 第 5 节标注「ConPTY 已于 build xxxxx 真机验证通过」并删除「未联调」警示；顺带真机验收 XTerm.NET 渲染的彩色输出（`ls --color` / `grep`）。
4. 可选加速：Insider 反馈中心提交复现（材料已在 `3b0f40a` commit message + 修复报告）；或尝试 `DISM /Online /Cleanup-Image /RestoreHealth`。

### H2：错误码映射表持续积累（随用随补）

**流程不变**：遇到未映射码（InfoBar 显示裸 `WSLC_E_...` 即信号）→ `ErrorHints` 补一行 + `TranslateCliErrorTests` 补一命中用例 → 单 commit 过质量门。
可选增强（若未映射码频繁出现）：`TranslateCliError` 返回 null 前加 `Debug.WriteLine(code)` 被动收集。

**状态（2026-09-04）**：官方码全集已入库 —— `WslcCli.ErrorHints` 覆盖 wslcsdk.h 全部 15 个 `WSLC_E_*`（0x8004_0601..060F）+ 4 个 WinRT/COM 标准 HRESULT，后续只剩"随真机新码补录"。

---

## 三、执行顺序建议

1. **P1a（被动触发）**：Windows 更新到达即插队复验——它是终端真机可用（含 XTerm.NET 渲染视觉验收）的唯一前置。
2. **H2（穿插）**：无排期，随 `TranslateCliError` 未命中留痕触发。

提交策略沿用：每批次独立 commit，commit message 附验证证据（测试数 / verify 输出）。
