# AGENTS.md — wslcUI

> 给接手本仓库的编码 agent（CodeBuddy / Claude / Cursor 等）的速查与防坑指引。
> 详细架构见 `docs/ARCHITECTURE.md`，项目说明见 `README.md`。

## 1. 这是什么

Windows 原生 UI 的 **WSL 容器（wslc）图形管理器**。本质 = 给 `wslc` 套一个 WinUI 3 原生 GUI 外壳。

- 技术栈：**C# + .NET 8 + WinUI 3（Windows App SDK 1.6）+ CommunityToolkit.Mvvm**。
- 后端集成 **API 优先 + CLI 桥接**：镜像列举/拉取/运行走 `Microsoft.WSL.Containers` SDK；**容器列举 / 按名启停 / 删除 / 日志**，以及**网络 / 卷的整套 CRUD**、**镜像构建**（`wslc build -t`）、**交互式终端**（`wslc exec -it`，经 ConPTY 真 TTY），因 2.9.9 SDK 无对应投影（见第 5 节），桥接 `wslc` CLI（`WslcCli.cs` / `Services/ConPty.cs`）。

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

- **需要 Windows + Visual Studio 2022+（含 "Windows App SDK" / WinUI 3 工作负载）**，目标平台 **x64**。本机（WinR9）装的是 **VS2026 Build Tools**（`C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\`，8月8日装，含 `MSBuild\Microsoft\VisualStudio\v18.0\AppxPackage\Microsoft.Build.Packaging.Pri.Tasks.dll`）。
- **dotnet CLI 也能完整编译 WinUI 3**（无需开 VS IDE）：默认 `dotnet build` 会因 PRI 任务缺失失败（`AppxMSBuildToolsPath` 默认指向 `$(MSBuildExtensionsPath)\Microsoft\VisualStudio\v$(VisualStudioVersion)\AppxPackage\`，dotnet SDK 里没有）。**仓库根目录已常驻 `Directory.Build.props`** 处理这件事：
  ```xml
  <Project>
    <PropertyGroup>
      <!-- 仅当外部未提供时才填，因此在 VS 内正常构建也不受影响 -->
      <AppxMSBuildToolsPath Condition="'$(AppxMSBuildToolsPath)' == ''">C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Microsoft\VisualStudio\v18.0\AppxPackage\</AppxMSBuildToolsPath>
      <VisualStudioVersion Condition="'$(VisualStudioVersion)' == ''">18.0</VisualStudioVersion>
    </PropertyGroup>
  </Project>
  ```
  直接 `dotnet build src/wslcUI/wslcUI.csproj -p:Platform=x64 -c Debug`（已验证 2026-08-28，Debug/Release 均 0 错误 0 警告）。换机器只需改这一个路径。
  ⚠️ 注意：bash/PowerShell 命令文本里出现 `MSBuild` 字样会被 WorkBuddy 命令校验拦截（LOLBin 规则），执行构建用上面这种干净命令或直接在 VS 里跑。
- 目标框架 `net10.0-windows10.0.26100.0`（`WindowsSdkPackageVersion=10.0.26100.87`），unpackaged（`WindowsPackageType=None`）。开发机需装 Windows App SDK **2.4** runtime（csproj 中 `WindowsAppSDKSelfContained=false`）。
  - ⚠️ **TFM 与 `WindowsSdkPackageVersion` 是绑定的**：SDK.Ref `10.0.26100.8x` 起带的 `WinRT.Runtime 2.3.x` 依赖 `System.Runtime 9.0.0.0`，配 `net8.0` 会直接 **CS1705**，并连锁触发 XamlCompiler 的 `WMC1509 / WMC0909 / WMC1111 / WMC9999`（报"Cannot resolve DataType"是假象，根因是 CS1705 没产出 dll）。**要留在 net8.0 就必须把 `WindowsSdkPackageVersion` 降回兼容版本**；当前选择升 TFM 到 net10.0。
  - `TargetPlatformMinVersion` 仍为 `10.0.19041.0`（wslc SDK 2.9.9 的投影要求编译期平台版本 ≥ 26100，但运行期下限可低）。
- **CommunityToolkit.Mvvm 8.4.x 新增诊断 `MVVMTK0045`**：WinRT/WinUI 场景下 `[ObservableProperty]` 必须写在 **partial 属性**上（而非字段），否则 39 条警告。本仓库 `MainViewModel` 已全部改成 `public partial T Name { get; set; }` 形式（C# 13 partial properties，net10 默认开启）。新增属性请沿用该写法。
- **WinAppSDK 2.4 `Pivot` 行为变化**：未显式 `SelectedIndex` 时不再默认选中第一项，启动会落在最后一项。**所有 `Pivot` 都必须显式写 `SelectedIndex="0"`**（即便你"以为"它在 1.6 行为下默认是 0）。本仓库 `MainWindow.xaml` 已显式声明。
- **WinAppSDK 2.x 启动期 P0（unpackaged）**：① `Program.cs` 的 `Bootstrap.Initialize` 必须把 `majorMinor` 与 `minVersion` 同步升到目标版本（如 2.4 → `0x00020004` / `0x0002000400000000UL`），否则即便装上 2.4 runtime 也可能被 back-compat shim 拉到 1.6 跑；② `app.manifest` 必须显式声明 `<compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1"><application><maxversiontested Id="10.0.26100.0"/></application></compatibility>`，否则 OS 会启用旧兼容模式并触发"未为此应用启用必要功能"报错。两项**必须一起改**。
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

### UI 布局（三栏）

`MainWindow` 用 `NavigationView` 取代了旧版 `Pivot`：

```
┌────────────────────────────────────────────────────────────────────┐
│ 顶栏 40px：wslcUI · WSL 容器管理                  主题  详情         │ ← 全局按钮（ToggleTheme / ToggleDetail）
├────────────────────────────────────────────────────────────────────┤
│ InfoBar (Auto, 错误/提示)                                           │
│ ProgressBar  (Auto, indeterminate, 非阻塞)                          │
├──────────┬─────────────────────────────────────┬───────────────────┤
│ NavigationView (220px)                         │ 详情面板 340px       │
│  · 容器                                       │ (按 page 切换内容)   │
│  · 镜像                                       │                     │
│  · 网络                                       │                     │
│  · 卷                                         │                     │
│  ─ 分割                                        │                     │
│  · 统计                                       │                     │
│  · 构建                                       │                     │
├──────────┴─────────────────────────────────────┴───────────────────┤
│ 内容区：每个 page 一个 Grid（搜索 + 工具栏 + 表头 + ListView + 空态） │ ← 7 个 Grid 用 Visibility 互斥
├────────────────────────────────────────────────────────────────────┤
│ 日志抽屉把手 (Auto, Ctrl+L 切换)                                    │ ← FontIcon E70E/E70D 切
│ 日志抽屉面板 (Auto, 230px) ← `Background="{ThemeResource LogPaneBg}"` │
├────────────────────────────────────────────────────────────────────┤
│ 状态栏 28px：Status | StatusBarSummary · wslc 2.9.9                  │
└────────────────────────────────────────────────────────────────────┘
```

**ViewModel 与 XAML 配套（6 个 page 互斥切换）**：`MainViewModel` 暴露 `ResourcePage` 枚举 + `CurrentPage` 偏属性；六个 `IsContainersPage` / `IsImagesPage` / `IsNetworksPage` / `IsVolumesPage` / `IsStatsPage` / `IsBuildPage` 派生 `bool` 由 `OnCurrentPageChanged` 同步翻转，XAML 用 `Visibility="{x:Bind conv:BoolConverters.ToVisibility(ViewModel.IsXxxPage), Mode=OneWay}"` 互斥。`SearchText` 在切 page 时被 `OnCurrentPageChanged` 清空，避免跨页 filter 残留。

### `App.xaml` 资源键

| 资源 | 用途 | 备注 |
|------|------|------|
| `StatusRunning{Bg,Bd,Fg}` / `StatusStopped{...}` / `StatusError{...}` / `StatusBusy{...}` | 状态徽章语义色 | 走 `ResourceDictionary.ThemeDictionaries` 的 `Dark`/`Light` 字典，`RequestedTheme` 切换时自动生效 |
| `LogPaneBg` | 日志抽屉面板底色 | 同上 |
| `HeaderButtonStyle` | 表头排序按钮（透明无边框） | `TargetType="Button"`，点击反馈靠 WinUI 3 内置 |
| `MetricCardStyle` | 统计页指标卡 | `TargetType="Border"`，统一圆角 6 / 描边 1 / Padding 14,10 |
| `KvRowStyle` | 详情面板键值行 | `TargetType="Grid"`，只接管 margin / padding / spacing；**列定义必须在使用处显式声明**（`Style` 的 `Setter` 不能加 `ColumnDefinitions`） |

**WinUI 3 无内置 DataGrid**：数据列表统一用 `ListView` + `DataTemplate` + 内嵌 `Grid`，**表头行和项模板必须使用同一套 `ColumnDefinition` 集合**（例如容器页 `1.3* / 1.6* / 120 / 1.2* / 110 / 104`），列才能对齐。本项目刻意不引 CommunityToolkit DataGrid 避免重包与版本耦合。

### `MainWindow.xaml` 25 处 kv 行布局（关键修复记录）

`<Grid Style="{StaticResource KvRowStyle}">` 内部需要 `<Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>` + 左侧 TextBlock `Grid.Column="0"` + 右侧 TextBlock（`HorizontalAlignment="Right"`）`Grid.Column="1"`。Style **不能**通过 `Setter` 给 `Grid` 注入 `ColumnDefinitions`，必须每处显式。**2026-08-28 落地时一次性 Python 注入**：25 个 Grid 全部补齐列定义 + 两列标签，跑 `dotnet build` 0 错误 0 警告。

### `ViewModel.Theme` → `Root.RequestedTheme` + 持久化

WinUI 3 的 `Grid`（`FrameworkElement`）有 `RequestedTheme` 属性，`MainWindow` 构造时订阅 `ViewModel.PropertyChanged`，在 `Theme` 变化时同步到 `Root.RequestedTheme`，顶栏「主题」按钮生效。

- **⚠️ 枚举映射坑**：`ElementTheme`（Default=0, Light=1, Dark=2）与 `ApplicationTheme`（Light=0, Dark=1）数值不一致，**禁止强转**（强转会把 Light 存成 Dark、Dark 存成非法值 2）。必须按语义映射（`MainWindow.xaml.cs` 已实现，勿回退）。
- 持久化走 `Services/UserSettings.cs` → `%LOCALAPPDATA%\wslcUI\settings.json`（unpackaged 无包身份，不能用 `ApplicationData.Current.LocalSettings`）。App 构造期读回设 `Application.RequestedTheme`，MainWindow 再映射到 `ElementTheme`。有 15 个单元测试（`tests/wslcUI.Tests`）。

### 数据刷新与绑定模式约定

- `ContainerInfo` 实现 `INotifyPropertyChanged`：**刷新走就地合并**（`MainViewModel.UpdateContainersInPlace`，按 Name 复用实例 + 差量增删），**不要改回整体替换 `Containers` 集合**——那会让 `SelectedContainer` 脱离列表，触发 ListView 布局期异步清空选中的竞态。
- 因为行不再重建，容器行模板与详情面板的所有可变字段绑定**必须 `Mode=OneWay`**（依赖 INPC）；`OneTime` 只可用于 `Name`/`Id` 这类不可变字段，或镜像/网络/卷页（模型仍是整体替换 + 行重建）。
- `MergeStatsIntoContainers` 对不在 stats 快照里的容器会重置回 `—` + `HasStats=false`，防止显示过期数值。
- NavigationView 选中与 `VM.CurrentPage` 双向同步：用户点击走 `NavView_SelectionChanged` → `NavigateCommand`；非导航入口切页（如空态「去拉取镜像」）由 `SyncNavSelection` 反向同步高亮。`Tag` 值必须与 `ResourcePage` 枚举名一致。

## 5. 当前进度（接手从这里看）

**已真正实现**（`src/wslcUI/Services/WslcSdkClient.cs`）：

- `IsReadyAsync` → `WslcService.GetMissingComponents()` 前置检查。
- `GetSessionAsync` → 建 `SessionSettings` + `Session.Start()`（信号量单例保护）。
- `PullImageAsync` → 带 `IProgress<(Status,Current,Total)>` 进度回调。
- `RunAndCaptureAsync` → `CreateContainer` + 订阅 `Process` 的 `OutputReceived/ErrorReceived/Exited` 事件流，捕获 stdout/stderr。
- `ListImagesAsync` → `Session.GetImages()`（纯 SDK，映射 `Name`/`Sha256`→`Digest`/`Size`/`CreatedTimestamp`；**只有 SDK 侧镜像有完整 sha256**，CLI 侧镜像 `Digest` 留空、详情面板显示「—」，不用假数据填充）。
- `Dispose` → 终止 Session。

**容器列举 / 按名启停 —— 已用 `wslc` CLI 桥接实现（`WslcCli.cs`），不是空壳：**

- `ListContainersAsync`：调 `wslc list -a`，用按表头推导列宽的表格解析（`WslcCli.ParseContainerList`）。wslc 2.9.9 的中文 locale 表头 `容器 ID / 名称 / 映像 / 已创建 / 状态 / 端口` 也能正确切片。**不要用 `--format json`**——wslc 2.9.9 的 JSON 形态不一致（list/images/stats 是单对象、network 是 NDJSON），早期用 JSON 优先的解析路径在真实环境会静默返回空列表。
  - 解析器已用真机 `wslc list -a` 联调，列对齐✅。若有新需求（新增列、改名）只需在 `ParseContainerList` 里加一行 `FindColumn(...)` 而不动 split。
  - 解析出的 `ContainerInfo` 字段（`src/wslcUI/Models/ContainerInfo.cs`）：`Id` / `Name` / `Image` / `Status` / `Ports` / `CreatedAt` + 派生 `StatusKind`（Running/Stopped/Error/Unknown，按 zh-CN + English 双向归一）/`IsRunning`/`IsStopped`/`IsError`/`IsUnknownStatus`/`StatusLabel`（徽章中文文案），其中 `Ports` / `CreatedAt` 默认 `—`（CLI 无该列时降级）。stats 字段 `Cpu` / `Mem` / `MemPercent` / `NetIo` / `Pids`（默认 `—`）+ `HasStats` 由 `MainViewModel.MergeStatsIntoContainers` 按 Name 从 `wslc stats` 快照回填。
- `StartAsync(name)` / `StopAsync(name)`：分别调 `wslc start <name>` / `wslc stop <name>`，非零退出抛 `InvalidOperationException`。
- `DeleteContainerAsync(name)`：`wslc rm <name>`，非零退出抛异常。
- `DeleteImageAsync(reference)`：`wslc image rm <reference>`（CLI 必然支持该子命令；首次联调仅核对退出码语义与报错文案）。
- `GetLogsAsync(name)`：`wslc logs <name>`，返回 stdout（非 `-f` 跟随）。
- **为什么不用 SDK**：2.9.9 的 C# 投影**没有** `Session.GetContainers()`，也**没有** `Session.GetContainer(name)`（见 [Known Gaps](https://wsl.dev/api-reference/csharp/known-gaps/)）。所以"列出所有容器 / 按名取回引用 / 删除 / 日志"在纯 SDK 下做不到，CLI 桥接是唯一路径。这是对"API 优先"原则的务实例外，已在 `WslcSdkClient.cs` 顶部注释标明。

**网络 / 卷（network / volume）—— 已实现，整组走 `wslc` CLI 桥接（`WslcCli.cs`）：**

- 2.9.9 的 C# 投影**完全没有** network / volume 资源类型，因此这组与容器列举/启停一样属于"SDK 无投影 → CLI 桥接"的路径。
- `ListNetworksAsync`：`wslc network ls`（JSON 优先，失败回退 docker 风格表格：`NETWORK ID | NAME | DRIVER | SCOPE` 取 NAME/DRIVER/SCOPE）。
- `CreateNetworkAsync(name)`：`wslc network create <name>`，空名抛 `ArgumentException`，非零退出抛 `InvalidOperationException`。
- `RemoveNetworkAsync(name)`：`wslc network remove <name>`，非零退出抛异常。
- `ListVolumesAsync`：`wslc volume ls`（JSON 优先，失败回退 docker 风格表格：`DRIVER | VOLUME NAME` 取 DRIVER/NAME）。
- `CreateVolumeAsync(name)`：`wslc volume create <name>`，空名抛 `ArgumentException`，非零退出抛异常。
- `RemoveVolumeAsync(name)`：`wslc volume remove <name>`，非零退出抛异常。
- 解析器尚未在真实 wslc 上联调，首次仅核对输出**列格式**（`network ls` / `volume ls` 子命令必然存在，JSON 不可用会自动回退表格解析，无需怀疑标志）。
- UI 已接：MainWindow 用 `NavigationView` 三栏布局（侧栏 220px 容器/镜像/网络/卷/分隔/统计/构建 / 内容 `*` / 详情面板 340px 可关），网络页与卷页各含"名称输入框 + 创建 + 删除"面板，绑定 `NewNetworkName`/`NewVolumeName` 与对应命令。

**资源监控 stats —— 已实现，走 `wslc` CLI 桥接（`WslcCli.cs`）：**

- 2.9.9 的 C# 投影**没有** stats / 资源监控端点（SDK 对象模型 `WslcService`/`Session`/`Container`/`Process` 均无对应成员），因此与容器列举/启停、网络/卷同属"SDK 无投影 → CLI 桥接"路径。
- `GetStatsAsync`：`wslc stats`（wslc 默认就是一次性快照，**不接受** docker 的 `--no-stream` 标志——运行会报"选项名称未被识别"）。
  `WslcCli.RunAsync` 保留**取消即杀进程**安全网（`ct.Register(() => proc.Kill())`）作为通用防护，stats 也**不**进主 `RefreshCommand` 而是独立的 `RefreshStatsCommand`，避免任何潜在卡顿波及主刷新。已用真机联调，中文表头 `容器 ID / 名称 / CPU 百分比 / 最大用量/限制 / 内存百分比 / 网络 I/O / 块 I/O / PIDS` 8 列按列宽解析 ✅。
- `ParseStats`：表格优先，按表头推导列宽解析；列名定位而非硬编码下标，新增列只需补一个 `Idx(...)`。
- UI 已接：MainWindow 的 `NavigationView` "统计" 页：搜索 + 「刷新快照」按钮（独立 `RefreshStatsCommand` 不进主刷新，避免 stats 卡顿波及主流程）+ 4 个指标卡（运行中 / 已停止或异常 / 快照数 / 镜像网络卷）+ 列表（容器 / CPU% / 内存·限制 / 内存% / 网络 I/O / 块 I/O / PID），绑定 `Stats` 与 `RefreshStatsCommand`。

**镜像构建 build —— 已实现，走 `wslc` CLI 桥接（`WslcCli.cs`）：**

- 2.9.9 SDK **没有** Dockerfile 构建的投影（只有运行期 `CreateContainer`）；镜像构建走 `wslc build -t <tag> <context>`（从目录里的 `Dockerfile`/`Containerfile` 构建）。
- `BuildImageAsync(contextDir, tag, progress)`：`wslc build -t <tag> "<contextDir>"`，用 `OutputDataReceived` 逐行流式回传构建日志（非一次性读到底），非零退出抛 `InvalidOperationException`。空标签抛 `ArgumentException`；目录 / Dockerfile 缺失抛 `DirectoryNotFoundException`/`FileNotFoundException`。
- UI 已接：MainWindow 的 `NavigationView` "构建" 页，含"上下文目录输入框 + 浏览(FolderPicker) + 标签输入框 + 构建按钮 + 只读滚动日志"，绑定 `BuildContext`/`PickBuildContextCommand`/`BuildTag`/`BuildImageCommand`/`BuildOutput`。`FolderPicker` 经 `InitializeWithWindow` 挂到 MainWindow 句柄（`MainViewModel.OwnerHandle`，由 `MainWindow` 构造时设置）。

**交互式终端 exec/attach —— 已实现（ConPTY + XTerm.NET cell 渲染）**

- 走 `wslc exec -it <name> /bin/sh`，但**不是**普通管道：用 Windows Pseudoconsole（ConPTY）P/Invoke 封装（`Services/ConPty.cs`，零外部 NuGet 依赖）给容器 shell 一个**真 TTY**，使行编辑 / 颜色 / 全屏程序正常。
- **渲染（R3，2026-09-01）**：`TerminalWindow` 是独立窗口（容器页"终端"按钮 / 行双击打开，经 `ProbeHealth` 预检兜底）；PTY 字节流 → UI 线程 → `XTerm.Terminal.Write`（nuget `XTerm.NET` 1.1.2，MIT，headless），`Terminal/TerminalView.cs` 订阅 `BufferChanged` 把 cell 矩阵渲染为行级 TextBlock（连续同属性合并 run，256 色/真彩色/bold/inverse/CJK 双宽）。**Resize 三级联动**：窗口 SizeChanged → `Terminal.Resize` → `PseudoConsole.Resize`。选型依据见 `docs/TERMINAL-RENDER-DECISION.md`（双盲 spike：自研原型踩中 VT100 延迟换行坑 vs XTerm.NET 4/4 真实流零修改）。R1 的 `VtStripper`+RichTextBlock 路径已退位（保留在 `Services/VtStripper.cs` 作回退种子）。
- ⚠️ **本机（Win11 26200.9278 insider）存在机器级 ConPTY attach 故障**：任何 PTY 子进程 `0xC0000142` 启动即死（三重实验定责系统，与 wslcUI 无关）。产品侧 `PseudoConsole.ProbeHealth()`（cmd /c exit 0 探测，5 分钟缓存）在开窗前拦截并弹诊断。Windows 更新后跑 `dotnet run --project tools/wslcUI.Verify` 复验（预检行恢复 + V5-V7 PASS 即闭环）。
- ✅ **P/Invoke 自检史**：2026-07-20 对照官方 ConptyExample 修正 STARTUPINFOW 结构错位与 `ref IntPtr`；2026-08-31 真机验证抓到致命 bug——`UpdateProcThreadAttribute` 的 `lpValue` 直接传了 `_hPC` 值（应为指向 HPCON 的指针，`AllocHGlobal`+`WriteIntPtr` 构造），此前 PTY 属性从未生效、子进程一直继承父控制台（commit `aa35cac`）。

**路线图（剩余可选增强）**：

- ~~完整 VT 终端渲染~~ ✅ 已完成（R1 SGR → R2 选型 → R3 XTerm.NET cell 渲染，见 `docs/TERMINAL-RENDER-DECISION.md`）；视觉真机验收待本机 ConPTY 故障修复（见上）。
- 镜像自动构建改为 SDK 的 `<WslcImage>` MSBuild 集成（CI 打包用，适合把 wslcUI 自身打包成镜像）。
- ~~真键盘事件转发~~ ✅ 已完成（R4）：`TerminalInputMapper`（纯函数可单测）+ `TerminalView` 聚焦/KeyDown/CharacterReceived/粘贴（Shift+Insert、Ctrl+Shift+V）→ `InputData` 事件 → PTY stdin；InputBox 已收起（仅 PTY 启动失败时作错误横幅）。注意：WinRT 的 Alt 是 `VirtualKeyModifiers.Menu`；`Key` 枚举无字母键，Ctrl/Alt+字母由映射器自合成。剩余：attach 到已运行进程。

## 6. 关键约束与坑

- **API 优先，但容器列举/启停 + 网络/卷整套 CRUD + 资源监控 stats + 镜像构建 + 交互式终端(exec) 是已知 SDK 缺口，已用 CLI 桥接（不是"封装 CLI 一切"）**：镜像列举/拉取/运行等 SDK 覆盖的操作继续走 SDK；容器列举/`start`/`stop`/`rm`/`logs`、`network`/`volume` 全部子命令、`stats`、`build -t`、`exec -it` 因 SDK 无投影才走 CLI。不要为其他本可用 SDK 的操作也加 CLI 封装。
- **CLI 必然支持所有已桥接的子命令**（`wslc list/start/stop/rm/logs`、`image rm`、`network`/`volume` 全套、`stats`、`build`、`exec -it`）。CLI 是原生事实来源、永远先于 SDK；滞后的只是预览版 `Microsoft.WSL.Containers` SDK 投影。**不要再把"子命令是否存在"列为风险** —— 联调时只需核对输出**列格式**，退出码语义（非零抛异常）已就位。
- **`--format json` 不可靠**：wslc 2.9.9 不同子命令的 JSON 形态不一致（list/images/stats 是单对象、network 是 NDJSON），早期"JSON 优先回退表格"的代码在真实环境静默返回空列表。**统一走按表头推导列宽的表格解析**（`WslcCli.SplitByColumns` / `ComputeColumnBoundaries` / `FindColumn`）。
- **wslc stats 不接受 docker 标志**：`--no-stream` / `-f` 之类都会报"选项名称未被识别"——wslc 默认就是一次性快照，**不要**在 stats 里传 `--no-stream`。
- **锁定 SDK 版本 `Microsoft.WSL.Containers` 2.9.9**（与 wslc 2.9.9.0 对齐）。GA（预计 2026 秋）前的破坏性变更需重编译；升级先比对 API 参考。
- **已核对过的关键 SDK 成员名（2.9.9，对照 [C# API 参考](https://wsl.dev/api-reference/csharp/)，并用独立控制台项目对 2.9.9 包做编译验证）**：`GetMissingComponents()` 返回 `IReadOnlyList<Component>`（判空用 `.Count == 0`，**不是** `ComponentFlags.None`）；`ProcessSettings.CommandLine`（**不是** `CmdLine`）；`Session.GetImages()` 返回 `IReadOnlyList<ImageInfo>`（`Name`/`Sha256`(IBuffer)/`Size`(ulong)/`CreatedTimestamp`）；`Container.Delete(DeleteContainerOption.None|Force)`；`Signal.SIGTERM`。`EnableAutoRemove` 官方示例未出现，已移除（改显式 `Delete`）。**⚠️ 2.9.9 的 `InitProcess.OutputReceived`/`ErrorReceived` 回调参数是 `byte[]`（2.9.3/2.9.4 是 `IBuffer`）**：`WslcSdkClient.cs` 里的 `data.ToArray()` 写法两态兼容（`IBuffer` 走 WinRT 扩展、`byte[]` 走 LINQ），无需改；若日后清理可改为直接 `Encoding.UTF8.GetString(data)`。
- **⚠️ 命名空间隔离（2.9.9 实测结论，改代码前必读）**：SDK `Session`（storagePath，如 `%LOCALAPPDATA%\wslcUI\session`）与 `wslc` CLI（WSL2 rootfs / 默认会话）**互不可见**。实测：SDK 拉的镜像、建的容器，`wslc list -a` / `wslc images` 都看不到；反之 CLI 的容器 `Session.OpenContainer` 也打不开。因此：容器列举/启停/删除/日志的 CLI 桥接**不可**迁移到 SDK（UI 管理的是 CLI 命名空间容器）；`Session.OpenContainer(name, mode)` 只能操作 SDK 会话自己 `CreateContainer` 的容器（2.9.9 新增，已实测：可打开、`Stop(Signal, TimeSpan)`、`Delete(Force)` 可用，但 **Stop 后不能 Start**，抛 `InvalidOperationException`）。
- **2.9.9 SDK 相对 2.9.3 的新 API（反射 diff + 实测）**：`Session.OpenContainer(name, mode)`、`Session.DeleteImage(nameOrId)`、`PushImage(Async)`+`PushImageOptions`、`TagImage`+`TagImageOptions`、`ImportImage(Async)(path, name)`、`LoadImage(Async)(path)`、`CreateVhdVolume`/`DeleteVhdVolume`、`Authenticate(Uri,user,pass)`、事件 `Terminated`/`ProcessCrashed`。**其中只有镜像删除被 wslcUI 采用**：`DeleteImageAsync` 现在是命名空间感知的 —— reference 命中 `Session.GetImages()`（忽略大小写）则 `Session.DeleteImage`，否则回退 `wslc image rm`（修复了"SDK 镜像 UI 可见但删不掉"的 bug）。其余新 API 因命名空间分裂、UI 合并列表下对 CLI 镜像无意义而暂未接入（Tag/Push/Import/Load 如需接入，需在 UI 上区分镜像来源或只对 SDK 镜像开放）。
- **待在真实 wslc 上联调（只核对输出格式，不怀疑子命令/标志是否存在）**：`WslcCli` 各解析器（`ParseList`/`ParseNetworkList`/`ParseVolumeList`/`ParseStats`）的**输出列格式**；`wslc start/stop <name>` 的退出码语义（非零抛异常已就位）。
- MainWindow 当前用 `IWslcClient`，切换 Fake/SDK 时 UI 代码不应改动。

## 7. 建议的下一步

1. ✅ 容器全生命周期骨架已实现：列表（`wslc list -a`）/ 启停 / 删除（`wslc rm`）/ 日志（`wslc logs`）走 CLI 桥接；镜像列举/拉取/运行走 SDK；镜像删除走 `wslc image rm`。UI 已含镜像输入框+拉取、删除容器/镜像按钮、日志面板。下一步在装有真实 wslc 的 Windows 上联调：核对 `WslcCli` 的 `list` 解析输出格式、`start/stop/rm/logs/image rm` 退出码语义，必要时收紧解析。
2. 把 `MainViewModel` 的 `Containers` / `Images` 集合接到真实数据，验证 MVVM 绑定链路（双栏列表 + 选中启停/删除 + 日志面板已接好）。
3. ✅ network / volume CRUD 已实现，整组走 `wslc` CLI 桥接（同 `IWslcClient`）。真机联调已通过：`wslc network create/remove` 退出码 0/非零语义正常，`network ls` 表头 `NETWORK ID / NAME / DRIVER / SCOPE` 与代码期望一致（按列宽解析），`volume ls` 表头 `DRIVER / VOLUME NAME` 同上。
4. ✅ 资源监控 stats 已实现，走 `wslc stats` CLI 桥接（`WslcCli.ParseStats` + 取消即杀进程安全网 + 独立 `RefreshStatsCommand`）。真机联调已通过：表头 `容器 ID / 名称 / CPU 百分比 / 最大用量/限制 / 内存百分比 / 网络 I/O / 块 I/O / PIDS` 8 列按列宽正确切片。**注意**：wslc 不接受 `--no-stream`，默认就是一次性快照。
5. ✅ 镜像构建 + 交互式终端均已实现（`wslc build -t` CLI 桥接 / `wslc exec -it` + ConPTY + XTerm.NET cell 渲染）。CLI 侧已真机验证（verify 工具 9/9，见 `tools/wslcUI.Verify`）；ConPTY 侧 lpValue bug 已修（`aa35cac`），**视觉验收卡在本机系统级 ConPTY 故障**（见第 5 节交互式终端段），待 Windows 更新后复验。
6. 终端增强剩余项（优先级从高到低）：P1a ConPTY 复验闭环（一条命令，见 `docs/REMAINING-PLAN-2026-08-31.md`）；错误码映射表随用随补（`WslcCli.ErrorHints`）；输入侧升级 `Terminal.GenerateKeyInput`；attach 到已运行进程。
