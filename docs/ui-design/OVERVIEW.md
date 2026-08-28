# 交付概览 — wslcUI 界面设计方案 v2

## 交付物

| 文件 | 说明 |
|------|------|
| `docs/ui-design/prototype.html` | 高保真可交互原型。单文件、零依赖，浏览器直接打开。可切换六个页面、选中查看详情、展开日志抽屉、切换深浅主题，右上角可切「正常 / 加载 / 空 / 错误」四种状态 |
| `docs/ui-design/DESIGN-SPEC.md` | 设计规格：信息架构、设计令牌、组件规范、交互规范、无障碍、代码落地映射、分阶段实施计划 |

## 关键决策

1. **Pivot 平铺 → `NavigationView` 三栏**。六类资源降为导航项，容器为默认首屏；统计 / 构建用分隔线归为工具组。
2. **新增详情面板**。选中对象即显示全部属性与可执行动作，解决"选中后无处可看、无处可点"。
3. **危险操作下沉**。删除从命令栏主区移入详情面板底部 + 强制二次确认，行内操作默认隐藏、悬停显现。
4. **状态色点化**。运行 / 停止 / 拉取中 / 构建中 / 异常各占一套语义色，徽章同时带文字，颜色不是唯一线索。
5. **日志改抽屉**。底部固定 160px 面板 → 默认 36px 可展开抽屉，空时零占位。
6. **空状态带 CTA**。每个空列表给出真实可执行动作，而不只是说明文字。

## 落地要点（详见 DESIGN-SPEC 第 9–11 节）

- ViewModel 需新增：`CurrentPage`、`SearchText`、`SortKey/SortDescending`、`IsLogPaneOpen`、`IsDetailOpen`、`Theme`、`Filtered*` 集合。
- 数据模型需扩展：`ContainerInfo.Ports/CreatedAt`（`ParseContainerList` 加两行 `FindColumn` 即可）、`ImageInfo.Digest`（SDK 已有 `Sha256`）；网络子网与卷占用依赖 CLI `inspect` 子命令，缺失时降级显示 `—`，**不用假数据**。
- WinUI 3 无内置 DataGrid，推荐 `ListView` + `DataTemplate` 内 `Grid` + 共享 `GridLength` 资源，避免引入重包。
- 实施分 P0–P3 四阶段，P0 只做骨架与容器页，可独立验收。

## 未完成 / 待确认

- 网络「子网」「接入容器数」、卷「占用」「使用者」依赖 `wslc network inspect` / `volume inspect`，需确认 wslc 2.9.9 是否提供；若不提供则保持 `—` 占位。
- 原型为视觉与交互验证件，不含真实数据绑定；转为 WinUI 时需按第 4 节令牌建立 `ThemeResource` 字典。
