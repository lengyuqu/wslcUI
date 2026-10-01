# wslcUI 架构

## 1. 为什么 API 优先,而不是封装 CLI

`wslc` 同时提供了 **CLI (`wslc.exe`)** 与 **编程 SDK
(`Microsoft.WSL.Containers`)**。WSL Containers 已于 2026-09-29 **GA（3.0.1）**，
本仓库 SDK 锁 **3.0.1** 与 runtime 对齐。选择 SDK 直接调用,可**省掉整层「进程封装 + stdout 解析 + 重试 + 流式读取」**,
且接口变更在编译期即可发现,而非运行时崩在字符串解析上。

> ⚠️ **例外（容器列举 / 按名启停 / 删除 / 日志 / inspect，网络 / 卷整套 CRUD，资源监控 stats，镜像构建，交互式终端）**：3.0.1 的 C# 投影**仍然没有**
> `Session.GetContainers()`，也**没有** `Session.GetContainer(name)`（见
> [Known Gaps](https://wsl.dev/api-reference/csharp/known-gaps/)；且 **network / volume 资源类型完全无投影，也没有 stats 端点、没有 Dockerfile 构建投影、没有 inspect 投影**）。因此"列出所有容器 / 按名取回引用 / 删除 / 取日志 / 读挂载"、
> 以及"网络/卷的创建、列举、删除"，还有"资源使用快照 / 镜像构建 / 交互式终端"在纯 SDK 下都做不到。这部分在 `WslcCli.cs` 里桥接
> `wslc list -a` / `wslc start` / `wslc stop` / `wslc rm` / `wslc image rm` / `wslc logs` / `wslc inspect <name> --format json`
> 以及 `wslc network create|ls|remove` / `wslc volume create|ls|remove` / `wslc stats`
> 以及 `wslc build -t <tag> <context>`（镜像构建）/ `wslc exec -it <name> /bin/sh` 与 `wslc attach <name>`（交互式终端，经 ConPTY 真 TTY）。
> 属于对"API 优先"的有据例外，不是退回到"用 CLI 封装一切"。

SDK 对象模型(Microsoft.WSL.Containers):

| 类型 | 职责 |
|------|------|
| `WslcService` | 静态入口: `GetMissingComponents()` 前置检查、版本、安装 |
| `Session` | WSL 后端主机:镜像拉取(带进度)、导入、创建容器 |
| `Container` | 启停、检查、删除、在其中运行进程 |
| `Process` | 读取 `stdout`/`stderr`、写 `stdin`、发信号、观察退出码(事件) |

典型流: `WslcService.GetMissingComponents()` → `new Session().Start()` →
`PullImageAsync` → `CreateContainer` → `Container.Start()` → 通过
`Process` 的事件流交互。

## 2. 分层

```
┌─────────────────────────────────────────────┐
│ 视图层  WinUI 3 (Fluent)                    │  XAML + x:Bind
│   - MainWindow: NavigationView 三栏          │
│     (侧栏 220 + 内容 + 详情面板 340          │
│     + 日志抽屉 + 状态栏)                     │
│     7 page：容器/镜像/网络/卷/统计/构建/维护   │
├─────────────────────────────────────────────┤
│ ViewModel  MVVM (CommunityToolkit.Mvvm)      │  ObservableObject / RelayCommand
│   - MainViewModel: 状态、命令、集合绑定        │
├─────────────────────────────────────────────┤
│ 集成层  IWslcClient 适配器                    │  与 UI 解耦的契约
│   - WslcSdkClient  → Microsoft.WSL.Containers │  真实后端
│   - FakeWslcClient → 内存假数据              │  离线开发
├─────────────────────────────────────────────┤
│ 后端   wslc.exe + WSL2 (SDK 内部驱动)        │  OCI 容器/镜像/网络/卷
└─────────────────────────────────────────────┘
```

## 3. 适配器模式 (关键设计)

ViewModel **只依赖 `IWslcClient` 接口**,不感知具体后端。好处:

- 真实后端用 `WslcSdkClient`(生产)。
- 没装 WSL 时用 `FakeWslcClient` 跑通整个 XAML/ViewModel 流程。
- SDK 无投影的能力（容器列举 / 启停 / 删除 / 日志 / inspect，network / volume CRUD，
  stats，build，exec / attach）统一由 `WslcCli.cs` 这一个 CLI 适配器补齐同一接口，
  UI 完全无感——后续新增缺口只需在 `WslcCli` 加方法、在 `WslcSdkClient` 转发。

切换只改 `App.xaml.cs` 一行注册:
```csharp
services.AddSingleton<IWslcClient, WslcSdkClient>();   // 或 FakeWslcClient
```

## 4. 线程模型

`Program.Main` 启动时为当前线程安装 `DispatcherQueueSynchronizationContext`,
因此 `[RelayCommand]` 内的 `await` 续体回到 UI 线程,可直接更新
`ObservableCollection` / `[ObservableProperty]`,无需手动 marshal。

## 5. 已知缺口与后续

| 项 | 状态 | 说明 |
|----|------|------|
| 镜像列举 | ✅ SDK + CLI 合并 | `Session.GetImages()`（SDK 自身命名空间）+ `wslc images`（CLI 命名空间），按镜像 ID 去重合并——两者互不可见，只取其一都会漏（详见 `WslcSdkClient.ListImagesAsync`） |
| 容器列举 | ✅ 已用 CLI 桥接 | 3.0.1 SDK 无 `Session.GetContainers()`，改走 `wslc list -a`（`WslcCli.cs`） |
| 启停(按名) | ✅ 已用 CLI 桥接 | SDK 无 `GetContainer(name)`，走 `wslc start/stop <name>` |
| 删除容器 | ✅ 已用 CLI 桥接 | 走 `wslc rm <name>` |
| 删除镜像 | ✅ 命名空间感知 | 2.9.9 起：reference 命中 `Session.GetImages()` 走 `Session.DeleteImage`，否则 `wslc image rm`（修复 SDK 镜像 UI 可见但删不掉的 bug） |
| 容器日志 | ✅ 已用 CLI 桥接 | 走 `wslc logs <name>`（非 `-f` 跟随） |
| 网络列举 | ✅ 已用 CLI 桥接 | 3.0.1 SDK 无 network 投影，走 `wslc network ls`（`WslcCli.ParseNetworkList`） |
| 网络创建/删除 | ✅ 已用 CLI 桥接 | 走 `wslc network create/remove <name>` |
| 卷列举 | ✅ 已用 CLI 桥接 | 3.0.1 SDK 无 volume 投影，走 `wslc volume ls --format json`（`WslcCli.ParseVolumeListJson`）。**此路径刻意不做表格回退**——Mountpoint 列只存在于 JSON 输出里 |
| 卷创建/删除 | ✅ 已用 CLI 桥接 | 走 `wslc volume create/remove <name>` |
| 资源监控 (stats) | ✅ 已用 CLI 桥接 | 走 `wslc stats -a`（`WslcCli.ParseStats`，按表头推导列宽解析；取消即杀进程安全网 + 独立 `RefreshStatsCommand`）。**必须带 `-a`** —— 不带时 wslc 只返回一个容器（2026-10-02 实测修正的存量 bug） |
| 实时资源曲线 (sparkline) | ✅ 轮询自采样 | stats 不流式 → `MainViewModel` 用 `DispatcherQueueTimer` 每 3 秒取 `wslc stats -a --format json`（NDJSON，`ParseStatsJson`），按容器名攒最多 60 点；坐标换算见纯函数 `Services/SparklineGeometry.cs`。CPU / 内存各自独立成图（都按本批最大值缩放，叠加会误导读图） |
| 容器内文件浏览 | ✅ 已用 CLI 桥接 | 列目录 `wslc exec <ctr> ls -la <path>`（`ParseDirectoryListing` 纯函数）；搬迁 `wslc container cp`。**wslc 的 cp 语义与 docker 不同**：上传目标必须是容器内已存在的目录、且不能指定目标文件名 —— 详见 AGENTS.md 第 5 节 |
| 镜像构建 (wslc build -t) | ✅ 已用 CLI 桥接 | 走 `wslc build -t <tag> <context>`，`OutputDataReceived` 流式回传（SDK 无 Dockerfile 构建投影） |
| 容器 inspect（挂载关联） | ✅ 已用 CLI 桥接 | `wslc inspect <name> --format json` 取 `Mounts[]`（表格里完全不可见，必须走 JSON）。容器被选中时**按需**异步拉取而非并入 Refresh，避免 N+1；有独立取消源防快速切换选中导致的陈旧回填 |
| 清理（prune 容器/镜像/网络/卷） | ✅ 已用 CLI 桥接 | `wslc container\|image\|network\|volume prune -f`（**必须带 `-f`**：wslc 自 2.9.12 起默认弹交互确认，不加会让子进程永久等 stdin → UI 卡住）。**输出原样展示不解析**（回收量文案随版本/语言漂移）。只作用于 CLI 命名空间，碰不到 SDK session 资源 |
| 磁盘占用合计 | ⚠️ 只有镜像可用 | wslc **无** `system df`；`volume list` 的 `Size` 恒为 `N/A`；容器大小只在 `list -a --size` 里以 `0B (虚拟 451MB)` 混合形态出现（刻意不采）。故卡片只给「镜像总占用」（`Services/SizeParser.cs`），容器/卷只给数量；有 SIZE 解析失败时合计前加 `≥` 表示下界 |
| 交互式终端 (exec / attach + ConPTY) | ✅ 已实现 | `wslc exec -it <name> /bin/sh`（在容器内起新进程）或 `wslc attach <name>`（附加到运行中容器的现有前台进程），经 `Services/ConPty.cs` 的 Windows Pseudoconsole P/Invoke 给容器真 TTY。渲染是 **XTerm.NET（VT 解析 + cell 缓冲）+ 自研 WinUI 渲染层**（`Terminal/TerminalView.cs`），输入是真键盘转发（`Terminal/TerminalInputMapper.cs`），**不是** R1 的「去 ANSI 转义文本」MVP；选型依据见 `docs/TERMINAL-RENDER-DECISION.md` |

## 6. 版本对齐

SDK 与 wslc runtime **版本已对齐**：WSL Containers 于 2026-09-29 GA（3.0.1），
本仓库同时锁 `Microsoft.WSL.Containers` **3.0.1** 与 `wslc 3.0.1.0`。
GA 后仍可能有破坏性变更,升级时先比对
[wsl.dev/api-reference/csharp](https://wsl.dev/api-reference/csharp/)，并补跑第 7 节全套质量门。

3.0.1 SDK 相对 2.9.9 的 winmd diff 为**纯增量、无删除**：
`IProcessSettings.EnableStandardInput`（bool，对应 "Support STDIN through SDK"）
与 `ErrorCode.ContainerDeleted`；本项目未引用二者，升包源兼容。

## 7. 质量门与验证

每次改动按顺序全过才算完成：

| 命令 | 期望 |
|------|------|
| `dotnet build wslcUI.sln -c Debug` | 0 错误 0 警告（含 tools 下的 ConPtyProbe / wslcUI.Verify） |
| `dotnet build src/wslcUI/wslcUI.csproj -p:Platform=x64 -c Release` | 0 错误 0 警告 |
| `dotnet test tests/wslcUI.Tests/wslcUI.Tests.csproj -p:Platform=x64 -c Debug` | 全部通过 |
| `dotnet run --project tools/wslcUI.Verify -p:Platform=x64 -c Debug` | 9 PASS / 0 FAIL；V5~V7 在本机 ConPTY 系统故障下记 NA（非代码问题） |

- **单元测试**覆盖：CLI 表格与 JSON 解析器（`Services/TableParserTests.cs`，以真机
  `wslc` 输出原文为夹具）、错误码中文映射、用户设置持久化、卷→容器反转映射、
  终端键盘映射、VT 转义剥离、XTerm 集成、ConPTY 健康预检。
- **真机 verify** 覆盖：CLI 输出编码与五个表格解析器（行数零偏差）、失败上浮、
  `ArgumentList` 参数整体性、ConPTY 创建/交互/跨块 UTF-8、SDK 取消清理链。
  它经 `InternalsVisibleTo` 直接调用仓库内真实代码路径，而非逻辑副本。
- **环境前置检查**：`powershell -ExecutionPolicy Bypass -File scripts\verify-env.ps1`。
