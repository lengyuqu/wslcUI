# wslcUI 架构

## 1. 为什么 API 优先,而不是封装 CLI

`wslc` 本身处于预览期,同时提供了 **CLI (`wslc.exe`)** 与 **编程 SDK
(`Microsoft.WSL.Containers`, NuGet 2.9.9)**。两者同为预览、风险等价,因此
选择 SDK 直接调用,可**省掉整层「进程封装 + stdout 解析 + 重试 + 流式读取」**,
且接口变更在编译期即可发现,而非运行时崩在字符串解析上。

> ⚠️ **例外（容器列举 / 按名启停 / 删除 / 日志，网络 / 卷整套 CRUD，以及资源监控 stats）**：wslc 2.9.9 的 C# 投影**没有**
> `Session.GetContainers()`，也**没有** `Session.GetContainer(name)`（见
> [Known Gaps](https://wsl.dev/api-reference/csharp/known-gaps/)；且 **network / volume 资源类型完全无投影，也没有 stats / 资源监控端点**）。因此"列出所有容器 / 按名取回引用 / 删除 / 取日志"、
> 以及"网络/卷的创建、列举、删除"，还有"资源使用快照"在纯 SDK 下都做不到。这部分在 `WslcCli.cs` 里桥接
> `wslc list -a` / `wslc start` / `wslc stop` / `wslc rm` / `wslc image rm` / `wslc logs`
> 以及 `wslc network create|ls|remove` / `wslc volume create|ls|remove` / `wslc stats`
> 以及 `wslc build -t <tag> <context>`（镜像构建）/ `wslc exec -it <name> /bin/sh`（交互式终端，经 ConPTY 真 TTY）。
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
│     6 page：容器/镜像/网络/卷/统计/构建       │
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
- 未来若 `stats` 监控、`build`、`exec` 在 SDK 里缺失,可单独为其写 CLI 适配器
  实现同一接口,UI 无感(network/volume/build/exec 已用此模式桥接)。

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
| 镜像列举 | ✅ 已实现 | `Session.GetImages()`（纯 SDK） |
| 容器列举 | ✅ 已用 CLI 桥接 | 2.9.9 SDK 无 `Session.GetContainers()`，改走 `wslc list -a`（`WslcCli.cs`） |
| 启停(按名) | ✅ 已用 CLI 桥接 | SDK 无 `GetContainer(name)`，走 `wslc start/stop <name>` |
| 删除容器 | ✅ 已用 CLI 桥接 | 走 `wslc rm <name>` |
| 删除镜像 | ✅ 命名空间感知 | 2.9.9 起：reference 命中 `Session.GetImages()` 走 `Session.DeleteImage`，否则 `wslc image rm`（修复 SDK 镜像 UI 可见但删不掉的 bug） |
| 容器日志 | ✅ 已用 CLI 桥接 | 走 `wslc logs <name>`（非 `-f` 跟随） |
| 网络列举 | ✅ 已用 CLI 桥接 | 2.9.9 SDK 无 network 投影，走 `wslc network ls`（`WslcCli.ParseNetworkList`） |
| 网络创建/删除 | ✅ 已用 CLI 桥接 | 走 `wslc network create/remove <name>` |
| 卷列举 | ✅ 已用 CLI 桥接 | 2.9.9 SDK 无 volume 投影，走 `wslc volume ls`（`WslcCli.ParseVolumeList`） |
| 卷创建/删除 | ✅ 已用 CLI 桥接 | 走 `wslc volume create/remove <name>` |
| 资源监控 (stats) | ✅ 已用 CLI 桥接 | 走 `wslc stats`（`WslcCli.ParseStats`，按表头推导列宽解析；取消即杀进程安全网 + 独立 `RefreshStatsCommand`） |
| 镜像构建 (wslc build -t) | ✅ 已用 CLI 桥接 | 走 `wslc build -t <tag> <context>`，`OutputDataReceived` 流式回传（SDK 无 Dockerfile 构建投影） |
| 交互式终端 (exec -it + ConPTY) | ✅ 已实现 | 走 `wslc exec -it <name> /bin/sh`，经 `Services/ConPty.cs` 的 Windows Pseudoconsole P/Invoke 给容器真 TTY；独立 `TerminalWindow` 渲染（去 ANSI 转义） |

## 6. 预览风险

SDK 与 wslc 同为预览,GA 预计 2026 年秋。本仓库已锁定
`Microsoft.WSL.Containers` **2.9.9**。GA 前的破坏性变更需重编译适配;
建议升级时先比对 [wsl.dev/api-reference/csharp](https://wsl.dev/api-reference/csharp/)。
