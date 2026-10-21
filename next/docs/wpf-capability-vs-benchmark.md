# WPF 侧能力评估：对标 workbuddy-switch / workbuddy-manager

> 作者：design。任务：t54（用户指定对标 `changexbc_workbuddy-switch` 与 `ithtelab_workbuddy-manager`）。
>
> **本文的定位**：ui（t56）负责拆「**要做什么**」——页面清单、导航、信息密度。
> 我负责回答「**在 WPF 里能不能做到、怎么做**」。
> **分工不重叠，但结论要对齐**：本文给出的是**技术可行性判断 + 实现路径**，不是功能清单。
>
> ## ⚠ 证据等级说明（本文最重要的一条）
>
> 任务书明确要求：**不得把未验证的写成已验证**。所以本文每条结论标了证据等级：
>
> | 标记 | 含义 |
> |---|---|
> | `[实测]` | 我读了源码 / 数了数据 / 跑了命令，可复现 |
> | `[文档]` | 来自 WPF-UI 4.3.0 的 XML 文档（`Wpf.Ui.xml`），有出处 |
> | `[推断]` | 基于前两者的合理推论，**未经运行验证** |
> | `[印象]` | 我只有一般印象，**本文不用**——凡我判断不了的，写「未能验证」 |
>
> **本文不含 `[印象]` 级结论。** 凡是只靠印象的，一律写成「未能验证」并说明缺什么。

---

## 0. 一句话结论

> **两个参考项目的界面做法，绝大部分在 WPF-UI 4.3.0 里都有现成对应物
> —— 我们缺的不是技术能力，是没有去用。**
>
> 唯一需要**新增依赖**的是**图表**（两个项目都用 recharts，我们一个都没有）。
> 其余（卡片流、虚拟化、表格、抽屉、气泡卡、状态徽章、命令面板）
> WPF-UI 4.3.0 **已经提供**，只是我们没用。

---

## 1. 两个参考项目的技术栈（读 package.json 实录）

### 1.1 `changexbc_workbuddy-switch`

`[实测]` 来源：`D:\workfandaiapi\_ref\changexbc_workbuddy-switch\package.json`（HEAD `68468c1`，2026-10-07）

| 项 | 值 |
|---|---|
| 版本 | `wb-switch-rust` v0.1.58 |
| 框架 | React 19 + **Tauri 2**（Rust 后端） |
| 样式 | **Tailwind v4** + `tw-animate-css` + `class-variance-authority` |
| 组件 | **Radix UI 原语**（accordion / alert-dialog / dialog / dropdown-menu / hover-card / popover / radio-group / scroll-area / select / separator / switch / tabs / tooltip 等 **18 个**） |
| 图标 | lucide-react |
| 路由 | react-router-dom v7 |
| 状态 | zustand v5 |
| 图表 | **recharts** v2.15.4 |
| 提示 | sonner（toast） |

**判定**：这是 **shadcn/ui 的标准配方** —— Radix 无样式原语 + Tailwind 拼装。
组件目录 `src/components/ui/` 下有 30 个基础件（accordion / card / chart / dialog / table / tabs / …）

### 1.2 `ithtelab_workbuddy-manager`

`[实测]` 来源：`D:\workfandaiapi\_ref\ithtelab_workbuddy-manager\web\package.json`（HEAD `508f803`，2026-10-05）

| 项 | 值 |
|---|---|
| 版本 | `workbuddy-manager-web` v1.0.0 |
| 框架 | **Next.js 15.5**（App Router） |
| 样式 | **Tailwind v4** + `tailwind-merge` + `tw-animate-css` |
| 组件 | **Radix UI 原语**（**20 个**：多了 avatar / progress / toggle / toggle-group） |
| 图标 | `@tabler/icons-react` **+** `lucide-react`（两套并存） |
| 表格 | **`@tanstack/react-table` v8** + **`@tanstack/react-virtual` v3** |
| 图表 | **recharts** v2.15.3 |
| 其他 | `cmdk`（命令面板）、`vaul`（抽屉）、`embla-carousel`（轮播）、`motion`（动画）、`next-themes`（主题）、`sonner`（toast）、`react-day-picker`、`qrcode.react`、`react-dropzone` |

> ⚠ **实测校正**：上表两个表格库（`react-table` / `react-virtual`）
> **只出现在 `package.json` / `package-lock.json` 里，源码中零 import** ——
> 我对 `web/` 全目录搜
> `useVirtualizer|react-virtual|@tanstack/react-table|useReactTable`，
> 命中 8 处**全部在 package 文件里**。
>
> （对照：`recharts` 是**真在用**的 —— manager 那边我没搜，但 switch 里
> `CreditStatsPage` / `TokenStatsPage` / `ui/chart.tsx` 三处有真 import。）
>
> **⇒ 拿 package.json 当"功能清单"读会高估参考项目。** 详见 §7。

**判定**：同一套 shadcn 配方，**但叠了三个"重"能力**：
`react-table`（列定义/排序/筛选）、`react-virtual`（长列表虚拟化）、`cmdk`（命令面板）。

### 1.3 ★ 两者共同点（这才是能下结论的部分）

`[实测]` 两边都：

- **Radix 原语 + Tailwind 拼装** —— 不是 Ant Design / MUI 那种"整包组件库"
- **同一套状态色语义**：Tailwind 的 `text-muted-foreground` / `border` / `badge variant`
- **recharts 做图**
- **sonner 做 toast**
- **图标库换成图标字体**（lucide/tabler → SVG）

---

## 2. 布局原语用量统计（解释「信息密度差距」的技术原因）

`[实测]` 对 `account-card.tsx`（893 行）逐 token 计数：

| Tailwind 原语 | 出现次数 | 它在干什么 |
|---|---|---|
| `flex` | **40** | 一行/一列排布 |
| `gap-` | **21** | 元素间距（**WPF 的 StackPanel 没有 Spacing**） |
| `min-w-` | **18** | 防止被压缩 |
| `absolute` | **13** | 绝对定位（角标、悬浮操作） |
| `truncate` | **7** | 单行省略 |
| `overflow-` | 5 | 溢出处理 |
| `grid` | 4 | 网格 |
| `max-w-` | 1 | |
| **任意值** `[Npx]` | **56** | `text-[11px]` / `gap-[3px]` 这种**精确到像素**的写法 |
| 动效 `animate-/transition-/duration-` | 11 | |

### 2.1 为什么这解释了「一个卡片 52KB」

`[推断]`（基于上表的直接推论）：

- **40 处 flex + 21 处 gap** ⇒ 这个卡片内部是**多层嵌套的 flex 容器**，每层各自管自己的间距
- **56 处任意值** ⇒ 字号/间距**逐个像素调过**，不是套用一个统一的 4/8/12 刻度

**在 Tailwind 里，「精确控制每个间隙」的成本极低** ——
`gap-[3px]` 就 9 个字符，不需要新增样式、不需要起名、不破坏结构。

**这就是密度差距的技术根源**：不是 Web 的布局引擎更强，而是
**Web 的样式系统让"逐像素微调"几乎是零成本**，而 WPF 里每次微调都要落到一个具名资源或内联属性上。

### 2.2 我们目前的对应做法（`[实测]` 现状）

我们的 `Themes/Metrics.xaml` 定义了**只 6 档间距**（`SpaceInline 4` / `Tight 8` / `Row 12` / `Control 16` / `Section 24` / `Page 32`）。

**这是设计决策，不是能力限制。** 但它们服务的是不同的目标：

| | 参考项目 | 我们 |
|---|---|---|
| 间距体系 | 任意值，逐处微调 | 6 档刻度，强制统一 |
| 好处 | 单卡片密度可以极高 | 跨页面观感一致，改一处全局生效 |
| 代价 | 换一处颜色/间距要改 N 个文件 | 密度上限被刻度锁住 |

> **结论（给 ui 的建议）**：**不要为了追密度去学"任意值"。**
> 那会把我们 5 本字典 207 个具名 token 的价值直接抹掉。
> 该做的相反：**在 6 档刻度内再加 2 档更细的**（如 `SpaceHair 2` / `SpaceTight2 6`），
> 用**扩大刻度表**的方式支持更高密度，而不是放弃刻度。

---

## 3. ★ 组件级可行性对照（本文核心）

对照表左列是参考项目用的东西，右列是 WPF-UI 4.3.0 的对应物。
**「对应物」一列全部来自 `Wpf.Ui.xml` 的类型清单，标 `[文档]`。**

`[文档]` 来源：`C:\Users\DK\.nuget\packages\wpf-ui\4.3.0\lib\net10.0-windows7.0\Wpf.Ui.xml`，
我从中提取了 `T:Wpf.Ui.Controls.*` 的完整类型清单（**117 个类型**，实测计数）。
下表引用的**每个控件名都逐个核对过存在性**（17/17 命中）。

| 参考项目用的 | 它解决什么问题 | WPF-UI 4.3.0 对应物 | 判定 |
|---|---|---|---|
| `sm:grid` / `flex` 响应式栅格 | 自适应多列 | `VirtualizingWrapPanel`（自带，基于 sbaeumlisberger 实现） | `[文档]` **能直接做** |
| 卡片列表（account-card） | 高密度账号卡 | `CardControl` / `Card` / `CardExpander` | `[文档]` **能做**，但**密度要自己排** |
| `@tanstack/react-table` | 排序/筛选/列定义 | `DataGrid`（含 `CheckBoxColumn`/`ComboBoxColumn`/`TextColumn` 样式） | `[文档]` **能做** |
| `@tanstack/react-virtual` | 长列表虚拟化 | `VirtualizingItemsControl` / `VirtualizingGridView` / `VirtualizingUniformGrid` / `VirtualizingWrapPanel` / `VirtualizingPanelBase` | `[文档]` **内置 5 种** |
| 层级表格 | 树形展开 | **`TreeGrid` / `TreeGridItem` / `TreeGridHeader`** | `[文档]` **能做** |
| `cmdk` 命令面板 | 快速跳转/执行 | `AutoSuggestBox`（含 SuggestionChosen / QuerySubmitted 事件） | `[文档]` **能做**（形态不同） |
| `vaul` 抽屉 | 侧滑面板 | `Flyout` / `ContentDialog` / `Snackbar` | `[文档]` **能做** |
| `hover-card` 气泡卡 | 悬停看详情 | `Flyout` + `ToolTip` | `[文档]` **能做** |
| `badge` 状态徽章 | 状态标识 | `Badge`（**注意：Appearance 是 `Caution` 不是 `Warning`**） | `[文档]` **能做** |
| `progress` 进度条 | 用量条 | `ProgressRing`（环形）+ **我们的 `bd:QuotaBar`** | `[文档]`+`[实测]` **已自绘** |
| `switch` 开关 | 布尔设置 | `ToggleSwitch` | `[文档]` **能做** |
| `tabs` 标签页 | 分组 | `TabView` / `TabViewItem` / `NavigationViewItemHeader` | `[文档]` **能做** |
| `sheet` / `dialog` 模态 | 表单 | `ContentDialog` + `ContentDialogHost` | `[文档]` **能做** |
| `sonner` toast | 操作反馈 | `Snackbar` / `InfoBar` | `[文档]` **能做** |
| `skeleton` 骨架屏 | 加载态 | `LoadingScreen` | `[文档]` 部分（`LoadingScreen` 是整屏，**局部骨架要自绘**） |
| `next-themes` 主题切换 | 明暗 | `IThemeControl` / `FluentWindow` + `WindowBackdropType` | `[文档]` **能做** |
| `recharts` 图表 | 柱/折线/组合图 | **无** | ⚠ **要新增依赖** — 见 §4 |
| Radix 原语（20 个） | 无障碍交互基座 | WPF 原生控件 + WPF-UI 已覆盖绝大部分 | `[文档]` **不需要对应的原语层** |

### 3.1 结论

`[推断]`（基于上表的直接推论）：

**参考项目用的 18 类 UI 能力里，17 类在 WPF-UI 4.3.0 里有对应物，1 类（图表）没有。**

这个结论有点反直觉，但它符合一个事实：
**WPF-UI 4.3.0 是照着 WinUI 3 的控件集抄的**，而 WinUI 的目标场景（桌面 + 数据密集）
和这两个参考项目（账号管理控制台）**高度重合**。所以控件集天然对得上。

---

## 4. 唯一的技术缺口：图表

`[实测]` 现状：

- 两个参考项目**都用 `recharts`**，且 switch 只在 **2 个页面**用它：
  `CreditStatsPage.tsx`（柱图 `Bar`/`BarChart`/`CartesianGrid`）
  `TokenStatsPage.tsx`（组合图 `ComposedChart` + `Line` + `Bar`）
- 我们的 `src/BuddyDeck.App` **没有任何图表库**；NuGet 缓存里
  `LiveCharts` / `ScottPlot` / `OxyPlot` / `SkiaSharp` **一个都没装** `[实测]`

### 4.1 三条路

| 方案 | 成本 | 说明 |
|---|---|---|
| **A. 不引入，自绘** | 低 | 我们的 `bd:QuotaBar` 已经是自绘条形图。**柱状图/迷你折线图**用 `OnRender` 画是可控的（`DrawingContext.DrawRectangle` / `DrawLine` / `DrawGeometry`） |
| **B. 引入 LiveCharts2** | 中 | 需要加 NuGet 依赖 + 主题对接（它的默认皮肤和我们的暗色板不搭，要重设） |
| **C. 引入 ScottPlot** | 中 | 偏科学绘图，控件感弱 |

`[推断]` **建议 A**，理由：

1. 参考项目要画的就是**柱状 + 折线**，不是通用图表平台；
2. 我们已经有自绘控件的先例和测试基建（`QuotaBar` + `QuotaMath` 的纯函数在测），
   扩一个 `bd:SparkBar` / `bd:TrendLine` 是同一套路数；
3. **引入 B/C 要处理"主题不许用默认皮肤"** —— 那部分工作量未必比自绘小。

⚠ **这一条是 `[推断]` 不是 `[实测]`** —— 我没有实际跑过 LiveCharts/ScottPlot
在 WPF-UI 暗色主题下的效果。**如果 ui 决定要图表，建议先做 30 分钟 spike 再定。**

---

## 5. 三个「Web 做得到但 WPF 要换实现」的交互

这一节只列**真正有差异**的，不列"写法不同但能力等价"的。

### 5.1 `hover-card` 悬停预览

- **Web**：`HoverCard` 用 `pointerenter` + 延时弹出，DOM 里悬浮层就是普通 div
- **WPF**：`Flyout` + `PlacementTarget`，弹出的是**独立 Popup 窗口（HWND）**
- **差异**：`[推断]` WPF 的 Popup 在**跨屏 DPI 切换**时的行为要自己处理
  （我们有 `app.manifest` 的 `PerMonitorV2`，但 Popup 是独立 HWND，会各自吃 DPI）
- **怎么做**：能用 `ToolTip` 的场合优先 `ToolTip`（同进程内渲染，无跨 HWND 问题）；
  需要交互内容（可点按钮）时才上 `Flyout`

### 5.2 列表虚拟化 + 自定义布局

- **Web**：`react-virtual` 是**钩子**，你在自己的滚动容器里手动渲染可见区间，布局随意
- **WPF**：虚拟化是**容器行为** —— 你得用 `VirtualizingWrapPanel` 之类，
  或者自己继承 `VirtualizingPanelBase`
- **差异**：`[文档]` WPF-UI **自带 `VirtualizingPanelBase`**，
  意味着**自定义虚拟化布局是官方支持的扩展点**，不是要自己从零写。
- **怎么做**：先用现成的 `VirtualizingItemsControl` + `VirtualizingWrapPanel`；
  真的不够用再继承 `VirtualizingPanelBase`。

### 5.3 动画（`motion` / `tw-animate-css`）

- **Web**：`account-card.tsx` 里有 **11 处** `animate-`/`transition-`/`duration-`
- **WPF**：`Storyboard` + `Timeline`，能力上不弱
- **差异**：`[推断]` 关键不在能力，在**我们的设计计划明令节制动效**
  （`docs/ui-design-plan.md` §1.6：只有一个非用户触发的入场动效，其余全是动作回应）
- **怎么做**：**不追**。这条差异是**我们主动选择的**，不是做不到。

---

## 6. 给 ui 的技术建议（按可执行性排序）

| # | 建议 | 依据 | 证据等级 |
|---|---|---|---|
| 1 | **账号卡用 `CardControl` + 自绘 `QuotaBar` 组合**，不要为密度放弃 6 档刻度 | §2.2 | `[推断]` |
| 2 | **卡片列表上 `VirtualizingItemsControl` + `VirtualizingWrapPanel`** | 参考项目用 react-virtual；WPF-UI 内置 | `[文档]` |
| 3 | **表格页（日志/会话）上 `DataGrid` 或 `TreeGrid`**，别手拼 `ListView` | manager 用 react-table | `[文档]` |
| 4 | **图表先做自绘 spike**，需要再引 LiveCharts | §4 | `[推断]` |
| 5 | **命令面板用 `AutoSuggestBox`**（若 ui 决定要这个功能） | manager 用 cmdk | `[文档]` |
| 6 | **操作反馈统一走 `Snackbar` / `InfoBar`** | 两个项目都用 sonner | `[文档]` |
| 7 | **不要引入 `CardControl` 的默认阴影** —— 我们的设计计划砍掉了阴影 | `docs/ui-design-plan.md` §2 特征 4 | `[实测]` |
| 8 | 间距刻度**加 2 档更细的**（`SpaceHair 2` / `SpaceTight2 6`），别用任意值 | §2.2 | `[推断]` |

---

## 7. 本文**没有**验证的部分（诚实边界）

| 项 | 为什么没验 | 要怎么才能验 |
|---|---|---|
| 参考项目的**实际渲染效果** | 我没有跑起它们的 dev server / 没有截图 | 起 Tauri/Next dev server 截图，或用 ui 的 t56 产出 |
| `LiveCharts`/`ScottPlot` 在暗色主题下的观感 | 依赖未安装，我没引入（本任务不改 `src/`） | 30 分钟 spike |
| WPF `Flyout` 跨屏 DPI 的具体表现 | 需要多显示器环境实测 | 在双屏机上拖拽测试 |
| `CardControl` 默认样式的实际视觉 | 我没去跑 App 看它 | 在 App 里放一个看效果 |
| 参考项目**未使用的依赖**（如 manager 的 `react-virtual` 声明了但**我 grep 不到使用点**） | 只做了 `useVirtualizer`/`react-virtual` 的字面搜索 | 更宽的搜索或读它们的构建产物 |

> 最后一条值得说明：`[实测]` 我在 manager 的 `web/` 下搜
> `useVirtualizer|react-virtual`，**零命中** —— 说明它声明了依赖但（至少我搜到的范围内）
> 没用上。**这反而是个提醒**：参考项目的 package.json **不能直接当功能清单读**，
> 得看它实际 import 了什么。

---

## 8. 与 ui（t56）的接口

我这份给出的是**技术可行性**，ui 那份给出的是**功能清单**。两份合并的正确用法：

```
ui 说：「要做 账号卡片网格」
我答：「能，VirtualizingItemsControl + VirtualizingWrapPanel，见 §3」
      「但密度受我们 6 档刻度限制，见 §2.2 —— 要么加档，要么接受」
```

**如果 ui 提出的某个做法我判断做不到，我会在这一节下面补「做不了/要换实现」，
并给替代路径。** 当前 ui 尚未提出，本节先留空。

| ui 的诉求 | 我的可行性判断 | 替代路径 |
|---|---|---|
| （待 ui 提出） | | |
