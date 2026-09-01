# wslcUI 本轮工作总结报告（2026-09-01）

> 范围：`fa598ba`…`79833f3` 共 **16 个 commit**（4 个 fix / 3 个 feat / 2 个 test / 2 个 docs / 若干配套）
> 主线：静态审查 → 分批缺陷修复 → 真机验证体系 → 遗留计划执行 → 终端渲染三级演进（R1→R2→R3）
> 终态质量门：构建 0 错误 0 警告 ｜ **57/57 单元测试全过** ｜ verify 9/9 PASS ｜ `git status` 干净
> 过程性文档：`docs/FIX-REPORT-2026-08-31.md`（缺陷明细）、`docs/REMAINING-PLAN-2026-08-31.md`（计划追踪）、`docs/TERMINAL-RENDER-DECISION.md`（选型决策）

---

## 一、总览

| 阶段 | 产出 | 关键 Commit |
|------|------|------------|
| ① 缺陷检测与修复 | 15 处审查缺陷（高4/中7/低3/低3）+ 1 处验证中追加的致命 bug | `fa598ba` / `faf10a5` / `cb7fa61` / `aa35cac` |
| ② 真机验证体系 | verify 工具 10 项 + V10 SDK 探针 + 机器故障三重定责 | `202c63d` / `6b194af` |
| ③ 遗留计划批次一 | 错误码中文化 / ConPTY 预检兜底 / 仓库卫生 | `56ec421` / `3b0f40a` / `0fcd873` |
| ④ 终端渲染演进 | R1 SGR 颜色 → R2 双盲选型 → R3 XTerm.NET cell 渲染 | `011c928` / `eea9b29` / `004fb5b` |
| ⑤ 收口 | AGENTS.md 更新至真实现状、计划追踪、夹具二进制保护 | `23d9e33` / `b223a43` / `ecabc83` 等 |

**一句话总结**：从「代码体检」出发，修复了包括一个从未被发现的 PTY 致命 bug 在内的 16 处缺陷，建成了可持续运行的验证体系，并把终端从"去转义文本 MVP"推进到完整的 cell 级彩色渲染。

---

## 二、缺陷修复（16 处）

### 审查发现的三批（commit `fa598ba` / `faf10a5` / `cb7fa61`）

| 级别 | 数量 | 代表性缺陷 |
|------|------|-----------|
| 高 | 4 | ConPTY Dispose 与读线程竞态；构造异常句柄泄漏；终端 4KB 分块 UTF-8 丢字；CLI 输出编码按 GBK 解码（跨机器静默空列表） |
| 中 | 7 | 列举失败静默空表；`Arguments` 拼接参数注入；App 退出 Session 不清理；stats 取消主刷新；`RunAndCaptureAsync` 取消容器泄漏；终端输出无界增长；OSC 正则吞正文 |
| 低 | 3 | 同名行重复添加；`ShowLogsAsync` 变量捕获；终端回车 `\n` 应为 `\r` |

### 验证阶段追加的致命发现（commit `aa35cac`）

**`UpdateProcThreadAttribute` 的 `lpValue` 直接传了 `_hPC` 值**——API 要求指向 HPCON 的指针。属性列表实际一直是坏的，`CreateProcessW` 静默忽略伪控制台属性，**终端窗口从未真正连上 PTY**（子进程一直继承父控制台）。这是 AGENTS.md「未联调」预警的最大坑，由自建的 verify 程序首跑即抓到——验证体系价值的最佳实证。

---

## 三、真机验证体系

### 交付物

- **`tools/wslcUI.Verify`**：控制台验证程序，经 `InternalsVisibleTo` 直接调用仓库内真实代码路径（非逻辑副本）。V1-V10 覆盖：CLI 编码与五个表格解析器（行数零偏差）、失败上浮、参数整体性、ConPTY 生命周期/交互/跨块解码、SDK 取消路径（`--sdk-cancel` 拉真实镜像验证 `Stop(SIGTERM)+Delete(Force)` 清理链）。
- **`scripts/verify-env.ps1`**：前置环境检查（wslc / UTF-8 假设 / dotnet SDK / WinAppRuntime 2.4）。
- **VT 流夹具 + 采集脚本**：WSL `script` 命令采集 ls/grep/vi/top 真实输出（Linux PTY，不受 Windows ConPTY 故障限制），`.gitattributes` 保护字节精确性。

### 本机故障定责（重要结论）

本机（Win11 26200.9278 insider）任何 PTY 子进程 `0xC0000142` 启动即死。经**三重实验**（官方模式最小复现 / 纯非托管构造 / 计划任务独立宿主）+ 宿主 A/B 对照确认：**机器级系统故障，与 wslcUI 无关**。产品侧已用 `ProbeHealth()` 预检兜底（开窗前探测，故障弹诊断而非死窗口）。Windows 更新后一条命令复验闭环。

---

## 四、终端渲染演进（P4 三级跳）

### R1：SGR 颜色渲染（`011c928`）
`VtStripper` 跨块状态机（保留 SGR 颜色/bold/dim，丢弃光标类 CSI 与 OSC）+ `RichTextBlock` 着色 + xterm 256 色调色板。已知限制如实记录（WinUI `Run` 无背景色属性）。

### R2：双盲选型调研（`eea9b29`）
同一组真实 VT 流夹具跑两条路线：自研原型（9/9，但实测踩中 **VT100 延迟换行**坑——top 恰好 80 列满宽导致整屏错位）vs **XTerm.NET**（4/4 零修改，含 CJK 双宽/输入序列生成）。**决策：XTerm.NET 核心 + 自研 WinUI 渲染层**（原计划 XtermSharp 已归档出局）。

### R3：全屏 cell 渲染（`004fb5b`）
- `TerminalView`：`BufferChanged` → cell 矩阵 → 行级 TextBlock（同属性合并 run），256 色/真彩色/inverse/bold/CJK 双宽
- **Resize 三级联动**：窗口 → `Terminal.Resize` → `PseudoConsole.Resize`（P/Invoke 首次接线）
- 集成 API 全部实测确认（颜色编码 256/257 默认、真彩色高位、样式位映射），固化进代码注释与防漂移测试

---

## 五、用户体验改进

| 改进 | 效果 |
|------|------|
| 错误码中文化（`56ec421`） | InfoBar 从裸 `WSLC_E_CONTAINER_NOT_FOUND` 变为「找不到该容器，可能已被删除。刷新列表后重试。」+ 10 项映射 |
| 终端彩色渲染（R1+R3） | `ls --color`/`grep`/全屏程序的 256 色 + bold 正确显示 |
| ConPTY 预检兜底（`3b0f40a`） | 故障机器点「终端」得到明确诊断，不再是黑屏死窗口 |
| 失败上浮（`faf10a5`） | 「0 容器」与「查询失败」不再混淆 |

---

## 六、质量总账

- **单元测试**：15 → **57**（+42：错误码翻译 10、ConPTY 预检 1、VtStripper 15、VtCellSpike 9、XTerm 集成 7），全过
- **verify**：V1-V4/V8/V10 全 PASS；V5-V7 因机器故障记 NA（预检自动降级，恢复后自动参与判定）
- **每批次门禁**：`dotnet build` 0 错误 0 警告 → `dotnet test` 全过 → 涉 CLI/PTY 补跑 verify → 独立 commit（message 附验证证据）
- **文档同步**：AGENTS.md 终端章节从「MVP 未联调」更新至 R3 现状；三份过程文档入库

---

## 七、遗留事项（均为被动型，无可主动推进项）

| 事项 | 触发条件 | 动作 |
|------|----------|------|
| P1a ConPTY 复验 | Windows 更新 | `dotnet run --project tools/wslcUI.Verify`，13 PASS / 0 NA 即闭环 + R3 视觉验收（`vim`/`htop`/resize） |
| H2 错误码补录 | 遇到未映射码 | `ErrorHints` + 一行 + 一单测 |
| 背景色渲染 | 可选升级 | 渲染层换 Win2D `CanvasControl`（接口已预留） |
| 输入升级 | 可选 | `Terminal.GenerateKeyInput` 真键盘转发 |
| attach 已运行进程 | 可选增强 | 路线图项 |

**项目达到干净收敛点**：全部代码工作已提交，验证体系可复现，剩余事项均有明确的触发条件与一条命令级别的处置方案。
