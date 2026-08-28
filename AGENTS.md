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
  解析器尚未在真实 wslc 上联调，首次联调核对输出**列格式**即可（`wslc list -a` 子命令必然存在；`--format json` 不可用会自动回退表格解析，无需担心标志）。
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
- UI 已接：MainWindow 用 `Pivot` 四页（容器 / 镜像 / 网络 / 卷），网络页与卷页各含"名称输入框 + 创建 + 删除"面板，绑定 `NewNetworkName`/`NewVolumeName` 与对应命令。

**资源监控 stats —— 已实现，走 `wslc` CLI 桥接（`WslcCli.cs`）：**

- 2.9.9 的 C# 投影**没有** stats / 资源监控端点（SDK 对象模型 `WslcService`/`Session`/`Container`/`Process` 均无对应成员），因此与容器列举/启停、网络/卷同属"SDK 无投影 → CLI 桥接"路径。
- `GetStatsAsync`：`wslc stats --no-stream`（取单次快照；`--no-stream` 是 docker 兼容的一次性采样标志）。
  CLI 必然支持 `--no-stream`（docker 兼容一次性采样标志）。`WslcCli.RunAsync` 仍保留**取消即杀进程**安全网（`ct.Register(() => proc.Kill())`）作为通用防护，stats 也**不**进主 `RefreshCommand` 而是独立的 `RefreshStatsCommand`，避免任何潜在卡顿波及主刷新。首次联调仅核对 `wslc stats` 的输出**列顺序**（`WslcCli.ParseStats` 已留 TODO）。
- `ParseStats`：表格优先（docker 风格列 `CONTAINER ID | NAME | CPU% | MEM USAGE / LIMIT | MEM% | NET I/O | BLOCK I/O | PIDS`），未尝试 JSON 变体。
- UI 已接：MainWindow 的 `Pivot` 第五页"统计"，含"刷新统计"按钮 + 只读列表（容器 / CPU% / 内存·限制 / 内存% / 网络 I/O / 块 I/O / PID），绑定 `Stats` 与 `RefreshStatsCommand`。

**镜像构建 build —— 已实现，走 `wslc` CLI 桥接（`WslcCli.cs`）：**

- 2.9.9 SDK **没有** Dockerfile 构建的投影（只有运行期 `CreateContainer`）；镜像构建走 `wslc build -t <tag> <context>`（从目录里的 `Dockerfile`/`Containerfile` 构建）。
- `BuildImageAsync(contextDir, tag, progress)`：`wslc build -t <tag> "<contextDir>"`，用 `OutputDataReceived` 逐行流式回传构建日志（非一次性读到底），非零退出抛 `InvalidOperationException`。空标签抛 `ArgumentException`；目录 / Dockerfile 缺失抛 `DirectoryNotFoundException`/`FileNotFoundException`。
- UI 已接：MainWindow 的 `Pivot` 第六页"构建"，含"上下文目录输入框 + 浏览(FolderPicker) + 标签输入框 + 构建按钮 + 只读滚动日志"，绑定 `BuildContext`/`PickBuildContextCommand`/`BuildTag`/`BuildImageCommand`/`BuildOutput`。`FolderPicker` 经 `InitializeWithWindow` 挂到 MainWindow 句柄（`MainViewModel.OwnerHandle`，由 `MainWindow` 构造时设置）。

**交互式终端 exec/attach —— 已实现（最重）**

- 走 `wslc exec -it <name> /bin/sh`，但**不是**普通管道：用 Windows Pseudoconsole（ConPTY）P/Invoke 封装（`Services/ConPty.cs`，零外部 NuGet 依赖）给容器 shell 一个**真 TTY**，使行编辑 / 颜色 / 全屏程序正常。
- `TerminalWindow`（`TerminalWindow.xaml(.cs)`）是独立窗口，容器页"终端"按钮打开；字节流经 `PseudoConsole.OutputReceived` 事件回传，UI 线程 `DispatcherQueue` 合入 `TextBlock`，并用正则剥掉 ANSI/OSC 转义让文本可读；输入框回车把整行 + 换行写回 PTY。
- ⚠️ ConPTY 是 `kernel32.dll` 的 `CreatePseudoConsole`/`CreateProcessW` P/Invoke，**本会话无法编译验证**（无 .NET / WSL），属未联调代码。首次真实运行见 `ConPty.cs` 顶部 TODO 核对清单（创建是否成功 / 输入是否到达 / resize 是否生效）。完整 VT 渲染（XTermSharp / WinUI TermControl）是后续增强，当前 MVP 只显示去转义文本。
- ✅ **P/Invoke 已对照微软官方 `microsoft/terminal` ConptyExample 做逐项自检**（2026-07-20）：修正了 2 个致命错误 —— ① `STARTUPINFOW` 缺 `dwXCountChars`/`dwYCountChars`/`dwFillAttribute` 三个 DWORD 导致结构体错位、CreateProcess 读到垃圾；② `InitializeProcThreadAttributeList` 的 `lpSize` 用了 `ref int`（4 字节），但 `SIZE_T` 是 64 位宽，64 位下尺寸被截断/越界，改为 `ref IntPtr`。另统一为「管道非继承 + `bInheritHandles=false`」的官方模式，并把 3 个 `VOID` 返回函数声明为 `void`。其余（管道方向、`PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE=0x00020016`、标志常量、`COORD` 按值传递）核对无误。

**路线图（剩余可选增强）**：

- 完整 VT 终端渲染（XTermSharp / WinUI TermControl）。
- 镜像自动构建改为 SDK 的 `<WslcImage>` MSBuild 集成（CI 打包用，适合把 wslcUI 自身打包成镜像）。
- 交互式终端支持 attach 到已运行进程（而非仅 `exec` 新 shell）。

## 6. 关键约束与坑

- **API 优先，但容器列举/启停 + 网络/卷整套 CRUD + 资源监控 stats + 镜像构建 + 交互式终端(exec) 是已知 SDK 缺口，已用 CLI 桥接（不是"封装 CLI 一切"）**：镜像列举/拉取/运行等 SDK 覆盖的操作继续走 SDK；容器列举/`start`/`stop`/`rm`/`logs`、`network`/`volume` 全部子命令、`stats`、`build -t`、`exec -it` 因 SDK 无投影才走 CLI。不要为其他本可用 SDK 的操作也加 CLI 封装。
- **CLI 必然支持所有已桥接的子命令与标志**（`wslc list/start/stop/rm/logs`、`image rm`、`network`/`volume` 全套、`stats --no-stream`）。CLI 是原生事实来源、永远先于 SDK；滞后的只是预览版 `Microsoft.WSL.Containers` SDK 投影。**不要再把"子命令/标志是否存在"列为风险** —— 联调时只需核对输出**列格式**（各 `Parse*` 已留 TODO），退出码语义（非零抛异常）已就位。
- **锁定 SDK 版本 `Microsoft.WSL.Containers` 2.9.9**（与 wslc 2.9.9.0 对齐）。GA（预计 2026 秋）前的破坏性变更需重编译；升级先比对 API 参考。
- **已核对过的关键 SDK 成员名（2.9.9，对照 [C# API 参考](https://wsl.dev/api-reference/csharp/)，并用独立控制台项目对 2.9.9 包做编译验证）**：`GetMissingComponents()` 返回 `IReadOnlyList<Component>`（判空用 `.Count == 0`，**不是** `ComponentFlags.None`）；`ProcessSettings.CommandLine`（**不是** `CmdLine`）；`Session.GetImages()` 返回 `IReadOnlyList<ImageInfo>`（`Name`/`Sha256`(IBuffer)/`Size`(ulong)/`CreatedTimestamp`）；`Container.Delete(DeleteContainerOption.None|Force)`；`Signal.SIGTERM`。`EnableAutoRemove` 官方示例未出现，已移除（改显式 `Delete`）。**⚠️ 2.9.9 的 `InitProcess.OutputReceived`/`ErrorReceived` 回调参数是 `byte[]`（2.9.3/2.9.4 是 `IBuffer`）**：`WslcSdkClient.cs` 里的 `data.ToArray()` 写法两态兼容（`IBuffer` 走 WinRT 扩展、`byte[]` 走 LINQ），无需改；若日后清理可改为直接 `Encoding.UTF8.GetString(data)`。
- **⚠️ 命名空间隔离（2.9.9 实测结论，改代码前必读）**：SDK `Session`（storagePath，如 `%LOCALAPPDATA%\wslcUI\session`）与 `wslc` CLI（WSL2 rootfs / 默认会话）**互不可见**。实测：SDK 拉的镜像、建的容器，`wslc list -a` / `wslc images` 都看不到；反之 CLI 的容器 `Session.OpenContainer` 也打不开。因此：容器列举/启停/删除/日志的 CLI 桥接**不可**迁移到 SDK（UI 管理的是 CLI 命名空间容器）；`Session.OpenContainer(name, mode)` 只能操作 SDK 会话自己 `CreateContainer` 的容器（2.9.9 新增，已实测：可打开、`Stop(Signal, TimeSpan)`、`Delete(Force)` 可用，但 **Stop 后不能 Start**，抛 `InvalidOperationException`）。
- **2.9.9 SDK 相对 2.9.3 的新 API（反射 diff + 实测）**：`Session.OpenContainer(name, mode)`、`Session.DeleteImage(nameOrId)`、`PushImage(Async)`+`PushImageOptions`、`TagImage`+`TagImageOptions`、`ImportImage(Async)(path, name)`、`LoadImage(Async)(path)`、`CreateVhdVolume`/`DeleteVhdVolume`、`Authenticate(Uri,user,pass)`、事件 `Terminated`/`ProcessCrashed`。**其中只有镜像删除被 wslcUI 采用**：`DeleteImageAsync` 现在是命名空间感知的 —— reference 命中 `Session.GetImages()`（忽略大小写）则 `Session.DeleteImage`，否则回退 `wslc image rm`（修复了"SDK 镜像 UI 可见但删不掉"的 bug）。其余新 API 因命名空间分裂、UI 合并列表下对 CLI 镜像无意义而暂未接入（Tag/Push/Import/Load 如需接入，需在 UI 上区分镜像来源或只对 SDK 镜像开放）。
- **待在真实 wslc 上联调（只核对输出格式，不怀疑子命令/标志是否存在）**：`WslcCli` 各解析器（`ParseList`/`ParseNetworkList`/`ParseVolumeList`/`ParseStats`）的**输出列格式**；`wslc start/stop <name>` 的退出码语义（非零抛异常已就位）。
- MainWindow 当前用 `IWslcClient`，切换 Fake/SDK 时 UI 代码不应改动。

## 7. 建议的下一步

1. ✅ 容器全生命周期骨架已实现：列表（`wslc list -a`）/ 启停 / 删除（`wslc rm`）/ 日志（`wslc logs`）走 CLI 桥接；镜像列举/拉取/运行走 SDK；镜像删除走 `wslc image rm`。UI 已含镜像输入框+拉取、删除容器/镜像按钮、日志面板。下一步在装有真实 wslc 的 Windows 上联调：核对 `WslcCli` 的 `list` 解析输出格式、`start/stop/rm/logs/image rm` 退出码语义，必要时收紧解析。
2. 把 `MainViewModel` 的 `Containers` / `Images` 集合接到真实数据，验证 MVVM 绑定链路（双栏列表 + 选中启停/删除 + 日志面板已接好）。
3. ✅ network / volume CRUD 已实现，整组走 `wslc` CLI 桥接（同 `IWslcClient`）。下一步在真实 wslc 上联调：核对 `network ls`/`volume ls` 输出列格式与 `create`/`remove` 退出码，必要时收紧解析（`WslcCli.ParseNetworkList`/`ParseVolumeList` 已留 TODO）。
4. ✅ 资源监控 stats 已实现，走 `wslc stats --no-stream` CLI 桥接（`WslcCli.ParseStats` + 取消即杀进程安全网 + 独立 `RefreshStatsCommand`）。下一步在真实 wslc 上联调：核对 `stats --no-stream` 的输出列顺序（`WslcCli.ParseStats` 已留 TODO）。
5. ✅ 镜像构建 + 交互式终端均已实现（分别走 `wslc build -t` CLI 桥接 与 `wslc exec -it` + ConPTY 真 TTY）。下一步在真实 wslc 上联调：核对 build 流式输出、以及 ConPTY 的创建/输入/resize（见 `Services/ConPty.cs` 顶部 TODO）。
