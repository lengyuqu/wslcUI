# wslcUI

Windows 原生 UI 的 **WSL 容器 (wslc)** 管理工具。直接基于 `Microsoft.WSL.Containers`
C# SDK 驱动 wslc,不封装 CLI 进程 —— 见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。

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

- `WslcSdkClient` 中的**列举 / 启停**方法带 `TODO`，因为 wslc 预览 SDK 的
  枚举与按名查找接口名需对照
  [C# API 参考](https://wsl.dev/api-reference/csharp/) 确认。
- 资源监控 (`wslc stats`)、network/volume 增删改、镜像自动构建
  (`<WslcImage>` MSBuild) 暂未实现。
- SDK 与 wslc 同为预览，GA 预计 2026 年秋；锁版本 2.9.4 以避免破坏性变更。

## 项目结构

```
wslcUI/
├── wslcUI.sln
├── README.md
├── docs/ARCHITECTURE.md
└── src/wslcUI/
    ├── wslcUI.csproj
    ├── App.xaml(.cs)            # DI 容器 + 启动
    ├── MainWindow.xaml(.cs)     # 主窗口 (XAML + x:Bind)
    ├── Program.cs                # WinUI 3 入口
    ├── app.manifest
    ├── Models/                  # ContainerInfo / ImageInfo
    ├── Services/
    │   ├── IWslcClient.cs        # 后端抽象 (适配器接口)
    │   ├── WslcSdkClient.cs     # 真实 wslc SDK 实现
    │   └── FakeWslcClient.cs     # 离线开发用
    └── ViewModels/
        └── MainViewModel.cs      # MVVM + RelayCommand
```
