# wslcUI

Windows 原生 UI 的 **WSL 容器 (wslc)** 管理工具。以 `Microsoft.WSL.Containers`
C# SDK 为主驱动 wslc,SDK 无投影的能力(容器列举/启停/删除/日志/inspect、网络与卷
整套 CRUD、stats、镜像构建、交互式终端)统一桥接 `wslc` CLI —— 见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。

## 定位

> **wslcUI 是 `wslc` 的原生 Windows GUI —— 补上 Docker Desktop 里 Docker 帮不上忙的那一层。**

`wslc`（WSL Containers，3.0.1 GA）已经把 Docker Desktop 的**下面三层收进了操作系统**：
独立 VM（每会话一个，含独立 VHD）、`dockerd` + `containerd` 引擎、`wslc.exe` / `container.exe` CLI。
**唯一没有人替它做的是最上面的 Dashboard 层** —— 这正是 wslcUI 存在的理由：

| 层 | Docker Desktop | Windows 现状 |
|----|----------------|--------------|
| Dashboard（GUI） | Docker Desktop | **wslcUI ← 本仓库** |
| Compose / K8s / 扩展 | Docker Desktop | ⚠️ 缺口（wslc 本身没有 `compose`） |
| CLI | `docker` | `wslc.exe` / `container.exe`（wslc 提供） |
| 引擎 | `dockerd` | `dockerd` + `containerd`（wslc 提供，VM 内） |
| VM | Docker Desktop 自带 | 每会话独立 VM + VHD（wslc 提供） |

**边界说明（不吹）**：wslcUI 提供的是 **Docker Desktop Dashboard 那一层的等价物**，
不是 Docker Desktop 的对等替代 —— 后者还打包了 Compose 多容器编排、单节点 Kubernetes、
Extensions 市场、Scout 漏洞扫描与镜像仓库集成，这些 wslc 侧没有，GUI 也变不出来。
wslcUI 的差异化在于**走 SDK + CLI 双通道**（对 SDK/CLI 命名空间分裂有针对性处理）、
**真 TTY 终端**（ConPTY + XTerm.NET cell 渲染，非日志尾巴）、**容器内文件浏览**、
**实时 CPU / 内存曲线**、**挂载关联双向视图**、**端点面板**、**容器事件实时流**，
以及**占用统计 + 一键清理**。

### 对标社区同行的取舍（2026-10-02 核实）

wslc 3.0.1 GA 公告点名的社区项目里有两个同构 GUI 客户端。逐条核实后的结论：

| 能力 | wslcUI | 备注 |
|------|--------|------|
| 容器/镜像/网络/卷生命周期 | ✅ | 已追平 |
| 真 TTY 终端（ConPTY + XTerm.NET + 真键盘转发） | ✅ **独有** | 社区两款都没有 |
| 容器内文件浏览 | ✅ **独有** | 同上 |
| 挂载关联双向视图 | ✅ **独有** | 同上 |
| 端点面板 | ✅ | 社区竞品有对标功能 |
| 容器事件实时流 | ✅ | 竞品用 `wslc events` 做了活动流 |
| 批量操作（Select 模式） | ✅ | — |
| 镜像出向（push / tag / save / load / import） | ✅ | 竞品亦具备 |
| 容器仓库登录 | ✅ | `--password-stdin`，不落盘 |
| `container export` / `kill`、`network connect`/`disconnect` | ✅ | — |
| **Compose 编排** | ❌ **不做** | wslc 自身无 `compose`（官方称「呼声最高、无时间表」）。竞品是**自己写编排层**（读 yaml → 依赖排序 → 健康门控），属另写一个产品 |
| **k3s 单节点集群** | ❌ **不做** | wslc 无任何编排/K8s 命令，结构性缺口 |
| **WSL 引擎控制**（重启会话/装更新/改存储位置） | ❌ **不做** | wslc 只有 `system session {enter,list,run,shell,terminate}` + `settings`（打开 yaml），无「重启引擎/检查更新」命令；硬做等于自己拼 `wsl.exe` |
| **Intune 企业策略感知** | ❌ **不做** | 需企业环境才有意义，个人开发机永远走不到该分支 |
| **AI 助手 / 诊断** | ❌ **不做** | 与「纯本地、无账号无 API Key」的定位冲突 |
| 镜像更新徽章（digest 比对 registry） | ❌ | 需要访问外部 registry，与定位冲突 |

## 技术栈

| 层 | 选型 |
|----|------|
| UI 框架 | WinUI 3 (Windows App SDK 2.4) · Fluent Design |
| 语言 / 运行时 | C# · .NET 10 (net10.0-windows10.0.26100) |
| 模式 | MVVM — CommunityToolkit.Mvvm (源生成器) |
| 依赖注入 | Microsoft.Extensions.DependencyInjection |
| 容器后端 | `Microsoft.WSL.Containers` 3.0.1 (wslc SDK, **GA**) + `wslc` CLI 桥接 |

## 下载即用（预编译包）

到 [Releases](https://github.com/lengyuqu/wslcUI/releases) 下载 `wslcUI-v1.0.0-win-x64.zip`，
解压到任意目录后双击 `wslcUI.exe` —— **不需要安装 .NET 运行时，也不需要安装 Windows App SDK 运行时**
（自包含发布：.NET 10 与 Windows App SDK 2.4 都随包分发；解压后约 235 MB，zip 约 88 MB）。

唯一前置：**WSL2 + `wslc` ≥ 2.9.9**（推荐 3.0.1 GA），见下方「环境前置」第 3 条。

自己构建自包含包：

```bash
dotnet publish src/wslcUI/wslcUI.csproj -p:Platform=x64 -c Release -r win-x64 \
  --self-contained true -p:WindowsAppSDKSelfContained=true -o bin/publish/win-x64
```

⚠️ `dotnet publish` 对 WinUI 3 有已知遗漏：**不产出** `App.xbf` / `MainWindow.xbf` /
`TerminalWindow.xbf` / `ContainerFilesWindow.xbf` / `wslcUI.pri`，需从
`src/wslcUI/bin/x64/Release/net10.0-windows10.0.26100.0/win-x64/` 补齐后才能运行；
且自包含部署下**不能**调用 Windows App SDK bootstrapper（详见 [AGENTS.md](AGENTS.md) 第 3 节——
两者都会导致启动即崩）。

## 环境前置

1. **Visual Studio 2026 (18.x) / 2022 17.6+**，勾选工作负载 *使用 C# 的桌面开发* 与
   *Windows App SDK* (WinUI 3)。
2. **.NET 10 SDK**。
3. **WSL2 + wslc 3.0.1**（GA；≥ 2.9.9 均可，2.9.11+ 与 3.0.1 表头均已兼容）：
   ```powershell
   wsl --install --no-distribution
   wsl --update
   & "C:\Program Files\WSL\wslc.exe" --version   # 期望 3.0.1.0
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

## 已知缺口

- `WslcSdkClient` 中**容器列举 / 按名启停 / 删除 / 日志 / inspect**、**网络 / 卷整套 CRUD**、**资源监控 stats**、**镜像构建**、**交互式终端**
  均桥接 `wslc` CLI（`WslcCli.cs` / `Services/ConPty.cs`），因为 wslc 3.0.1 的 C# 投影仍然没有 `Session.GetContainers()` /
  `Session.GetContainer(name)`，且 network/volume 资源类型完全无投影、也没有 stats / 镜像构建 / inspect 端点。其余 SDK 覆盖的操作（镜像拉取 /
  运行）继续走 SDK；**镜像列举是 SDK 与 CLI 两个命名空间的合并去重**——二者互不可见，只取其一都会漏。
- 资源监控（`wslc stats` + `WslcCli.ParseStats`，表格解析）已实现，列表为单次快照、「刷新统计」按钮独立触发。wslc 3.0.1 仍不接受 docker 的 `--no-stream` 标志——默认就是一次性快照。
  ⚠️ **必须带 `-a`**（2026-10-02 实测修正）：`wslc stats` **不带参数时只返回一个容器**，统计页此前因此永远只显示 1 行 —— 存量 bug，已改为 `stats -a`（返回全部容器，含已停止的，其值为真实的 0）。
- **统计页实时曲线（2026-10-02 新增）**：wslc 的 stats 不流式，所以「开始采样」按钮每 3 秒轮询一次 `wslc stats -a --format json` 并自己攒历史（每容器最多 60 点）。详情面板按选中容器画 **CPU / 内存两条独立曲线**（刻意不叠在一张图里 —— 各自按本批最大值缩放，叠起来会诱使人跨曲线比高度）。曲线逻辑是纯函数 `Services/SparklineGeometry.cs`（不依赖任何 UI 类型，可单测）。离开统计页自动停采样。
- **活动流页（2026-10-02 新增）**：`wslc events` 实时流。事件行格式
  `<ISO8601纳秒> <类别> <动作> <ID> (key=value, …)`，解析器
  `Services/EventLineParser.cs`（纯函数）。实测动作集：`container create|start|kill|stop|destroy`、
  `network connect|disconnect`。`value` 内可含逗号与尖括号（`maintainer=NGINX Docker Maintainers <…>`），
  故按「合法 `key=` 前缀」定位边界而非简单 split 逗号。
  ⚠️ **两个实测坑**：
  ① **事件流永不退出** —— 即使带 `--since` 也继续挂在 stdout 等新事件
  （`timeout` 杀它得到退出码 124），所以**绝不能 `ReadToEndAsync`**；
  历史回读用 `ListEventsAsync` 的「逐行异步读 + 空闲 400ms 即止」；
  页面离开时 `Stop()`，否则留下常驻 `wslc.exe`。
  ② `timeout N wslc events > file` **读到 0 行**（管道缓冲），但同一命令用
  `OutputDataReceived` 逐行读立刻拿到数据 —— **不是 wslc 缺陷**，别据此误判。
  纳秒时间戳超出 `DateTimeOffset` 分辨率：低 2 位另存 `SubTickNanoseconds`
  参与 `SortKey` 全序比较，否则同一 100ns 内的事件排序不稳定。
- **端点面板（2026-10-02 新增）**：聚合全部**运行中**容器已发布到宿主的端口，
  一键复制 `ip:port` / 打开浏览器。数据来自容器行的 Ports 列，经
  `Services/EndpointParser.cs` 纯函数解析（不额外调 CLI）。真实列形态
  `127.0.0.1:18096->80/tcp, 127.0.0.1:18095->443/tcp`；绑定 `0.0.0.0` 的端点
  会给出「局域网可访问」警告。
  打开外部程序走 `Process.Start` + `UseShellExecute=true` 而非 WinRT
  `Launcher.LaunchUri` —— 本项目是 **unpackaged**（无包身份），后者默认处理器解析不可靠。
- **批量操作 Select 模式（2026-10-02 新增）**：容器页「多选」进入多选态，
  批量启动/停止/删除。`SelectionMode` 在 `SelectionChanged` 里切（单选时与原行为一致），
  选中项名字经 `SetBulkSelection` 汇成唯一事实源；批量动作**逐个容错**——
  单个失败不中断其余，结束后汇总成功/失败计数。
- **镜像出向与容器强杀（2026-10-02 新增）**：`image push [-a] / tag / save / load / import`、
  `registry login --password-stdin / logout`、`container export / kill`、
  `network connect / disconnect`、`system info` 全部桥接。
  ⚠️ 与 docker 的参数形态有实质差异（已逐条 `--help` 核实）：
  `push` 只有**一个**位置参数（无「源+目标」双参形态）；`save` 的 `<image>...` 必填且
  **没有「导出全部」**；`load` 用 `-i`；`import` 的 tar 是**必填位置参数**且**没有 `-o`**。
  **登录必须走 `--password-stdin`** —— `-p` 会把密码暴露在进程命令行上；
  密码只经管道进子进程，不落盘、不进日志、不进异常消息。
- **容器内文件浏览（2026-10-02 新增）**：独立窗口 `ContainerFilesWindow`（对标 Docker Desktop 的 Container File Explorer 的**子集**：浏览 / 上传 / 下载 / 删除，不含在线编辑）。列目录走 `wslc exec <name> ls -la <path>`，搬文件走 `wslc container cp`。仅对**运行中**容器可用（`exec` 需要容器在跑）。
  ⚠️ **wslc 的 `container cp` 与 docker 语义不同**（实测 2026-10-02）：上传目标**必须是容器内已存在的目录**且**不能指定目标文件名**；`cp file ctr:/tmp/new.txt` 会报 `ERROR_PATH_NOT_FOUND`，`cp - ctr:/tmp/new.txt`（stdin 形态）同理。因此 UI 只提供「上传到当前目录」，文件名沿用本地文件名。
- 镜像构建（`wslc build -t <tag> <context>`，`WslcCli.BuildImageAsync`，逐行流式回传日志）已实现，「构建」页含上下文目录选择 + 标签 + 滚动日志。
- 容器挂载关联（`wslc inspect <name> --format json` → `Mounts[]`，`WslcCli.InspectContainerAsync`）已实现：容器列表「卷」列 + 详情面板「挂载」区块 + 卷页的反向被引清单；选中容器时按需异步拉取而非并入主刷新（避免 N+1），并有独立取消源防快速切换选中导致的陈旧回填。
- **维护页（磁盘占用 + 一键清理，2026-10-02 新增）**已实现：4 张占用卡片 + 5 条清理命令（已停止容器 / 悬空镜像 / 全部未用镜像 / 未使用网络 / 未使用卷）+ CLI 原始输出区，破坏性操作全部走确认对话框。
  ⚠️ **占用合计只覆盖镜像** —— wslc **没有** `system df` 之类的总占用命令；`wslc volume list --format json` 的 `Size` 恒为 `"N/A"`；容器大小只以 `0B (虚拟 451MB)` 这种混合形态出现在 `list -a --size`（解析脆弱，刻意不采）。因此容器/卷只显示**数量**而非体积；若某个镜像 SIZE 解析失败，合计前缀加 `≥` 明示是下界。
- 交互式终端已实现且**已真机联调**：`wslc exec -it <name> /bin/sh`（容器内起新进程）与 `wslc attach <name>`（附加到运行中容器的现有前台进程），经 Windows Pseudoconsole（`Services/ConPty.cs`）给容器真 TTY；渲染走 **XTerm.NET（VT 解析 + cell 缓冲）+ 自研 WinUI 渲染层**（`Terminal/TerminalView.cs`，256 色 / 真彩色 / bold / inverse / CJK 双宽），选型见 [docs/TERMINAL-RENDER-DECISION.md](docs/TERMINAL-RENDER-DECISION.md)。输入侧为**真键盘转发**（`Terminal/TerminalInputMapper.cs`：KeyDown / 字符 / 粘贴直达 PTY stdin，Ctrl/Alt 组合键自合成），非 InputBox 整行发送。
  ⚠️ 开发机（Win11 26200.9278 insider）存在**机器级 ConPTY 故障**：任何 PTY 子进程 `0xC0000142` 启动即死（三重实验定责系统，与 wslcUI 无关）。开窗前的 `PseudoConsole.ProbeHealth()` 预检会拦下并弹诊断，不会开出一个黑屏死窗口。
- SDK 与 wslc 已于 2026-09-29 同期 **GA（3.0.1）**，包版本锁在 `3.0.1` 与 runtime 对齐。
  3.0.1 SDK 相对 2.9.9 的 winmd 差异为**纯增量、无删除**（`IProcessSettings.EnableStandardInput`、`ErrorCode.ContainerDeleted`），
  本项目未引用二者，升包源兼容；**仍无 `compose` 命令**（官方列为最想要的待办，无时间表）。

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
│   ├── MainWindow.xaml(.cs)      # 主窗口：NavigationView 三栏（侧栏 220 + 内容 + 详情面板 340 + 日志抽屉 + 状态栏；9 page：容器/镜像/网络/卷/统计/构建/维护/端点/活动，x:Bind）
│   ├── TerminalWindow.xaml(.cs)  # 交互式终端窗口（exec / attach）
│   ├── ContainerFilesWindow.xaml(.cs) # 容器内文件浏览窗口（浏览/上传/下载/删除）
│   ├── Program.cs                # WinUI 3 入口（Windows App SDK 2.4 bootstrap）
│   ├── app.manifest
│   ├── Models/                   # ContainerInfo (+Ports/CreatedAt/stats/StatusKind/Mounts) / ContainerMount / ContainerFileEntry / ImageInfo (+Digest/Reference) / NetworkInfo / VolumeInfo / StatInfo / EndpointInfo / ContainerEvent / SystemInfo
│   ├── Converters/
│   ├── Services/
│   │   ├── IWslcClient.cs        # 后端抽象（适配器接口）
│   │   ├── WslcSdkClient.cs      # 真实后端：SDK 覆盖的操作 + 转发 WslcCli
│   │   ├── WslcCli.cs            # CLI 桥接 + 表格/JSON 解析器 + 错误码中文映射
│   │   ├── EventStreamService.cs  # `wslc events` 长驻流（异步逐行读 + 优雅停止）
│   │   ├── EventLineParser.cs    # 事件行 → ContainerEvent（纯函数，含纳秒时间戳与全序 SortKey）
│   │   ├── EndpointParser.cs     # 端口列 → 端点行（纯函数）
│   │   ├── SizeParser.cs         # 镜像 SIZE 字符串 ⇄ 字节数（纯函数，维护页占用合计用）
│   │   ├── SparklineGeometry.cs  # 采样值 → 折线坐标（纯函数，不依赖 UI 类型）
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
