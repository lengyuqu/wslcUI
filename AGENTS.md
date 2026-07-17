# AGENTS.md — wslcUI

> 给接手本仓库的编码 agent（CodeBuddy / Claude / Cursor 等）的速查与防坑指引。
> 详细架构见 `docs/ARCHITECTURE.md`，项目说明见 `README.md`。

## 1. 这是什么

Windows 原生 UI 的 **WSL 容器（wslc）图形管理器**。本质 = 给 `wslc` 套一个 WinUI 3 原生 GUI 外壳。

- 技术栈：**C# + .NET 8 + WinUI 3（Windows App SDK 1.6）+ CommunityToolkit.Mvvm**。
- 后端集成 **API 优先**：直接引用 `Microsoft.WSL.Containers` NuGet 包（wslc 官方 C# 托管 SDK），**不封装 CLI**。

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
- `Dispose` → 终止 Session。

**⚠️ 仍是空壳（TODO，返回空列表 / 只 `await Task.Yield()`）—— 最先要补的：**

- `ListContainersAsync` / `ListImagesAsync`：枚举接口名待对照 API 参考。
- `StartAsync(name)` / `StopAsync(name)`：按名查找 + 启停，接口名待确认。

**未实现**（路线图）：

| 功能 | 说明 |
|------|------|
| 资源监控 stats | 预览 SDK 未明确对应端点，可能需 CLI 兜底（单独写适配器实现 `IWslcClient`） |
| network / volume CRUD | 待确认 SDK 覆盖度 |
| 镜像自动构建 | 可用 `<WslcImage>` MSBuild 集成 |
| 交互式终端 exec/attach | `Process` 已给 stdin/stdout 字节流，需配 ConPTY / XTermSharp 控件渲染 |

## 6. 关键约束与坑

- **API 优先，不要改回 CLI 封装**：wslc 与 SDK 同为预览、风险等价；SDK 调用少一层胶水代码，接口变更编译期可见。
- **锁定 SDK 版本 `Microsoft.WSL.Containers` 2.9.3**（与 wslc 2.9.3.0 对齐）。GA（预计 2026 秋）前的破坏性变更需重编译；升级先比对 API 参考。
- **编译前先核对 `WslcSdkClient.cs` 顶部 TODO**：`SessionSettings.MemoryMB` vs `MemorySizeInMB`、枚举方法名、`GetContainer(name)` 等预览 SDK 成员名可能需调整。
- MainWindow 当前用 `IWslcClient`，切换 Fake/SDK 时 UI 代码不应改动。

## 7. 建议的下一步

1. 对照 https://wsl.dev/api-reference/csharp/ 补全 `ListContainersAsync` / `ListImagesAsync` / `StartAsync` / `StopAsync` 的真实实现，让 MainWindow 的"刷新列表 / 启停"真正可用。
2. 把 `MainViewModel` 的 `Containers` / `Images` 集合接到真实数据，验证 MVVM 绑定链路。
3. 评估 stats / network / volume 是否需要 CLI 兜底适配器（实现同一 `IWslcClient`）。
4. 交互式终端（最重）留到最后。
