# wslcUI

Windows 原生 UI 的 **WSL 容器 (wslc)** 管理工具。以 `Microsoft.WSL.Containers`
C# SDK 为主驱动 wslc,对 SDK 无投影的容器/网络/卷操作桥接 `wslc` CLI —— 见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。

## 技术栈

| 层 | 选型 |
|----|------|
| UI 框架 | WinUI 3 (Windows App SDK 1.6) · Fluent Design |
| 语言 / 运行时 | C# · .NET 8 (net8.0-windows10.0.19041) |
| 模式 | MVVM — CommunityToolkit.Mvvm (源生成器) |
| 依赖注入 | Microsoft.Extensions.DependencyInjection |
| 容器后端 | `Microsoft.WSL.Containers` 2.9.4 (wslc SDK, **preview**) |

## 环境前置

1. **Visual Studio 2022 17.6+**，勾选工作负载 *使用 C# 的桌面开发* 与
   *Windows App SDK* (WinUI 3)。
2. **.NET 8 SDK**。
3. **WSL2 + wslc 2.9.4**：
   ```powershell
   wsl --install --no-distribution
   wsl --update
   & "C:\Program Files\WSL\wslc.exe" --version   # 期望 2.9.4.0
   ```
4. WinUI 3 **unpackaged** 运行时：安装
   [Windows App SDK 1.6 运行时](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads)
   ，或在 `wslcUI.csproj` 中设 `<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>`。

## 运行

用 Visual Studio 打开 `wslcUI.sln`，目标平台选 `x64`，`F5` 启动。

- 默认接入真实 wslc(`WslcSdkClient`)。
- 没有 WSL 也想跑界面：在 `App.xaml.cs` 把
  `AddSingleton<IWslcClient, WslcSdkClient>()` 换成 `FakeWslcClient`，
  即可离线开发 XAML / ViewModel。

## 已知缺口(预览期)

- `WslcSdkClient` 中**容器列举 / 按名启停 / 删除 / 日志**、**网络 / 卷整套 CRUD**、**资源监控 stats**
  均桥接 `wslc` CLI（`WslcCli.cs`），因为 wslc 2.9.4 的 C# 投影没有 `Session.GetContainers()` /
  `Session.GetContainer(name)`，且 network/volume 资源类型完全无投影、也没有 stats 端点。其余 SDK 覆盖的操作（镜像列举 /
  拉取 / 运行）继续走纯 SDK。
- 资源监控（`wslc stats --no-stream` + `WslcCli.ParseStats`）已实现，列表为单次快照、「刷新统计」按钮独立触发。
- 镜像自动构建 (`<WslcImage>` MSBuild) 暂未实现。
- SDK 与 wslc 同为预览，GA 预计 2026 年秋；锁版本 2.9.4 以避免破坏性变更。

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
    ├── MainWindow.xaml(.cs)     # 主窗口 (Pivot: 容器/镜像/网络/卷/统计 + x:Bind)
    ├── Program.cs                # WinUI 3 入口
    ├── app.manifest
    ├── Models/                  # ContainerInfo / ImageInfo / NetworkInfo / VolumeInfo / StatInfo
    ├── Services/
    │   ├── IWslcClient.cs        # 后端抽象 (适配器接口)
    │   ├── WslcSdkClient.cs     # 真实 wslc SDK 实现
    │   ├── WslcCli.cs            # 容器/网络/卷的 CLI 桥接 + 解析器
    │   └── FakeWslcClient.cs     # 离线开发用
    └── ViewModels/
        └── MainViewModel.cs      # MVVM + RelayCommand
```
