# wslcUI 遗留问题修复计划（修订版）

> 修订日期：2026-09-01（覆盖初版：其 P3 / P1b / P2 已全部完成，见下表）
> 基线：`6b194af` ｜ 环境：WinR9 / Win11 26200.9278 insider / wslc 2.9.9.0
> 质量门（每批次不变）：`dotnet build` 0 错误 0 警告 + `dotnet test` 26 用例全过 + 涉及 CLI/PTY 的批次补跑 verify

---

## 一、已完成项（本轮三批次，仅供追溯）

| 编号 | 内容 | Commit | 验证结果 |
|------|------|--------|----------|
| P3 | wslc 错误码 → 中文建议映射（`TranslateCliError` + `ShowError` 接入） | `56ec421` | 10 个新单测，26/26 全过 |
| P1b | `PseudoConsole.ProbeHealth()` 预检（5 分钟缓存）+ 开窗入口兜底弹诊断 | `3b0f40a` | 故障机实测预检命中 0xC0000142 |
| P2 | verify V10 SDK 取消路径探针（`--sdk-cancel`） | `6b194af` | 真机 PASS：OCE 上浮 + session 存活 + 零残留进程 |

执行中的两个衍生发现（已修复，记录备查）：
- **预检缓存 bug**：`ProbeHealth` 缓存命中路径硬编码返回 `exitCode=0`，一度造成「宿主相关故障」的误判；修正后确认 ConPTY 故障在所有宿主下一致，机器级定论维持。
- **SDK 命名空间陷阱**：`ListImagesAsync` 是 CLI+SDK 合并视图，CLI 侧有 alpine ≠ SDK 会话能跑（V10 首跑踩中）；探针已改为无条件 Pull。

---

## 二、剩余遗留问题

| 编号 | 问题 | 类型 | 规模 | 前置依赖 |
|------|------|------|------|----------|
| H1 | 仓库卫生：提交 docs、清理探针脚本、恢复 CLI 侧 alpine | 事务 | 小 | 无 |
| H2 | 错误码映射表的持续积累 | 代码维护 | 小 | 无（随真机使用推进） |
| P1a | ConPTY 机器级故障复验 | 外部依赖 | — | Windows 更新 |
| P4 | 终端完整 VT 渲染 | 路线图 | 大 | P1a（仅联调部分受限） |

### H1：仓库卫生（建议立即执行）

三个待办：

1. **提交文档**：`docs/FIX-REPORT-2026-08-31.md`（修复与验证总结报告）与本计划文件目前未入库，提交一个 `docs(...)` commit。
2. **清理 `tools/wslcUI.Verify/console-ab-probe.ps1`**：宿主 A/B 对照实验的一次性探针，结论已沉淀进本计划与 `3b0f40a` 的 commit message，可删除。
3. **恢复 CLI 侧 alpine:latest**：V10 首跑失败时收尾 `DeleteImageAsync` 经 CLI 回退路径误删了 CLI 命名空间的 alpine（开发机基础镜像）。恢复：`wslc pull alpine:latest`。已记录在案，影响仅限本机开发环境。

**验收**：`git status` 干净；`wslc images` 可见 alpine。

### H2：错误码映射表的持续积累

**现状**：`WslcCli.ErrorHints` 初版 10 个错误码，全部来自已观测 + 惯例推断。真机使用中必然遇到未映射码（InfoBar 显示原文兜底，不会更差，但体验降级）。

**方案**：低频维护项，遇到即补：

1. 观察：真机使用 / verify 运行中遇到未映射错误码（InfoBar 显示裸 `WSLC_E_...` 即为信号）。
2. 补录：`ErrorHints` 加一行 `{码} → {中文建议}`，同时在 `TranslateCliErrorTests` 补一个命中用例。
3. 可选增强（如未映射码频繁出现）：在 `TranslateCliError` 的 null 返回前记录 `Debug.WriteLine(code)`，便于被动收集。

**验收**：每次补录伴随单测 + 过构建门禁，单 commit。

### P1a：ConPTY 机器级故障复验（外部依赖触发，被动等待）

**现状**：本机 Win11 build 26200.9278 任何 PTY 子进程 `0xC0000142 (STATUS_DLL_INIT_FAILED)` 启动即死，已三重实验（官方模式最小复现 / 纯非托管构造 / 计划任务独立宿主）+ 宿主 A/B 对照确认与 wslcUI 无关。产品侧已有 P1b 预检兜底，期间用户体验不受损。

**方案**：

1. **触发条件**：Windows 更新使 build revision 变化（基线 26200.9278，`[System.Environment]::OSVersion` 快速比对）。
2. **复验命令**（零准备）：
   ```powershell
   dotnet run --project tools/wslcUI.Verify -p:Platform=x64 -c Debug
   ```
   预检行出现 `[预检] ConPTY attach 正常`、V5/V5b/V6/V7 全部参与判定为 PASS 即闭环。
3. **闭环后收尾**：
   - AGENTS.md 第 5 节标注「ConPTY 已于 build xxxxx 真机验证通过」，删除「未联调」警示；
   - 顺带可评估撤销 P1b 预检的 5 分钟缓存 TTL（保留亦可——预检本身是防御性设计，不依赖当前故障存在）。
4. **可选加速**：向 Windows Insider 反馈中心提交复现步骤（`console-ab-probe.ps1` 删除前可先归档为反馈材料）；或尝试 `DISM /Online /Cleanup-Image /RestoreHealth` + 重装 WindowsTerminal insider 包（成功率未知）。

**验收**：verify 输出 **13 PASS / 0 FAIL / 0 NA**，其中 V6（`\r` 回传）与 V7（中文跨块解码）首次真机通过。

### P4：终端完整 VT 渲染（路线图，三阶段递进）

**现状**：`TerminalWindow` 为去转义文本 MVP（regex 剥 CSI/OSC + `TextBlock` + 512K 上限）。无颜色、无光标定位、不支持全屏程序。每阶段独立可用、独立可验收、可停。

**阶段 R1 —— SGR 颜色子集渲染（小，可立即启动）**：

- 方案：将「regex 全剥」改为「CSI 解析器保留 SGR、丢弃其余」——新增轻量 `Services/VtStripper`（解析 `ESC[n;m H` 参数序列，维护 8/16 色前景背景 + bold/dim 状态机，其余 CSI/OSC 丢弃）。
- 渲染：`TextBlock` → `RichTextBlock`，按 run 切分着色；仍不支持光标移动。
- 收益：`ls`/`grep` 彩色输出可用，覆盖 80% 日常观感需求。
- 可测性：**`VtStripper` 纯解析层可完全单测，不依赖 ConPTY，故障机器上即可开发**；只有最终视觉联调需等 P1a。
- 步骤：① `VtStripper` + 单测（输入字节流 → 带 SGR 状态的文本段序列）；② `TerminalWindow.OnOutput` 接入，`RichTextBlock` 渲染；③ verify 加 V11（健康机器上 PTY 跑 `echo \x1b[31m红\x1b[0m`，断言渲染含红色 run；故障机器上该单测部分以纯解析器测试替代）。
- 验收：单测全过 + V11 PASS（P1a 后）或解析器单测全过（P1a 前）。

**阶段 R2 —— 渲染技术选型调研（中，产出文档）**：

- spike 评估两条路线：
  - **XTermSharp 移植**：System.Drawing/GDI 基座，WinUI 3 需自绘层（Win2D / `CanvasControl`）承接其 cell buffer；评估工作量和维护性。
  - **自研 cell 渲染**：基于 R1 的解析器扩展为完整 cell-buffer（行列模型 + 光标定位 + 备用屏幕缓冲）。
  - （`TermControl` 直接复用基本不可行：C++/WinRT 依赖重，先验证再下结论。）
- 产出：对比文档（性能 / 工作量 / 依赖风险）+ 技术选型结论，决策 R3 走向。
- 无前置依赖，可与 P1a 并行。

**阶段 R3 —— 全屏渲染（大）**：

- cell-buffer 完整渲染，支持 vim/top 类全屏程序；`PseudoConsole.Resize`（P/Invoke 已就位、未接线）随窗口尺寸联动。
- 前置：R2 选型结论 + P1a（联调必需）。
- 验收：PTY 内跑 `vim` / `htop` 画面不碎、resize 正确。

---

## 三、执行顺序建议

1. **H1（立即）**：提交 docs → 删探针脚本 → 恢复 alpine，一个 commit 内完成。
2. **P4-R1（可立即启动）**：`VtStripper` 解析层不依赖 ConPTY，故障机器上可完整开发 + 单测；UI 接入后视觉验收留待 P1a。
3. **H2（随用随补）**：无排期，遇到未映射码即补。
4. **P4-R2（可与 P1a 并行）**：调研型工作，不写死结论。
5. **P1a（被动触发）**：Windows 更新到达后跑一次 verify 闭环；若 Insider 反馈有回应则同步。
6. **P4-R3（最后）**：依赖 R2 结论 + P1a 修复。

提交策略沿用既有偏好：每批次独立 commit，commit message 附验证证据（测试数 / verify 输出）。
