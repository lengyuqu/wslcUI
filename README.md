# wslcUI

Windows 原生 UI 的 **WSL 容器 (wslc)** 管理工具。以 `Microsoft.WSL.Containers`
C# SDK 为主驱动 wslc,SDK 无投影的能力(容器列举/启停/删除/日志/inspect、网络与卷
整套 CRUD、stats、镜像构建、交互式终端)统一桥接 `wslc` CLI —— 见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。

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

- `WslcSdkClient` 中**容器列举 / 按名启停 / 删除 / 日志 / inspect**、**网络 / 卷整套 CRUD**、**资源监控 stats**、**镜像构建**、**交互式终端**
  均桥接 `wslc` CLI（`WslcCli.cs` / `Services/ConPty.cs`），因为 wslc 2.9.9 的 C# 投影没有 `Session.GetContainers()` /
  `Session.GetContainer(name)`，且 network/volume 资源类型完全无投影、也没有 stats / 镜像构建 / inspect 端点。其余 SDK 覆盖的操作（镜像拉取 /
  运行）继续走 SDK；**镜像列举是 SDK 与 CLI 两个命名空间的合并去重**——二者互不可见，只取其一都会漏。
- 资源监控（`wslc stats` + `WslcCli.ParseStats`，表格解析）已实现，列表为单次快照、「刷新统计」按钮独立触发。wslc 2.9.9 不接受 docker 的 `--no-stream` 标志——默认就是一次性快照。
- 镜像构建（`wslc build -t <tag> <context>`，`WslcCli.BuildImageAsync`，逐行流式回传日志）已实现，「构建」页含上下文目录选择 + 标签 + 滚动日志。
- 容器挂载关联（`wslc inspect <name> --format json` → `Mounts[]`，`WslcCli.InspectContainerAsync`）已实现：容器列表「卷」列 + 详情面板「挂载」区块 + 卷页的反向被引清单；选中容器时按需异步拉取而非并入主刷新（避免 N+1），并有独立取消源防快速切换选中导致的陈旧回填。
- 交互式终端已实现且**已真机联调**：`wslc exec -it <name> /bin/sh`（容器内起新进程）与 `wslc attach <name>`（附加到运行中容器的现有前台进程），经 Windows Pseudoconsole（`Services/ConPty.cs`）给容器真 TTY；渲染走 **XTerm.NET（VT 解析 + cell 缓冲）+ 自研 WinUI 渲染层**（`Terminal/TerminalView.cs`，256 色 / 真彩色 / bold / inverse / CJK 双宽），选型见 [docs/TERMINAL-RENDER-DECISION.md](docs/TERMINAL-RENDER-DECISION.md)。输入侧为**真键盘转发**（`Terminal/TerminalInputMapper.cs`：KeyDown / 字符 / 粘贴直达 PTY stdin，Ctrl/Alt 组合键自合成），非 InputBox 整行发送。
  ⚠️ 开发机（Win11 26200.9278 insider）存在**机器级 ConPTY 故障**：任何 PTY 子进程 `0xC0000142` 启动即死（三重实验定责系统，与 wslcUI 无关）。开窗前的 `PseudoConsole.ProbeHealth()` 预检会拦下并弹诊断，不会开出一个黑屏死窗口。
- SDK 与 wslc 同为预览，GA 预计 2026 年秋；锁版本 2.9.9 以避免破坏性变更。

## 验证与测试

```bash
# 1) 构建（含 tools 下的诊断/验收工具）
dotnet build wslcUI.sln -c Debug

# 2) 单元测试
dotnet test tests/wslcUI.Tests/wslcUI.Tests.csproj -p:Platform=x64 -c Debug

# 3) 真机验证（直调仓库内真实代码路径；V5~V7 在 ConPTY 系统故障下记 NA）
dotnet run --project tools/wslcUI.Verify -p:Platform=x64 -c Debug
```

环境前置自检：`powershell -ExecutionPolicy Bypass -File scripts\verify-env.ps1`。
质量门与覆盖范围详见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) 第 7 节。

## 项目结构

```
wslcUI/
├── wslcUI.sln                    # 4 个项目：src/wslcUI、tests、tools/ConPtyProbe、tools/wslcUI.Verify
├── README.md
├── AGENTS.md                     # 给接手 agent 的速查/防坑指引
├── Directory.Build.props         # dotnet CLI 构建 WinUI 3 所需的 AppxMSBuildToolsPath 修正
├── docs/
│   ├── ARCHITECTURE.md           # 分层、SDK 缺口与桥接策略、质量门
│   ├── TERMINAL-RENDER-DECISION.md
│   └── ui-design/                # 设计方案与可交互原型
├── scripts/verify-env.ps1        # 环境前置检查
├── src/wslcUI/
│   ├── wslcUI.csproj
│   ├── App.xaml(.cs)             # DI 容器 + 启动
│   ├── MainWindow.xaml(.cs)      # 主窗口：NavigationView 三栏（侧栏 220 + 内容 + 详情面板 340 + 日志抽屉 + 状态栏；6 page：容器/镜像/网络/卷/统计/构建，x:Bind）
│   ├── TerminalWindow.xaml(.cs)  # 交互式终端窗口（exec / attach）
│   ├── Program.cs                # WinUI 3 入口（Windows App SDK 2.4 bootstrap）
│   ├── app.manifest
│   ├── Models/                   # ContainerInfo (+Ports/CreatedAt/stats/StatusKind/Mounts) / ContainerMount / ImageInfo (+Digest/Reference) / NetworkInfo / VolumeInfo / StatInfo
│   ├── Converters/
│   ├── Services/
│   │   ├── IWslcClient.cs        # 后端抽象（适配器接口）
│   │   ├── WslcSdkClient.cs      # 真实后端：SDK 覆盖的操作 + 转发 WslcCli
│   │   ├── WslcCli.cs            # CLI 桥接 + 表格/JSON 解析器 + 错误码中文映射
│   │   ├── ConPty.cs             # Windows Pseudoconsole P/Invoke（真 TTY，零依赖）+ 健康预检
│   │   ├── UserSettings.cs       # 主题持久化（%LOCALAPPDATA%\wslcUI\settings.json）
│   │   ├── VtStripper.cs         # R1 转义剥离（已退位，保留作回退种子）
│   │   └── FakeWslcClient.cs     # 离线开发用
│   ├── Terminal/
│   │   ├── TerminalView.cs       # XTerm.NET cell 渲染层（行级 TextBlock + 同属性 run 合并）
│   │   └── TerminalInputMapper.cs # 键盘 → 终端输入序列（纯函数，可单测）
│   └── ViewModels/
│       ├── MainViewModel.cs      # MVVM + RelayCommand
│       └── VolumeReverseMapping.cs # 卷 → 容器反转映射（纯函数，可单测）
├── tests/wslcUI.Tests/           # xUnit 单测；TestData/*.vt 为真实 VT 流夹具
└── tools/
    ├── wslcUI.Verify/            # 真机验证程序 + VT 夹具采集/清洗脚本
    ├── ConPtyProbe/              # ConPTY 诊断探针
    └── XTermNetSpike/            # P4-R2 选型 spike（已完成，未纳入 sln）
```

> `bin/`、`obj/`、`.vs/`、`.workbuddy/`（本地 AI 状态）与构建产物均已在 `.gitignore` 中排除。
