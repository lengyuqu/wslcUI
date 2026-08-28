# wslcUI

Windows 原生 UI 的 **WSL 容器 (wslc)** 管理工具。以 `Microsoft.WSL.Containers`
C# SDK 为主驱动 wslc,对 SDK 无投影的容器/网络/卷操作桥接 `wslc` CLI —— 见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。

## 技术栈

| 层 | 选型 |
|----|------|
| UI 框架 | WinUI 3 (Windows App SDK 2.4) · Fluent Design |
| 语言 / 运行时 | C# · .NET 10 (net10.0-windows10.0.26100) |
| 模式 | MVVM — CommunityToolkit.Mvvm (源生成器) |
| 依赖注入 | Microsoft.Extensions.DependencyInjection |
| 容器后端 | `Microsoft.WSL.Containers` 2.9.9 (wslc SDK, **preview**) |

## 环境前置

1. **Visual Studio 2026 (18.x) / 2022 17.6+**，勾选工作负载 *使用 C# 的桌面开发* 与
   *Windows App SDK* (WinUI 3)。
2. **.NET 10 SDK**。
3. **WSL2 + wslc 2.9.9**：
   ```powershell
   wsl --install --no-distribution
   wsl --update
   & "C:\Program Files\WSL\wslc.exe" --version   # 期望 2.9.9.0
   ```
4. WinUI 3 **unpackaged** 运行时：安装
   [Windows App SDK 2.4 运行时](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads)
   ，或在 `wslcUI.csproj` 中设 `<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>`。

## 运行

用 Visual Studio 打开 `wslcUI.sln`，目标平台选 `x64`，`F5` 启动。

- 默认接入真实 wslc(`WslcSdkClient`)。
- 没有 WSL 也想跑界面：在 `App.xaml.cs` 把
  `AddSingleton<IWslcClient, WslcSdkClient>()` 换成 `FakeWslcClient`，
  即可离线开发 XAML / ViewModel。

## 已知缺口(预览期)

- `WslcSdkClient` 中**容器列举 / 按名启停 / 删除 / 日志**、**网络 / 卷整套 CRUD**、**资源监控 stats**、**镜像构建**、**交互式终端**
  均桥接 `wslc` CLI（`WslcCli.cs` / `Services/ConPty.cs`），因为 wslc 2.9.9 的 C# 投影没有 `Session.GetContainers()` /
  `Session.GetContainer(name)`，且 network/volume 资源类型完全无投影、也没有 stats / 镜像构建端点。其余 SDK 覆盖的操作（镜像列举 /
  拉取 / 运行）继续走纯 SDK。
- 资源监控（`wslc stats` + `WslcCli.ParseStats`，表格解析）已实现，列表为单次快照、「刷新统计」按钮独立触发。wslc 2.9.9 不接受 docker 的 `--no-stream` 标志——默认就是一次性快照。
- 镜像构建（`wslc build -t <tag> <context>`，`WslcCli.BuildImageAsync`，逐行流式回传日志）已实现，「构建」页含上下文目录选择 + 标签 + 滚动日志。
- 交互式终端（`wslc exec -it <name> /bin/sh` + Windows Pseudoconsole `Services/ConPty.cs` 真 TTY，`TerminalWindow` 独立窗口渲染）已实现。**ConPTY 属未联调代码**（本会话无 .NET / WSL 无法编译验证），首次真实运行见 `ConPty.cs` 顶部 TODO。
- SDK 与 wslc 同为预览，GA 预计 2026 年秋；锁版本 2.9.9 以避免破坏性变更。

## 项目结构

```
wslcUI/
├── wslcUI.sln
├── README.md
├── AGENTS.md                     # 给接手 agent 的速查/防坑指引
├── docs/ARCHITECTURE.md
└── src/wslcUI/
    ├── wslcUI.csproj
    ├── App.xaml(.cs)            # DI 容器 + 启动
    ├── MainWindow.xaml(.cs)     # 主窗口：NavigationView 三栏（侧栏 220 + 内容 + 详情面板 340 + 日志抽屉 + 状态栏；6 page：容器/镜像/网络/卷/统计/构建，x:Bind）
    ├── TerminalWindow.xaml(.cs)  # 交互式终端 (ConPTY 真 TTY, wslc exec -it)
    ├── Program.cs                # WinUI 3 入口
    ├── app.manifest
    ├── Models/                  # ContainerInfo (+Ports/CreatedAt/stats/StatusKind) / ImageInfo (+Digest/Reference) / NetworkInfo / VolumeInfo / StatInfo
    ├── Services/
    │   ├── IWslcClient.cs        # 后端抽象 (适配器接口)
    │   ├── WslcSdkClient.cs     # 真实 wslc SDK 实现
    │   ├── WslcCli.cs            # 容器/网络/卷/镜像构建 的 CLI 桥接 + 解析器
    │   ├── ConPty.cs            # Windows Pseudoconsole P/Invoke (终端真 TTY, 零依赖)
    │   └── FakeWslcClient.cs     # 离线开发用
    └── ViewModels/
        └── MainViewModel.cs      # MVVM + RelayCommand
```
