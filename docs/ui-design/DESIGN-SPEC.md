# wslcUI 界面设计规格 v2

> 配套可交互原型：`docs/ui-design/prototype.html`（浏览器直接打开）。
> 适用范围：`src/wslcUI/MainWindow.xaml` 的整体重构，ViewModel 与后端接口保持不变的扩展。

---

## 1. 设计目标

| 目标 | 判定标准 |
|------|---------|
| 一眼看清全局状态 | 容器运行状态无需读文字，靠色点扫视；异常项自动置顶 |
| 选中即可操作 | 任何资源选中后，右侧详情面板给出该对象的全部可执行动作 |
| 危险操作不可逆点 | 删除类动作脱离命令栏主区，进入详情面板底部并强制二次确认 |
| 空状态也是引导 | 每个空列表给出下一步动作按钮，而不是一句"暂无数据" |
| 后端慢也不假死 | 所有异步操作必须同时具备：进度指示、可取消、失败可重试 |

---

## 2. 信息架构

```
wslcUI 窗口
├── 标题栏（品牌 / 状态演示开关 / 主题切换）
├── 主体 三栏
│   ├── 左：导航（容器 · 镜像 · 网络 · 卷 ── 统计 · 构建）
│   ├── 中：命令栏 + 资源列表（或构建终端）
│   └── 右：详情面板（属性 + 操作）
├── 日志抽屉（默认收起，按需展开，高度 230）
└── 状态栏（后端状态 / 计数 / 版本）
```

**分组原则**：容器 / 镜像 / 网络 / 卷 是资源对象，归为一组；统计 / 构建 是工具，用分隔线隔开。
容器为默认首屏，因为它是用户 90% 的操作对象。

---

## 3. 布局与栅格

| 区域 | 尺寸 | 说明 |
|------|------|------|
| 标题栏 | 高 40 | 自定义标题栏预留拖拽区 |
| 导航 | 宽 220（可折叠至 56） | 折叠后仅图标 + Tooltip |
| 内容区 | `1fr`，最小宽 0 | `minmax(0,1fr)` 防止内容撑破 |
| 详情面板 | 宽 340（可隐藏至 0） | 对象未选中时显示引导文案，不隐藏 |
| 日志抽屉 | 收起 36 / 展开 230 | 高度过渡 180ms |
| 状态栏 | 高 28 | — |

栅格：4pt 基准，使用 4 / 8 / 12 / 16 / 20 / 24。
内容区内边距 14；卡片内边距 12–14。

---

## 4. 设计令牌

### 4.1 颜色（深色主题）

| 令牌 | 值 | 用途 |
|------|-----|------|
| `bg-app` | `#1F1F1F` | 窗口底 |
| `bg-card` | `#2B2B2B` | 卡片、输入框 |
| `bg-elev` | `#333333` | 导航、详情面板（抬升一层） |
| `bg-hover` | `#FFFFFF0D` | 行悬停 |
| `bg-select` | `#FFFFFF14` | 行选中 |
| `stroke` | `#3D3D3D` | 分隔线、表格线 |
| `stroke-strong` | `#525252` | 输入框、次按钮边框 |
| `fg` | `#F3F3F3` | 主文本 |
| `fg-2` | `#C2C2C2` | 次文本 |
| `fg-3` | `#8A8A8A` | 表头、提示、时间戳 |
| `accent` | `#4CC2FF` | 主按钮底、选中指示、进度 |

### 4.2 语义色（状态徽章）

| 状态 | 底 | 边 | 字 | 圆点 |
|------|-----|-----|-----|------|
| 运行中 | `#173528` | `#2F6B48` | `#6CCB8F` | 同字色 |
| 已停止 | `#333333` | `#4D4D4D` | `#A6A6A6` | 同字色 |
| 拉取中 / 进行中 | `#123246` | `#2C6D94` | `#7FC7FF` | 同字色 |
| 构建中 | `#3A2F16` | `#7A6029` | `#F0C674` | 同字色 |
| 异常退出 | `#40212A` | `#8A3140` | `#F1707D` | 同字色 |

> 规则：徽章 = 6px 圆点 + 11.5px 文字，全圆角胶囊，左右内边距 9px。
> 语义色**只用于状态**，不可用于装饰。

### 4.3 亮色主题覆盖

| 令牌 | 值 |
|------|-----|
| `bg-app` / `bg-card` / `bg-elev` | `#F3F3F3` / `#FFFFFF` / `#FAFAFA` |
| `stroke` / `stroke-strong` | `#E2E2E2` / `#C8C8C8` |
| `fg` / `fg-2` / `fg-3` | `#1B1B1B` / `#4A4A4A` / `#767676` |
| `accent` | `#0078D4` |

实现：整份令牌走 CSS 变量 / WinUI `ThemeResource`，切换 `ElementTheme` 即生效，不在代码里写 `if (dark)`。

### 4.4 字体与圆角

| 用途 | 字号 / 行高 | 字重 |
|------|------------|------|
| 页面标题 | 15 / 20 | Semibold |
| 正文、表格 | 13 / 18 | Regular |
| 表头、提示 | 11.5 / 16 | Semibold |
| 等宽（端口 / 摘要 / 日志 / 统计） | 12 / 20 | Regular |

字体族：`Segoe UI Variable Text` → `Segoe UI` → `Microsoft YaHei UI`。
等宽：`Cascadia Mono` → `Consolas`。

圆角：控件 4 · 卡片 6–8 · 窗口 12 · 徽章 999。

---

## 5. 组件规范

### 5.1 数据表

- 表头 sticky，点击切换排序（首升、再降），当前列显示 `▲/▼` 且用 accent 着色。
- 行高 38，悬停 `bg-hover`，选中 `bg-select` + 左侧 2px accent 竖条。
- 行内操作按钮默认 `opacity:0`，悬停或选中时显现（避免每行常驻按钮造成的视觉噪声）。
- CPU / 内存百分比列用 52×4 内联进度条，>50% 转红（>80% 内存转红）。
- 名称列用 `Strong`，端口 / 摘要 / 挂载点 / 子网用等宽 + `fg-3`。

### 5.2 命令栏

排列顺序：**主操作（最右 / accent）← 次操作 ← 对象操作 ← 间隔 → 搜索（最左）← 页面标题**。
对象操作（启动 / 停止 / 删除）在无选中项时 `IsEnabled=false`，不隐藏——避免布局跳动。

### 5.3 空状态

结构：`40px 线稿图标` → `标题（fg-2, Semibold）` → `一句说明（≤28 字）` → `主按钮 CTA`。
CTA 必须是真实可执行动作，例如"去拉取镜像"直接跳到镜像页。

### 5.4 加载与错误

- 加载：骨架屏（6 行，1.2s 循环渐变），**不用全屏遮罩**——保留导航可用。
- 错误：`InfoBar`（顶栏下方）+ 内容区错误态（图标 + 原因 + 重试按钮）。
- 错误文案必须包含**可执行的下一步**，并附上原始命令与退出码，例如：
  > 命令 `wslc list -a` 退出码 1：WSL 组件未就绪，或该发行版未运行。

### 5.5 确认对话框

删除一律走 `ContentDialog`：标题 `删除<资源类型>`，正文含对象名与"此操作不可撤销"。
默认焦点落在**取消**上，删除按钮用 danger 样式。

### 5.6 日志抽屉

- 默认收起为 36px 条，展开 230px，高度过渡 180ms。
- 展开时载入当前选中对象的日志；未选中时提示"选中对象后按 L 载入"。
- 明确标注 wslc `logs` 不支持 `-f` 跟随，实时输出请打开终端。

---

## 6. 页面规格

| 页面 | 列 | 主操作 | 对象操作 | 空状态 CTA |
|------|-----|--------|---------|-----------|
| 容器 | 名称 / 镜像 / 状态 / 端口 / CPU / 创建 / 行内 | 运行容器 | 启动 · 停止 · 日志 · 终端 | 去拉取镜像 |
| 镜像 | 仓库 / 标签 / 大小 / 在用 / 拉取时间 / 行内 | 拉取镜像 | 运行 · 删除 | 拉取 alpine:latest |
| 网络 | 名称 / 驱动 / 作用域 / 子网 / 接入容器 / 行内 | 新建网络 | 删除 | 新建网络 |
| 卷 | 名称 / 驱动 / 占用 / 挂载点 / 使用者 / 行内 | 新建卷 | 删除 | 新建卷 |
| 统计 | 容器 / CPU% / 内存 / 内存% / 网络 I/O / 块 I/O / PID | 刷新快照 | — | 刷新快照 |
| 构建 | 上下文输入 + 标签输入 + 终端输出 | 开始构建 | — | — |

统计页顶部增加 4 个指标卡：运行中容器数、CPU 均值、内存占用合计、镜像总数。

---

## 7. 交互规范

| 交互 | 行为 |
|------|------|
| 单击行 | 选中 → 详情面板更新 → 日志抽屉目标切换 |
| 双击行（容器） | 直接打开 ConPTY 终端 |
| 右键行 | `MenuFlyout`：启动 / 停止 / 重启 / 日志 / 终端 / ── / 删除 |
| `/` | 聚焦搜索框 |
| `L` | 展开 / 收起日志抽屉 |
| `R` | 刷新当前列表 |
| `Esc` | 关闭对话框 / 收起抽屉 |
| 删除 | 强制 `ContentDialog` 二次确认 |

---

## 8. 无障碍

- 所有图标按钮必须有 `AutomationProperties.Name` 与 Tooltip。
- 状态**不得只靠颜色**传达：徽章同时含文字标签。
- 键盘可达：导航、列表、命令栏、详情面板均可通过 Tab 遍历，焦点环可见（2px accent）。
- 对比度：正文 ≥ 4.5:1，表头与提示 ≥ 3:1（当前令牌已满足）。
- 尊重 `prefers-reduced-motion`：骨架屏渐变与抽屉过渡自动降级。

---

## 9. 与现有代码的落地映射

### 9.1 可直接复用

| 现有成员 | 新界面位置 |
|---------|-----------|
| `Containers/Images/Networks/Volumes/Stats` | 各页列表源 |
| `SelectedContainer/Image/Network/Volume` | 详情面板数据源 |
| `RefreshCommand/StartCommand/StopCommand` | 命令栏与详情面板 |
| `DeleteContainerCommand/DeleteImageCommand` | 详情面板底部 danger 区 |
| `PullCommand/BuildImageCommand` | 镜像页 / 构建页主操作 |
| `ShowLogsCommand` | 日志抽屉 |
| `PickBuildContextCommand` | 构建页「浏览」 |
| `IDialogService.ConfirmAsync` | 删除确认 |
| `TerminalWindow` + `ConPty` | 双击行 / 终端按钮 |
| `IsBusy` / `Status` / `InfoBar` 三件套 | 加载态 / 状态栏 / 错误态 |

### 9.2 需新增的 ViewModel 成员

| 新增 | 类型 | 用途 |
|------|------|------|
| `CurrentPage` | enum | 替换 Pivot 索引，驱动导航选中态 |
| `SearchText` | string | 六页共用（`OnChanged` 触发过滤） |
| `SortKey` / `SortDescending` | string / bool | 表头排序 |
| `IsLogPaneOpen` | bool | 日志抽屉展开状态 |
| `IsDetailOpen` | bool | 详情面板折叠 |
| `Theme` | `ElementTheme` | 主题切换并持久化 |
| `Filtered*` | `ObservableCollection<T>` | 过滤 + 排序后的视图，列表绑定它而非原始集合 |

### 9.3 需扩展的数据模型（依赖 CLI 输出）

| 模型 | 现有字段 | 需补 | 来源 |
|------|---------|------|------|
| `ContainerInfo` | Id/Name/Image/Status | `Ports`、`CreatedAt` | `wslc list -a` 表头已有「端口」「已创建」列，`ParseContainerList` 加两个 `FindColumn` 即可 |
| `ContainerInfo` | — | `Cpu/Mem/MemPercent/NetIo/Pids` | 由 `StatInfo` 按 `Name` 关联（stats 表含「名称」列） |
| `ImageInfo` | Id/Repo/Tag/Size | `Digest` | SDK `GetImages()` 已有 `Sha256` |
| `NetworkInfo` | Name/Driver/Scope | `Subnet`、` AttachedCount` | `wslc network ls` 无此列 → 需 `network inspect`；**若无该子命令，字段显示「—」并保留占位** |
| `VolumeInfo` | Name/Driver/Mountpoint | `Size`、`UsedBy` | `wslc volume ls` 无 size → 同上，缺失降级为「—」 |

> 降级原则：拿不到的字段一律显示 `—`，并在详情面板用 `fg-3` 呈现，**禁止用假数据填充**。

---

## 10. 分阶段实施

| 阶段 | 内容 | 依赖 |
|------|------|------|
| **P0** | `NavigationView` 三栏骨架；容器页改造（列 / 徽章 / 行内操作 / 选中态）；容器详情面板；`CurrentPage` 与 `IsDetailOpen` | 无 |
| **P1** | 搜索 + 排序 + 空状态 CTA；镜像 / 网络 / 卷 / 统计 / 构建 五页统一到同一布局模板；日志抽屉替换底部固定面板 | P0 |
| **P2** | 模型扩展（`Ports`/`CreatedAt`/`Digest`/`Subnet`/`AttachedCount`/`Size`）；`WslcCli` 解析补齐；stats 与 containers 关联；指标卡 | P1 |
| **P3** | 键盘加速键、`MenuFlyout` 右键菜单、骨架屏、主题持久化、自动刷新开关、Mica 背景 | P2 |

---

## 11. 风险与约束

| 风险 | 影响 | 对策 |
|------|------|------|
| WinUI 3 **无内置 DataGrid** | 表头排序 + 列对齐需自研 | 方案 A：`CommunityToolkit.WinUI.Controls.DataGrid`（Toolkit 8.x 提供 WinUI 3 版）；方案 B：`ListView` + `DataTemplate` 内 `Grid`，列宽通过共享 `GridLength` 资源保证表头与行对齐。**推荐 B**，避免引入重包与 WinAppSDK 版本耦合 |
| `wslc` 无 `network inspect` / `volume inspect` | 网络子网、卷占用拿不到 | 字段降级为「—」；若后续 CLI 支持再补解析 |
| `wslc stats` 为一次性快照且较慢 | 主刷新被拖慢 | 维持现状：独立 `RefreshStatsCommand`，不并入 `RefreshCommand`；可选自动轮询默认关闭 |
| stats「名称」与 containers「名称」不完全一致 | 详情面板资源区空白 | 关联失败时详情面板资源区整块隐藏，不显示 0 值 |
| `Pivot` → `NavigationView` 后旧 `SelectedIndex` 行为变化 | 首屏落在最后一页 | `NavigationView.SelectedItem` 显式设为容器项，不要依赖默认 |
| 主题切换与 WinAppSDK 2.x back-compat | 可能回落到 1.6 视觉 | 仅切 `ElementTheme`，不动 `Bootstrap.Initialize` 版本参数 |

---

## 12. 验收清单

- [ ] 六个页面布局一致，切换无闪烁、无空白帧
- [ ] 每个列表均有空状态 + 加载态 + 错误态三种可见状态
- [ ] 所有删除操作均有二次确认，且默认焦点在取消
- [ ] 容器状态可仅凭颜色点辨识
- [ ] 键盘可完成：切换页面 → 选中容器 → 打开终端 → 删除（含确认）
- [ ] 深色 / 亮色主题下正文对比度均 ≥ 4.5:1
- [ ] 后端不可用时，界面仍可导航，错误信息含原始命令与退出码
