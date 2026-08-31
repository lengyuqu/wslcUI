# wslcUI 缺陷修复与真机验证总结报告

> 日期：2026-08-31
> 范围：全仓库静态审查 → 分批修复 → 真机端到端验证
> 结果：**15 处缺陷修复**（含验证阶段追加发现的 1 处 ConPTY 致命 bug）、**9 PASS / 0 FAIL / 4 NA**、构建 0 错误 0 警告、15 个单元测试全过

---

## 一、背景与流程

1. **静态审查**：并行审查 4 个层面（CLI 桥接、ConPTY/终端、ViewModel、App/窗口/设置），通读全部核心源码确认缺陷，共识别 14 处。
2. **分批修复**：按高 → 中 → 低三批修复，每批独立 commit，均通过 `dotnet build`（0 错误 0 警告）后才提交。
3. **真机验证**：构建验证程序直接调用仓库内真实代码路径（非逻辑副本），在真机（WinR9 / Win11 26200.9278 / wslc 2.9.9.0）端到端执行，过程中**追加发现并修复 1 处 ConPTY 致命 bug**，并识别出 1 个与代码无关的机器级故障。

---

## 二、缺陷修复清单

### 第一批（高危）— commit `fa598ba`

| # | 缺陷 | 位置 | 修复 |
|---|------|------|------|
| H1 | `Dispose()` 在读线程仍阻塞于 `ReadFile(_hOutRead)` 时关闭该句柄——未定义行为（线程泄漏/复用句柄读脏数据/AV） | `ConPty.cs` | 先关伪控制台与 `_hInWrite` 使管道断开，`_reader?.Join(2000)` 后再经 `ReleaseHandles()` 统一释放 |
| H2 | 构造函数异常路径不清理已创建的管道/伪控制台/属性列表句柄（构造抛异常不触发 Dispose），内核对象泄漏 | `ConPty.cs` | 构造体包 try/catch，异常时 `ReleaseHandles()` 后 rethrow |
| H3 | 终端输出按 4KB 块独立 `UTF8.GetString`，多字节中文横跨块边界产生 U+FFFD 丢字 | `TerminalWindow.xaml.cs` | 改用有状态 `Decoder`（`GetDecoder()`）流式解码 |
| H4 | `RunAsync` 未设输出编码，GUI 进程默认按系统 ANSI 代码页（GBK）解码——跨区域机器中文表头整体乱码 → 解析静默返回空列表 | `WslcCli.cs` | 显式 `StandardOutputEncoding/ErrorEncoding = UTF8` |

### 第二批（中危）— commit `faf10a5`

| # | 缺陷 | 位置 | 修复 |
|---|------|------|------|
| M1 | 列举类命令（list/images/stats/network ls/volume ls）非零退出静默返回空表，用户无法区分「没有资源」与「查询失败」 | `WslcCli.cs` | 非零退出抛 `InvalidOperationException` 带 stderr（`CliFailed`） |
| M2 | `string.Join` 拼接 `Arguments`，含空格/引号的用户输入被拆分或注入额外参数 | `WslcCli.cs` | `RunAsync`/`BuildImageAsync` 改用 `ArgumentList` 逐参数传递 |
| M3 | App 退出不 Dispose DI 容器，SDK Session 不 Terminate，session storage 残留 | `MainWindow.xaml.cs` / `App.xaml.cs` | `MainWindow.Closed` 时 `App.Services.Dispose()`（`Services` 声明类型改为 `ServiceProvider`） |
| M4 | stats 与主操作共享 CTS，「刷新快照」会取消进行中的主刷新并弹误导性错误条 | `MainViewModel.cs` | stats 独立 `_statsCts` 取消链 |
| M5 | `RunAndCaptureAsync` 取消路径不清理已 Start 的容器，后台继续运行 | `WslcSdkClient.cs` | catch `OperationCanceledException` → `Stop(SIGTERM)` + `Delete(Force)` 后 rethrow |
| M6 | 终端输出 `Text +=` 无界增长 + O(n²) 拼接，长会话拖垮 UI | `TerminalWindow.xaml.cs` | 512K 字符上限，超限丢弃头部 |
| M7 | OSC 剥离正则只认 BEL 结尾（现代程序用 ST 漏剥），且贪婪匹配会吞掉中间正文 | `TerminalWindow.xaml.cs` | `\x1b\].*?(\x07\|\x1b\\)` 懒惰匹配，双结尾支持 |

### 第三批（低危）— commit `cb7fa61`

| # | 缺陷 | 位置 | 修复 |
|---|------|------|------|
| L1 | `UpdateContainersInPlace` 的 existing 字典为循环前快照，快照内同名重复行会重复 Add | `MainViewModel.cs` | 新增行同步登记 `existing[f.Name] = f` |
| L2 | `ShowLogsAsync` await 后仍读 `SelectedContainer.Name`，等待期切换选中导致日志标题/内容错位 | `MainViewModel.cs` | await 前捕获局部变量 |
| L3 | 终端回车发 `\n`（依赖终端默认配置），容器名未加引号防拆分 | `TerminalWindow.xaml.cs` | 改发 `\r`；命令行容器名加引号 |

### 验证阶段追加发现（致命）— commit `aa35cac`

| # | 缺陷 | 位置 | 修复 |
|---|------|------|------|
| B1 | **`UpdateProcThreadAttribute` 的 `lpValue` 直接传 `_hPC` 的值**——API 要求指向 HPCON 的指针（官方 ConptyExample 用 `AllocHGlobal` + `WriteIntPtr`）。属性列表实际是坏的，`CreateProcessW` 静默忽略伪控制台属性，**子进程继承父控制台——终端窗口从未真正连上 PTY**。且原代码不检查返回值 | `ConPty.cs` | `_hPcPtr = AllocHGlobal` + `WriteIntPtr` 构造指针传入；检查返回值；`Dispose` 释放 `_hPcPtr` |

> B1 是 AGENTS.md 中「ConPTY 未联调」预警的最大坑。发现路径：验证程序 V6 失败 → cmd banner 直接打进验证程序自身控制台（而非 PTY 管道）→ 定位属性传递失效。

---

## 三、真机验证体系 — commit `202c63d`

### 组成

| 文件 | 用途 |
|------|------|
| `tools/wslcUI.Verify/Program.cs` | 验证程序。经主项目 `InternalsVisibleTo` 直接调用 `WslcCli`/`PseudoConsole`/`TerminalWindow` **真实代码路径**（验证的不是逻辑副本） |
| `scripts/verify-env.ps1` | 前置环境检查：wslc 存在、CLI 输出确为 UTF-8（显式编码修复的前提）、WSL 状态、dotnet SDK ≥10、WinAppRuntime 2.4 |

### 验证项

| 项 | 内容 | 验证目标 |
|----|------|----------|
| V1 | wslc.exe 存在与版本 | 环境 |
| V2.1-V2.5 | list/images/network ls/volume ls/stats：UTF-8 解码命中表头 + 解析条数 == 原始数据行数 | H4（编码）、解析器零丢行 |
| V3 | `rm` 不存在容器 → 异常上浮带 stderr | M1 |
| V4 | 含空格名字 → 报错完整回显 | M2（参数整体性） |
| V5/V5b | ConPTY 构造/Dispose 无异常 | H1/H2 |
| V6 | `echo` + `\r` 回传 | PTY 输入链路、L3 |
| V7 | 14.4KB 中文输出跨 4KB 块解码无 U+FFFD | H3 |
| V8 | 转义剥离正则 4 用例（CSI / OSC+BEL / OSC+ST / 懒惰） | M7 |
| V9 | 指定容器的列表存在性 + logs 拉取（可选 `--container`） | 端到端 |

### 真机结果（WinR9，2026-08-31）

```
9 PASS / 0 FAIL / 4 NA
```

- V1-V4、V8 全部通过：wslc 2.9.9.0 输出确认为 UTF-8；五个解析器条数与原始行数**零偏差**；失败上浮与参数整体性符合预期。
- V5-V7（ConPTY）记 **NA**：机器级 ConPTY attach 故障（见下节）。

### 机器级故障记录（与 wslcUI 无关）

本机（Win11 26200.9278 insider）**任何** PTY 子进程（cmd/whoami/pwsh）在**任何宿主**下均以 `0xC0000142 STATUS_DLL_INIT_FAILED` 启动即死。已通过三重交叉实验确认与代码无关：

1. 官方 ConptyExample 模式最小复现 —— 失败；
2. 纯非托管内存构造 `STARTUPINFOEXW`（排除 C# marshal 嫌疑）—— 失败；
3. 独立控制台宿主 + 计划任务（脱离终端 Job/会话）—— 仍失败。

期间已排除：Windows Terminal 委托（切换 conhost 后依旧）、宿主 Job 限制（计划任务下依旧）。判断为该 insider 构建的系统缺陷，待 Windows 更新修复。

**验证程序内置 `PtyMinRepro` 预检**：每次运行先起独立最小 PTY 复现探测机器健康度，故障时 V5-V7 自动降级 NA 并给出诊断说明；机器修复后无需改代码即自动恢复完整判定。

---

## 四、提交记录

| Commit | 内容 |
|--------|------|
| `fa598ba` | fix(high): ConPTY 生命周期与 CLI 输出编码四处高危缺陷 |
| `faf10a5` | fix(medium): CLI 桥接健壮性与资源清理七处中危缺陷 |
| `cb7fa61` | fix(low): 合并/日志/终端输入三处低危缺陷 |
| `aa35cac` | fix(conpty): UpdateProcThreadAttribute lpValue 传值错误导致 PTY 属性从未生效 |
| `202c63d` | test(verify): 真机验证套件 - 端到端验证三批缺陷修复 |

质量门：每个 commit 前均通过 `dotnet build`（0 错误 0 警告）；单元测试 15/15 通过。

---

## 五、遗留事项

1. **V6/V7 真机复验**：待本机 Windows 更新修复 ConPTY 系统故障后重跑 `dotnet run --project tools/wslcUI.Verify -p:Platform=x64`，确认 B1 修复后的完整 PTY 链路（输入 `\r` 回传 + 中文跨块解码）。
2. **`Stop(Signal.SIGTERM, TimeSpan)` 重载签名**：仅取消 `RunAndCaptureAsync` 时触发，构建通过但未真机走到该路径，首次触发时留意。
3. **行为变化知会**：列举类命令失败现在会弹 InfoBar 错误（此前静默空表）。若真机发现某些正常场景返回非零，需单独收紧。
4. **终端完整 VT 渲染**（XTermSharp / TermControl）仍为路线图项；当前 MVP 维持去转义文本展示。
