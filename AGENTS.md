# AGENTS.md — wslcUI

> 给接手本仓库的编码 agent（CodeBuddy / Claude / Cursor 等）的速查与防坑指引。
> 详细架构见 `docs/ARCHITECTURE.md`，项目说明见 `README.md`。

## 1. 这是什么

Windows 原生 UI 的 **WSL 容器（wslc）图形管理器**。本质 = 给 `wslc` 套一个 WinUI 3 原生 GUI 外壳。

- 技术栈：**C# + .NET 8 + WinUI 3（Windows App SDK 1.6）+ CommunityToolkit.Mvvm**。
- 后端集成 **API 优先 + CLI 桥接**：镜像列举/拉取/运行走 `Microsoft.WSL.Containers` SDK；**容器列举 / 按名启停 / 删除 / 日志**，以及**网络 / 卷的整套 CRUD**，因 2.9.4 SDK 无对应投影（见第 5 节），桥接 `wslc` CLI（`WslcCli.cs`）。

## 2. 仓库与协作

- 远程：`workbuddy/wslcUI` @ git233
  - HTTPS：`https://git233.phynews.com:18443/workbuddy/wslcUI.git`
  - SSH：`ssh://git@git233.phynews.com:13422/workbuddy/wslcUI.git`
- git233 是 **Gitea** 实例，用官方 `tea` CLI 管理（建仓 / issue / PR / release）。
- **⚠️ Windows 上 `tea` 只能从 PowerShell 调用**（Git Bash 调 `tea.exe` 会启动卡死，连 `--version` 都无输出）；调用前先 `Set-Location C:\`。
  - 登录：`tea logins add --name git233 --url https://git233.phynews.com:18443 --token <TOKEN> --git-credentials --insecure`
    - **只传 `--token`，别带 `--user`**（dev 版 tea 会把 `--user` 当 basic-auth 路径、报 `no password set`）。
    - 自签证书必须带 `--insecure`。
  - 建仓：`tea repos create --name <x> --login git233`（**别带 `--owner`/`--insecure`**，否则报 `not found`）。
- 自签证书：`git` 访问需 `git config http.https://git233.phynews.com:18443/.sslVerify false`（已为仓库配好）。凭据走 `store` helper 读 `~/.git-credentials`（注意文件里 host 是 `%3a` 编码端口，裸 URL push 时 git 按字面匹配；若认证失败请补一条明文 `:18443` 端口的条目）。
- 主分支：`master`。

## 3. 如何构建

- **需要 Windows + Visual Studio 2022（含 "Windows App SDK" / WinUI 3 工作负载）**。本机无 `dotnet` CLI，无法编译验证 —— 务必在装有 WinUI 3 的 Windows 上打开 `wslcUI.sln`，目标平台 **x64**。
- 目标框架 `net8.0-windows10.0.19041.0`，unpackaged（`WindowsPackageType=None`）。开发机需装 Windows App SDK 1.6 runtime（csproj 中 `WindowsAppSDKSelfContained=false`）。
- 首次编译若报 SDK 成员名错误，见第 5 节 TODO 清单，对照 [C# API 参考](https://wsl.dev/api-reference/csharp/) 修正。

## 4. 架构速览

四层（详见 `docs/ARCHITECTURE.md`）：

```
视图 WinUI 3 ──x:Bind──▶ ViewModel (MVVM) ──▶ IWslcClient 适配器 ──▶ wslc SDK / WSL2 后端
```

- **关键设计：ViewModel 只依赖 `IWslcClient` 接口，不感知后端。**
  - `WslcSdkClient` = 真实后端（调 SDK）。
  - `FakeWslcClient` = 没装 WSL 时的内存假数据，用于跑通整个 UI 流程。
  - 切换只改 `App.xaml.cs` 一行 DI 注册：`services.AddSingleton<IWslcClient, WslcSdkClient>();`（或 `FakeWslcClient`）。
- 线程：`Program.Main` 装了 `DispatcherQueueSynchronizationContext`，`[RelayCommand]` 内 `await` 续体回到 UI 线程，可直接更新 `ObservableCollection`。
- `app.manifest` 已声明 `runFullTrust`（unpackaged WinUI 3 必需）。

## 5. 当前进度（接手从这里看）

**已真正实现**（`src/wslcUI/Services/WslcSdkClient.cs`）：

- `IsReadyAsync` → `WslcService.GetMissingComponents()` 前置检查。
- `GetSessionAsync` → 建 `SessionSettings` + `Session.Start()`（信号量单例保护）。
- `PullImageAsync` → 带 `IProgress<(Status,Current,Total)>` 进度回调。
- `RunAndCaptureAsync` → `CreateContainer` + 订阅 `Process` 的 `OutputReceived/ErrorReceived/Exited` 事件流，捕获 stdout/stderr。
- `ListImagesAsync` → `Session.GetImages()`（纯 SDK，映射 `Name`/`Sha256`/`Size`/`CreatedTimestamp`）。
- `Dispose` → 终止 Session。

**容器列举 / 按名启停 —— 已用 `wslc` CLI 桥接实现（`WslcCli.cs`），不是空壳：**

- `ListContainersAsync`：调 `wslc list -a`（JSON 优先，失败回退 docker 风格表格解析）。
  ⚠️ 解析器尚未在真实 wslc 2.9.4 上验证，首次联调务必核对输出格式（`wslc list -a --format json` 是否被支持）。
- `StartAsync(name)` / `StopAsync(name)`：分别调 `wslc start <name>` / `wslc stop <name>`，非零退出抛 `InvalidOperationException`。
- `DeleteContainerAsync(name)`：`wslc rm <name>`，非零退出抛异常。
- `DeleteImageAsync(reference)`：`wslc image rm <reference>`（⚠️ 该子命令未在真实 wslc 上验证，首次联调确认；若不支持请查 `wslc image --help` 修正）。
- `GetLogsAsync(name)`：`wslc logs <name>`，返回 stdout（非 `-f` 跟随）。
- **为什么不用 SDK**：2.9.4 的 C# 投影**没有** `Session.GetContainers()`，也**没有** `Session.GetContainer(name)`（见 [Known Gaps](https://wsl.dev/api-reference/csharp/known-gaps/)）。所以"列出所有容器 / 按名取回引用 / 删除 / 日志"在纯 SDK 下做不到，CLI 桥接是唯一路径。这是对"API 优先"原则的务实例外，已在 `WslcSdkClient.cs` 顶部注释标明。

**网络 / 卷（network / volume）—— 已实现，整组走 `wslc` CLI 桥接（`WslcCli.cs`）：**

- 2.9.4 的 C# 投影**完全没有** network / volume 资源类型，因此这组与容器列举/启停一样属于"SDK 无投影 → CLI 桥接"的路径。
- `ListNetworksAsync`：`wslc network ls`（JSON 优先，失败回退 docker 风格表格：`NETWORK ID | NAME | DRIVER | SCOPE` 取 NAME/DRIVER/SCOPE）。
- `CreateNetworkAsync(name)`：`wslc network create <name>`，空名抛 `ArgumentException`，非零退出抛 `InvalidOperationException`。
- `RemoveNetworkAsync(name)`：`wslc network remove <name>`，非零退出抛异常。
- `ListVolumesAsync`：`wslc volume ls`（JSON 优先，失败回退 docker 风格表格：`DRIVER | VOLUME NAME` 取 DRIVER/NAME）。
- `CreateVolumeAsync(name)`：`wslc volume create <name>`，空名抛 `ArgumentException`，非零退出抛异常。
- `RemoveVolumeAsync(name)`：`wslc volume remove <name>`，非零退出抛异常。
- ⚠️ 所有 network/volume 解析器**尚未在真实 wslc 2.9.4 上验证**；`network ls` / `volume ls` 是否支持 `--format json` 未知，首次联调务必核对输出格式（`wslc network --help` / `wslc volume --help`）。
- UI 已接：MainWindow 用 `Pivot` 四页（容器 / 镜像 / 网络 / 卷），网络页与卷页各含"名称输入框 + 创建 + 删除"面板，绑定 `NewNetworkName`/`NewVolumeName` 与对应命令。

**未实现**（路线图）：

| 功能 | 说明 |
|------|------|
| 资源监控 stats | 预览 SDK 未明确对应端点，可能需 CLI 兜底（单独写适配器实现 `IWslcClient`） |
| 镜像自动构建 | 可用 `<WslcImage>` MSBuild 集成 |
| 交互式终端 exec/attach | `Process` 已给 stdin/stdout 字节流，需配 ConPTY / XTermSharp 控件渲染 |

## 6. 关键约束与坑

- **API 优先，但容器列举/启停 + 网络/卷整套 CRUD 是已知 SDK 缺口，已用 CLI 桥接（不是"封装 CLI 一切"）**：镜像列举/拉取/运行等 SDK 覆盖的操作继续走 SDK；只有容器列举/`start`/`stop`/`rm`/`logs` 以及 `network`/`volume` 全部子命令因 SDK 无投影才走 CLI。不要为其他本可用 SDK 的操作也加 CLI 封装。
- **锁定 SDK 版本 `Microsoft.WSL.Containers` 2.9.4**（与 wslc 2.9.4.0 对齐）。GA（预计 2026 秋）前的破坏性变更需重编译；升级先比对 API 参考。
- **已核对过的关键 SDK 成员名（2.9.4，对照 [C# API 参考](https://wsl.dev/api-reference/csharp/)）**：`GetMissingComponents()` 返回 `IReadOnlyList<Component>`（判空用 `.Count == 0`，**不是** `ComponentFlags.None`）；`ProcessSettings.CommandLine`（**不是** `CmdLine`）；`Session.GetImages()` 返回 `IReadOnlyList<ImageInfo>`（`Name`/`Sha256`(IBuffer)/`Size`(ulong)/`CreatedTimestamp`）；`Container.Delete(DeleteContainerOption.None|Force)`；`Signal.SIGTERM`。`EnableAutoRemove` 官方示例未出现，已移除（改显式 `Delete`）。
- **待在真实 wslc 上验证**：`WslcCli.ParseList` 的表格解析（以及 `wslc list -a` 是否支持 `--format json`）；`wslc start/stop <name>` 的退出码语义。
- MainWindow 当前用 `IWslcClient`，切换 Fake/SDK 时 UI 代码不应改动。

## 7. 建议的下一步

1. ✅ 容器全生命周期骨架已实现：列表（`wslc list -a`）/ 启停 / 删除（`wslc rm`）/ 日志（`wslc logs`）走 CLI 桥接；镜像列举/拉取/运行走 SDK；镜像删除走 `wslc image rm`。UI 已含镜像输入框+拉取、删除容器/镜像按钮、日志面板。下一步是在装有真实 wslc 2.9.4 的 Windows 上联调：验证 `WslcCli` 的 `list` 解析、`start/stop/rm/logs` 退出码与 `image rm` 子命令是否存在，必要时收紧解析。
2. 把 `MainViewModel` 的 `Containers` / `Images` 集合接到真实数据，验证 MVVM 绑定链路（双栏列表 + 选中启停/删除 + 日志面板已接好）。
3. ✅ network / volume CRUD 已实现，整组走 `wslc` CLI 桥接（同 `IWslcClient`）。下一步是在真实 wslc 2.9.4 上联调 `network ls`/`volume ls` 的表格解析与 `create`/`remove` 退出码，必要时收紧解析（`WslcCli.ParseNetworkList`/`ParseVolumeList` 已留 TODO）。
4. 评估 stats 监控是否需要 CLI 兜底适配器（实现同一 `IWslcClient`）。
5. 交互式终端（最重）留到最后。
