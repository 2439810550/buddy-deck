# BuddyDeck.Next 界面设计计划

> 写于 DSH AgentTeams 会话。作者：design。
> 读者：ui（页面 XAML 与 ViewModel）、captain、后续接手的人。
> 方法：`D:\workfandaiapi\_skills\skills\skills\frontend-design\SKILL.md` 的「两遍法」——
> 先出紧凑 token 方案，再对照 AI 生成设计的五条识别特征自查，改掉「换个题目也会这么做」的部分。

---

## 0. 设计对象是谁

界面的主体只有两种东西：

| 对象 | 它到底是什么 | 用户每天用它做什么 |
|---|---|---|
| **账号** | 一份有寿命的凭证。会过期、会被上游限流、会按模型分头冷却 | 看哪个号还能撑、续期、切号 |
| **网关** | 本机 8787 上的一个进程。可能没起、可能跑的是旧代码 | 确认它在不在、是不是新代码 |

所以界面的**首要任务**是回答一句话：

> 现在这一池号，还能不能接活？

不是「展示数据」，是**值班**。它的近亲是配电室的馈线柜面板、是调度台，不是
营销站、不是 SaaS 后台。用户盯的是**状态和余量**，不是「指标卡片带来的成就感」。

**受众**：一个人，自己用，通常开着面板做别的事，偶尔瞥一眼。
**后果**：信息要**扫一眼就够**，不需要读数精细；而需要动作的东西（该续期了、
某个模型在冷却）不能被埋。

---

## 1. 第一遍：token 方案

### 1.1 色板（6 个基色）

不是「品牌配色 + 点缀色」，是**一个值班台的面板漆**。暗色是环境的默认：
这个面板常年和终端、IDE 并排开着，亮色大面积会刺眼。

| 名字 | Hex | 它是什么 | 服务哪个信息层级 |
|---|---|---|---|
| `Ink` | `#0E1114` | 面板底。带一点蓝的深灰，不是纯黑 | L0 窗口底 —— 只做背景，不承载信息 |
| `Slate` | `#171C21` | 表面 —— 列表行、分区块 | L1 内容底 |
| `Rail` | `#212932` | 抬升面 —— 悬停行、弹层、被选中项 | L2 交互反馈 |
| `Chalk` | `#E6EDF3` | 主文字。冷白，不用 `#FFF` | L3 文字主层 |
| `Steel` | `#8B98A5` | 次文字 / 标签 / 单位 | L4 文字次层 |
| `Amber` | `#D2A24C` | 唯一强调色。老式仪表盘的指示黄 | L3 焦点与「这个能点」 |

**为什么强调色是琥珀黄，不是电蓝/酸绿**：蓝是「科技感」的默认答案，也是
Mica 系统强调色会和它打架的颜色；酸绿撞五条识别特征里的第 2 条。琥珀是
**配电盘指示灯的既有语汇**——它的含义在现实里已经存在（注意、带电、现在在这里），
不需要用户重新学。而且它是暖色，在冷底上跳得出来，却不刺眼。

### 1.2 状态色（语义层，6 个）

状态色**不参与装饰**，只用在能表达账号真实状态的地方。

| 名字 | Hex | 对应 | 语义（界面上的原话） |
|---|---|---|---|
| `Ok` | `#4F9E6E` | `Healthy` | 可用 |
| `Warn` | `#D2A24C` | `ExpiringSoon` | 即将过期（= 琥珀，强调色复用，这是刻意的） |
| `Stop` | `#C9553F` | `Expired` | 已过期 |
| `Fault` | `#B4713C` | `Degraded` | 异常（上游拒绝/被限流） |
| `Idle` | `#5A6672` | `Unknown` / 离线 | 未知 / 未启动 |
| `Cool` | `#6E7FA8` | 冷却中 | 等 N 分钟后恢复 |

**注意 `Fault` 和 `Stop` 是两个不同的红**：`Stop` 是暗红（号废了，要你换），
`Fault` 是锈橙（上游的问题，等一等，别动）。把两者做成同一个红，用户就分不清
「我该去续期」还是「我该等」——**这正是这个界面最需要区分的两种处境**。

`Cool` 用蓝紫而不是红：冷却是**正常的调度行为**，不是故障。上游限流一个模型是
家常便饭，池子会自动换号。给它一个故障色的红，会让用户以为天天出事故。

### 1.3 字体与角色

两条：一条界面文本，一条数字。**不引入第三个**，不装字体文件。

| 角色 | 字体 | 字号 / 字重 | 用在哪 |
|---|---|---|---|
| 界面文本 | `Segoe UI Variable Text` → 回退 `Segoe UI` | — | 全局 |
| 页标题 | 同上 | 20 / SemiBold | 页面顶部那一个标题 |
| 区块标题 | 同上 | 14 / SemiBold | 「账号」「最近请求」这类分组 |
| 正文 | 同上 | 13 / Normal | 列表行、说明 |
| 小字 | 同上 | 12 / Normal | 次要说明、时间戳 |
| 微标签 | 同上 | 11 / Normal | 表头、单位 |
| 数字 | `Cascadia Mono` → 回退 `Consolas` | 各自跟随上下文 | 用量数字、端口、buildId、耗时 |

**为什么数字要等宽**：一列用量数字刷新时会跳宽度（`9`→`10` 整行右移），
等宽让数字**站住不动**。这是功能需要，不是「等宽很极客」的装饰——所以
它不对应识别特征里的「等宽做小标签的封面感」，只用在**真数字**上。

**大小写**：界面文本一律**句子大小写**，不用全大写做标签（识别特征 5 的
tracked-out ALL-CAPS eyebrow 直接砍掉）。`buildId` 这类本身就是标识符的照抄原样。

### 1.4 间距刻度

4 的倍数，但**分组间距是唯一的 24**——层级靠距离讲，不靠框。

```
4   紧贴（图标与它说明的文字）
8   同一行内的元素
12  行内边距（列表行上下）
16  控件之间、卡片内边距
24  区块之间 ← 唯一的分组距离
32  页面外边距
```

### 1.5 圆角 —— 这里是最关键的反模板决定

**不是「所有东西同一个圆角」**（识别特征 4 的核心）。圆角在 BuddyDeck 里是
**尺寸的代理**：东西越大越晚被看见，越方。层级靠**结构**（位置、留白、
左侧状态条）表达，不靠圆角大小表达——但既然要有圆角，就让它**说尺寸**。

| 名字 | 值 | 用在哪 | 为什么 |
|---|---|---|---|
| `RadiusBar` | 2 | 用量条、状态条、分隔线 | 细长元素大圆角会变「药丸」，像按钮 |
| `RadiusRow` | 4 | 列表行、输入框 | 行是**排队的单位**，方一点才像表 |
| `RadiusPanel` | 6 | 单独的分区块 | |
| `RadiusDialog` | 8 | 弹窗、菜单 | 只有浮起来的东西才配最圆 |

**没有阴影。** 阴影是「卡片悬浮在页面之上」的隐喻，而 BuddyDeck 的列表
**不悬浮**——它就是一页数据。层级用 `Ink → Slate → Rail` 三级明度 + 1px 边框
表达。这样既躲开了 `rgba(0,0,0,.1)` 的默认阴影，也躲开了「立体卡片套装」。

### 1.6 动效时长

只有一个非用户触发的动效。其余全是**动作的回应**。

| 名字 | 值 | 用在哪 |
|---|---|---|
| `MotionInstant` | 0 ms | 悬停、聚焦 —— 直接切，不加过渡 |
| `MotionFeedback` | 120 ms | 行悬停底色、按钮按下 |
| `MotionExpand` | 180 ms | 展开详情、状态条宽度变化 |
| `MotionEnter` | 220 ms | **唯一**的入场：内容首帧出现 |
| `MotionCooldown` | 1000 ms | 冷却倒计时的刷新节奏（不是动画，是心跳） |

**没有 fade-and-slide-up 的逐块入场**（识别特征明确点名）。只有一个整页
`MotionEnter`，而且是透明度，不位移——数据面板里元素从下方滑进来会让人
以为内容在加载。

### 1.7 布局概念

> **一句话**：左边一条常驻的「状态轨」，把每个账号的余量画成竖条，
> 右边是常规的列表 / 详情工作区——值班的人先看轨，再决定要不要看右边。

```
┌───────────────────────────────────────────────────────────────────────┐
│  BuddyDeck          [网关 ● 已连接 127.0.0.1:8787]      [刷新]  [设置] │ ← 顶栏 48px
├──────────┬────────────────────────────────────────────────────────────┤
│ 状态轨   │  账号                                                      │
│ (56px)   │                                                            │
│          │  ┌──────────────────────────────────────────────────────┐  │
│ ▓▓▓▓▓▓▓▓ │  │▌ 默认池 · 主号            [可用]                      │  │
│ ▓▓▓▓▓▓   │  │▌ 企业 · a1b2c3d4…         剩 12 天 · 06-12 09:30     │  │
│ ▓▓▓      │  ├──────────────────────────────────────────────────────┤  │
│ ▓▓       │  │▌ 备份号                  [即将过期]                  │  │
│ ░░░░░░░░ │  │▌ 个人 · e5f6…             剩 4 小时 · 今天 18:20      │  │
│ ░░░░░░░░ │  ├──────────────────────────────────────────────────────┤  │
│ ░░░░░░░░ │  │▌ 测试号                  [已过期]                    │  │
│          │  │▌ 个人 · 99aa…             过期 2 天                   │  │
│   ▁ 余量 │  └──────────────────────────────────────────────────────┘  │
│          │                                                            │
│  9 个号  │  最近请求                          （指标区，空时有邀请文案）  │
│  6 可用  │  ────────────────────────────────────────────────────────  │
│  2 冷却  │  klm-1 … 正常   820 ms   默认池                              │
└──────────┴────────────────────────────────────────────────────────────┘
```

**对齐**：全部**左对齐**。数字右对齐（用量、耗时）。**不居中任何东西**
——居中只出现在空态和错误态，因为那时页面上只有一句话，居中才是它该在的位置。

**状态轨为什么值得占 56px**：它把「还有多少能打」变成一个**余光可见**的东西。
列表要读文字才知道哪个号要没了；竖条扫一眼就知道。它也是这个界面的签名元素
——不占别的界面会占的视觉位置。

**列表行的左侧 3px 竖条**（ASCII 里的 `▌`）：行状态的第一层表达，颜色对应
1.2 的状态色。这样即使文字部分被扫过，状态色依然在。

### 1.8 原则

1. **层级靠结构，不靠装饰。** 位置、留白、左侧状态条。没有阴影，没有渐变，
   没有「卡片」——列表就是列表。
2. **一个签名元素，其余安静。** 状态轨是唯一大胆的东西（SKILL.md：把胆量花在
   一个地方）。列表、按钮、表单全部克制到近乎无设计。
3. **状态色必须说真话。** 只在能表达真实状态的地方上色。装饰性的彩色一律没有。
4. **空的时候给动作，错的时候给原因和修法。** 见第 3 节。
5. **数字站着不动。** 等宽、右对齐、刷新时位置不变。
6. **界面是值班，不是仪表盘秀。** 不放「总请求数」的大数字英雄区——
   那是最容易被生成出来的默认版面（SKILL.md 明确点名），而用户真正想知道的是
   「现在能不能接活」。

---

## 2. 第二遍：对照五条 AI 识别特征自查

SKILL.md 第 38–43 行列的五个特征，逐条对照我第一遍的方案。

### 特征 1：奶油底 `#F4F1EA` + 高对比衬线标题 + 陶土色点缀 `#D97757`

**第一遍有没有踩**：没有。底色是 `#0E1114` 冷深灰，字体是 Segoe UI Variable
（系统 UI 无衬线），强调色是 `#D2A24C` 琥珀。

**但要检查的是「换一个深色主题的题目，我是不是也这么做」**：深色 + 单一亮色
点缀本身是识别特征 2 的形状。所以我做了两件事把它变成**针对本题的选择**：

- 强调色选了**暖琥珀**而非电蓝/酸绿，理由写在第 1.1 节（Mica 系统色冲突 +
  配电盘既有语汇）。换题目（比如做一个音乐播放器）我不会选琥珀。
- **状态色参与构图**，不是只有点缀。第一遍方案里 6 个状态色全部有明确归属，
  而且是这个「账号有寿命」的主题**逼出来的**——换个题目凑不出这 6 个。

**结论**：保留深色。不是默认，是理由充分的。

### 特征 2：近黑底 + 单一酸绿/朱红点缀

**踩了。这是第一遍最大的默认选择。** 深色面板 + 一个跳色，是最省事的路径。

**改了什么**：

- 底色从「近黑」推到 **`#0E1114` 带蓝的深灰**，并建立 **三级明度阶梯**
  （`Ink/Slate/Rail`）。近黑 + 平坦表面是靠点缀色拉层级，我用明度阶梯拉层级
  ——这直接消掉了「深色卡片」的观感。
- 点缀色**不是单一**。有强调色（琥珀）+ **6 个有语义的状态色**。第一遍方案
  里状态色的地位和强调色是平的：颜色在这个界面里是**状态编码**，不是氛围。
- 颜色总数从「1 点缀」变成「6 基色 + 6 语义色」，但**约束更严**：语义色
  不许用作装饰，基色不许表达状态。

**为什么**：这个界面的主题就是「状态」，把颜色省成单一装饰色是丢掉了主体材料。

### 特征 3：broadsheet 排版 —— 发丝线、全零圆角、密排报纸栏

**第一遍有没有踩**：部分踩了形状（列表 + 分隔线），没踩排版（没有报纸栏、
没有衬线、没有密排正文）。

**改了什么**：

- **圆角不全零，也不一刀切**（见 1.5）。零圆角是报纸的风格选择；我这里是
  **按尺寸分级的圆角**，`2/4/6/8` 各自对应「条/行/区块/浮层」。这是第二遍里
  我给自己加的最重要的一条约束，它同时也是对特征 4 的回答。
- 分隔线用 `1px` **是结构**（行与行的边界），不是「发丝线装饰」。两者区别：
  装饰性发丝线出现在不该有的地方（标题下、区块外框）。我只在列表行之间用。

### 特征 4：SaaS 卡片套装 —— 内容剁成同样的圆角卡、一个圆角通吃、同一个 `rgba(0,0,0,.1)` 软阴影、渐变做装饰

**这条踩得最重，也是这个任务点名禁止的。** 第一遍草稿里我确实写了「卡片内边距 16」，
脑子里是卡片。

**改了什么**（逐条对应）：

| 默认做法 | 改成 | 为什么 |
|---|---|---|
| 内容剁成卡片 | **列表行**，靠分隔线与左侧状态条切分 | 行是排队的单位，卡片是「独立内容块」的隐喻。账号是列表，不是卡片集 |
| 一个圆角通吃 | `RadiusBar 2 / RadiusRow 4 / RadiusPanel 6 / RadiusDialog 8` | 圆角说尺寸 |
| `rgba(0,0,0,.1)` 软阴影 | **完全取消阴影**，用 `Ink→Slate→Rail` 三级明度 + 1px 边框 | 列表不悬浮，悬停才抬升一级明度 |
| 渐变做装饰 | **没有任何渐变** | 渐变在这里没有要传达的信息 |
| 「大数字 + 小标签 + 统计卡」的英雄区 | 顶栏一行状态 + 左侧状态轨 | SKILL.md 第 17 行点名这是默认版面。而且「3 个账号」不值得占一个英雄位 |

**这是第二遍里改动最大的一条。** 改完后整个界面没有「卡片」这个概念，
只有列表行、区块、浮层三种容器。

### 特征 5：模板化界面外壳 —— 每个标题上方 tracked-out 全大写小标签、`·` 连接元数据、`WORD — fragment` 破折号标签、`#0B0B0B` 冒充黑、等宽做小数据标签、`→` 加在按钮文字后

逐条处理（这条是**最容易不知不觉踩**的，因为它散在细节里）：

| 默认模型 | 决定 |
|---|---|
| 标题上方全大写 eyebrow 标签 | **删掉**。区块标题自己就是标题，不需要「ACCOUNTS」压在上面 |
| `·` 连接元数据 | **保留，但收紧**：顶栏状态串 `127.0.0.1:8787 · workbuddy · 17 个模型` 用 `·`——这三项确实是并列的碎片元数据，`·` 是它的正确排版。其余地方（列表行）改用逗号或直接分行 |
| `WORD — fragment` 破折号标签 | **不用**。标签就是标签，`可用`，不写成 `STATUS — 可用` |
| `#0B0B0B`/`#111` 冒充黑 | **不用**。底层是 `#0E1114`（带蓝），是选定色不是「接近黑的黑」 |
| 等宽做小数据标签 | **限制**：等宽**只用于真数字**（用量、端口、buildId、耗时）。不用等宽做「极客感」的标签或 eyebrow |
| `→` 加在链接/按钮后 | **不用** |

**另外自查两条 SKILL.md 第 25–28 行的排版禁令**：

- 不给标题里的单个词换色/加粗（不用）。
- 不用全大写做标签（不用，见上）。

---

## 3. 文案基调（界面自己的声音）

这一节是**规范**，ui 写 XAML 时按这个写，不自由发挥。

### 3.1 硬规则

1. **主动语态，动词开头。** 按钮写「导入登录文件」不写「导入」了事，写
   「续期全部」不写「批量操作」。
2. **按钮点下去发生什么，就写什么。** 按钮「保存」→ 提示「已保存」。
   **不写「提交」**——提交是个不说明结果的词。
3. **同一动作全流程同名。** 按钮叫「续期」→ 进行中叫「正在续期」→ 完成叫
   「已续期」→ 失败叫「续期失败」。从头到尾一个词。**绝不**按钮叫「刷新」
   提示叫「已更新」。
4. **句子大小写。** 「导入登录文件」不是「导入登录文件」。英文标识符
   （`buildId`、`8787`）照原样。
5. **禁用词表**：赋能、一键、智能、便捷、强大、轻松、极致、无缝、优雅。
   **一个都不许出现。** 这些词的作用是让作者自我感觉良好，不是让用户看懂。

### 3.2 空态：邀请动作，不表达情绪

空屏是**一个还没做的动作**，不是一个需要被安慰的处境。

| 位置 | 文案（标题 + 说明 + 按钮） |
|---|---|
| 账号列表空 | **还没有账号** / 导入一份登录文件就能开始。 / `[导入登录文件]` |
| 指标空 | **还没有请求记录** / 发一次聊天测试就能看到。 / `[去聊天测试]` |
| 网关未启动 | **网关未启动** / 点下面的按钮在本机 8787 上启动它。 / `[启动网关]` |
| 冷却中（无其他可用号） | **所有号都在冷却** / 最近一个 3 分钟后恢复。 / `[等一会儿再试]` |

**不许出现**：🙂、插画、「这里空空如也~」、「暂无数据」（说了等于没说）、
「开始你的旅程吧」。空态里**没有「~」**。

### 3.3 错误态：说清发生了什么 + 怎么修

错误**不道歉**（不说「抱歉」「对不起」），**不含糊**（不说「出错了」「操作失败」）。

固定三段结构：

```
[发生了什么]        ← 一句，具体到对象和现象
[为什么/在哪]       ← 一句，给出定位信息（端口、路径、状态码）
[怎么修]            ← 一句祈使句，或一个按钮
```

实例：

| 场景 | 文案 |
|---|---|
| 网关连不上 | **连不上 127.0.0.1:8787** / 端口上没有进程在监听。 / `[启动网关]` `[检查端口]` |
| 保险库读不了 | **读不了保险库** / `C:\Users\…\vault.json` 解析失败：第 12 行缺 `expiresAtMs`。 / `[打开所在目录]` |
| 上游 401 | **凭证失效** / 上游拒绝了这份 token，它可能已经在别处登录过。 / `[重新导入]` |
| 上游 429，全部冷却 | **所有号都被限流** / 上游要求 5 分钟后再试，最快 3 分钟后恢复。 / `[等一会儿再试]` |
| 磁盘上的代码和跑的不一致 | **跑的不是最新代码** / 进程里的指纹 `aec74ceb` 和磁盘上的 `7f21b004` 不一样。 / `[重启网关]` |

**绝不允许**的写法：`操作失败` / `出错了` / `未知错误` / `请稍后重试` /
`Something went wrong`。每一条都必须能让用户**下一步做点什么**。

### 3.4 口吻

会话式的平实动词，像同事在旁边说一句。不俏皮，不卖萌，不用感叹号。

---

## 4. 交付给 ui 的资源 key 清单

### 4.1 资源字典文件与引用顺序（`Source` 相对路径）

放进 `App.xaml` 的 `MergedDictionaries`，**必须按这个顺序**（后面的引用前面的）：

```xml
<ResourceDictionary Source="Themes/Colors.xaml" />   <!-- 色板 + 语义色 -->
<ResourceDictionary Source="Themes/Typography.xaml" /> <!-- 字号字重 -->
<ResourceDictionary Source="Themes/Metrics.xaml" />  <!-- 间距 + 圆角 + 描边 -->
<ResourceDictionary Source="Themes/Motion.xaml" />   <!-- 动效时长 -->
<ResourceDictionary Source="Themes/Controls.xaml" /> <!-- 组件样式（依赖上面四组） -->
```

**五行全引一遍**，保持引用显式，出问题好定位。

#### ⚠ 跨字典引用只能用 `DynamicResource`（这条是硬约束，踩过）

实施时发现的真坑，已由测试 `ThemeResourceTests` 钉住：

- `StaticResource` 在**解析期**只查当前字典，查不到被 merge 进来的兄弟字典 →
  运行时抛 XamlParseException「无法找到名为 X 的资源。资源名称区分大小写。」

规则：

| 位置 | 用什么 |
|---|---|
| `Setter.Value`、普通属性、`TemplateBinding` 之外的一切 | **`DynamicResource`** |
| `BasedOn` | **只能 `StaticResource`**（它不是 DependencyProperty） |
| `TargetType` | 字面类型名，不能是资源 |

`BasedOn` 的特殊性带来一个结构性后果：**任何用了 `BasedOn` 的样式，必须和它继承的样式住在同一本字典里**。
这就是为什么 `ListRowTitle` / `ListRowMeta` / `FieldLabel` / `FieldValue` / `FieldValueMono`
这几个复合样式住在 `Typography.xaml`（它们继承 `TextBody` / `TextSmall` / `TextNumber`），
而不是和 `ListRow` / `PrimaryButton` 一起放在 `Controls.xaml`。

**页面里引我的 key 一律用 `DynamicResource`**（页面和字典必然是跨字典）：

```xml
<Border Style="{DynamicResource ListRow}" />
<TextBlock Style="{DynamicResource ListRowTitle}" />
```

### 4.2 颜色 key（`Colors.xaml`）

**SolidColorBrush**（供 `Background`/`BorderBrush` 用）：

| Key | 值 | 用途 |
|---|---|---|
| `InkBrush` | `#0E1114` | 窗口底（Mica 下内容层的不透明底） |
| `SurfaceBrush` | `#171C21` | 列表/区块底 |
| `RailBrush` | `#212932` | 抬升面：悬停行、弹层 |
| `RailSubtleBrush` | `#1B2229` | 半抬升：斑马纹、表头 |
| `HairlineBrush` | `#262E37` | 1px 分隔线、边框 |
| `FocusBrush` | `#D2A24C` | 焦点框 |
| `TextPrimaryBrush` | `#E6EDF3` | 主文字 |
| `TextSecondaryBrush` | `#8B98A5` | 次文字/标签/单位 |
| `TextTertiaryBrush` | `#5A6672` | 占位符、禁用 |

**Color**（供自绘组件取原始色、做 opacity 运算）：

| Key | 值 |
|---|---|
| `AccentColor` | `#D2A24C` |
| `OkColor` | `#4F9E6E` |
| `WarnColor` | `#D2A24C` |
| `StopColor` | `#C9553F` |
| `FaultColor` | `#B4713C` |
| `IdleColor` | `#5A6672` |
| `CoolColor` | `#6E7FA8` |

**同色 Brush 版本**（自绘组件直接 `DynamicResource` 用，省得转）：

`AccentBrush` `OkBrush` `WarnBrush` `StopBrush` `FaultBrush` `IdleBrush` `CoolBrush`

**状态描边**（左侧 3px 竖条用，比实色浅一档，免得糊成一块）：
`OkRailBrush` `WarnRailBrush` `StopRailBrush` `FaultRailBrush` `IdleRailBrush` `CoolRailBrush`

### 4.3 排版 key（`Typography.xaml`）

`FontFamily` / `FontSize` / `FontWeight` 三类，key 如下：

| Key | 值 | 角色 |
|---|---|---|
| `FontUi` | `Segoe UI Variable Text, Segoe UI` | 界面文本 |
| `FontNumeric` | `Cascadia Mono, Consolas, Segoe UI` | 数字 |
| `FontSizeMicro` | 11 | 表头、单位 |
| `FontSizeSmall` | 12 | 次要说明、时间戳 |
| `FontSizeBody` | 13 | 列表行、正文 |
| `FontSizeSection` | 14 | 区块标题 |
| `FontSizeTitle` | 20 | 页标题 |
| `FontWeightNormal` | Normal | 正文 |
| `FontWeightEmphasis` | SemiBold | 标题、强调。**只有这两个**，不用 Bold |
| `FontSizeBadge` | 11.5 | 状态徽章里的词 |
| `LineHeightBody` / `LineHeightSmall` | 19 / 17 | 行高 |

> ⚠ 上面第一版文档里写的是 `FontWeightRegular` / `FontWeightSemiBold`，
> **实际落盘的 key 是 `FontWeightNormal` / `FontWeightEmphasis`**。
> 以本节现在的表为准（ui 已按这套对齐）。

**TextBlock 成套样式**（直接用 `Style="{DynamicResource TextPageTitle}"`）：

`TextPageTitle` `TextSectionTitle` `TextBody` `TextSmall` `TextMicro`
`TextNumber` `TextNumberStrong` `TextParagraph`

**复合样式（继承上面某一条，因此住在同一本字典里）**：

`ListRowTitle`（= `TextBody` + SemiBold）`ListRowMeta`（= `TextSmall`）
`FieldLabel`（= `TextSmall`）`FieldValue`（= `TextBody` + 换行）
`FieldValueMono`（= `TextNumber` + 换行）

### 4.4 间距 / 圆角 / 描边 key（`Metrics.xaml`）

| Key | 值 | 用途 |
|---|---|---|
| `SpaceInline` | 4 | 图标与它的文字 |
| `SpaceTight` | 8 | 同一行内元素 |
| `SpaceRow` | 12 | 列表行上下内边距 |
| `SpaceControl` | 16 | 控件之间、区块内边距 |
| `SpaceSection` | 24 | **区块之间（唯一的分组距离）** |
| `SpacePage` | 32 | 页面外边距 |
| `RadiusBar` | 2 | 用量条、状态条 |
| `RadiusRow` | 4 | 列表行、输入框 |
| `RadiusPanel` | 6 | 分区块 |
| `RadiusDialog` | 8 | 弹窗、菜单 |
| `StrokeHairline` | 1 | 分隔线粗细 |
| `StrokeFocus` | 2 | 焦点框粗细 |
| `SizeStatusRail` | 56 | 左侧状态轨宽度 |
| `SizeRowRail` | 3 | 行左侧状态条宽度 |
| `SizeTopBar` | 48 | 顶栏高度 |
| `SizeQuotaBarHeight` | 6 | 用量条高度 |
| `SizeStatusDot` | 8 | 网关状态点直径 |
| `SizeLabelColumn` | 160 | 详情页「标签/值」的标签列宽 |
| `SizeMaxReadingWidth` | 720 | 正文最大宽度（行宽 < 80 字符） |
| `BorderHairline` | `1` | Border 的 1px 描边 |
| `BorderLeftRail` | `3,0,0,0` | 只画左边的 3px 竖条描边 |

#### ⚠ 描边有两组 key，类型不同，别拿错（实际踩过一次）

```
StrokeHairline  sys:Double(1)   → 给 Rectangle.StrokeThickness / Pen 用
StrokeFocus     sys:Double(2)
BorderHairline  Thickness(1)    → 给 Border.BorderThickness 用
BorderLeftRail  Thickness(3,0,0,0)
```

这两组的**数值都是 1**，所以特别容易混。拿错的后果：

```xml
<Border BorderThickness="{StaticResource StrokeHairline}" />   <!-- ❌ 运行时炸 -->
```

**编译期完全通过**，运行时抛
`ArgumentException: 对 1 个参数，"BorderThickness" 是无效值`。
ui 六个页面共 19 处踩了这个。

口诀：**`Border` 用 `Border*`，`Rectangle`/`Pen` 用 `Stroke*`。**
回归测试见 `ThemeResourceTests.两组描边key不能互相替代`。

### 4.5 动效 key（`Motion.xaml`）

| Key | 值 |
|---|---|
| `MotionInstant` | `0:0:0` |
| `MotionFeedback` | `0:0:0.12` |
| `MotionExpand` | `0:0:0.18` |
| `MotionEnter` | `0:0:0.22` |
| `MotionCooldownMs` | `1000`（`Int32`，冷却倒计时的心跳间隔，不是动画时长） |

类型是 `Duration`。**用 `Duration` 的地方不要写 `"0:0:0.12"` 字面量**，
用 `{DynamicResource MotionFeedback}`。

### 4.6 Converters（`Converters/`）

**命名空间**：`xmlns:conv="clr-namespace:BuddyDeck.App.Converters"`

转换器**实例**在 `App.xaml` 的 `Application.Resources` 里声明一次。
下表**左列是 XAML 里的 key 名，右列是实现类名 —— 两列不要混**：

| XAML 里的 key | 转换器类 | 输入 → 输出 |
|---|---|---|
| `Timestamp` | `TimestampConverter` | `long`(Unix 毫秒) 或 `string`(ISO) → 本地时间串；参数 `date`/`clock` 换粒度 |
| `RemainingTime` | `RemainingTimeConverter` | 截止时刻（毫秒或 ISO）→「还剩 12 天」 |
| `CooldownTime` | `RemainingTimeConverter` | 同上，参数固定 `cooldown` →「3 分钟后恢复」 |
| `RatioText` | `RatioTextConverter` | `double`(0–1) → `93.4%` |
| `AccountStateBrush` | `AccountStateBrushConverter` | `AccountState` → Brush；参数 `rail` 竖条 / `badge` 徽章底 |
| `QuotaStateBrush` | `QuotaStateBrushConverter` | `QuotaState` → 条填充色；参数 `text` 换文字色 |
| `GatewayDotBrush` | `GatewayDotBrushConverter` | `bool`(在线) → 状态点色 |
| `BoolToVisibility` | `BoolToVisibilityConverter` | `bool` → `Visibility`（`true`→Visible） |
| `InverseBoolToVisibility` | 同上，**必须 `Invert="True"`** | 反相 |
| `StringToVisibility` | `StringToVisibilityConverter` | 非空串 → Visible |
| `EmptyStringToVisibility` | 同上，**必须 `Invert="True"`** | 空串 → Visible |
| `EmptyCollectionToVisibility` | `EmptyCollectionToVisibilityConverter` | 空集合 → Visible |
| `NotEmptyCollectionToVisibility` | 同上，**必须 `Invert="True"`** | 非空集合 → Visible |

#### ⚠ 反相 key 必须写 `Invert="True"`（实际踩过一次）

```xml
<conv:BoolToVisibilityConverter x:Key="BoolToVisibility" />
<conv:BoolToVisibilityConverter x:Key="InverseBoolToVisibility" Invert="True" />  <!-- ← 不能漏 -->
```

**漏了会怎样**：这个 key 名字叫 `Inverse`，但行为和正相**一模一样**
（因为反相原本靠 `ConverterParameter`，而参数是绑定时才传的，与 key 无关）。
后果是**静默的空白** —— ui 的仪表盘「离线时显示原因」那一块就渲染成了空。

**为什么改成属性**：`Invert` 是实例状态，`x:Key` 是实例名字，两者一一对应，
**不可能对不上**。靠 `ConverterParameter` 反相则依赖「每个使用点都记得传参数」——
那是一个必然会被漏掉的约定。

> 本节上一稿把这几个 key 直接列成「反相」却**没说要设 `Invert`**，
> 文档和实现对不上，ui 照文档写就踩了。现在以本表为准。
> （回归测试见 `VisibilityConverterTests.坑1_*`。）

#### ⚠ `EmptyCollectionToVisibility` 只认 `ICollection`，失败是静默的

绑 `{Binding Lines}`（集合本身）对。绑 `{Binding Lines.Count}`（int）**不对** ——
`int` 不实现 `ICollection`，模式匹配两个分支都不命中，判定为**「非空」→ 空态被折叠**，
于是**日志为空时界面看起来像「还在加载」**。不抛异常、不报警告。
LINQ 的 `Where(...)` 结果（裸 `IEnumerable<T>`）同理。

**绑集合本身；要过滤就先物化。**（回归测试见 `VisibilityConverterTests.坑2_*`。）

> **关于这段描述的来源**（记一笔，免得以后有人误以为它是实测结论）：
> 这条的行为方向最初是我按 captain 的转述写的，**没有独立验证**。
> `t10` 阶段我用可执行的测试把这个行为**钉成了断言**
> （`VisibilityConverterTests.坑2_绑Count时行为是静默判为非空` 与
> `裸IEnumerable同样被静默判为非空`），实测确认：**判定为「非空」是对的**，
> 即空态确实被折叠。现在这段描述与测试一致，不再依赖转述。

**关于 `BoolToVisibility`**：WPF-UI 自带 `BooleanToVisibilityConverter`，
但用我们自己的 —— 行为可预测，且支持实例级反相。

### 4.7 自绘组件（已落盘，属性以下表为准）

**命名空间**：`xmlns:bd="clr-namespace:BuddyDeck.App.Controls"`

| 控件类 | 属性 |
|---|---|
| `bd:QuotaBar` | `RemainingRatio`(0–1)、`CooldownUntil`、`StateOverride`(`QuotaState?`)、`Caption`、`ValueText`、`Now`；只读 `CurrentState` / `CurrentValueText` |
| `bd:AccountStateBadge` | `State`(`AccountState`)、`Label`(留空 = 用状态的标准说法) |
| `bd:EmptyState` | `Headline`、`Detail`、`ActionText`、`ActionCommand` |
| `bd:ErrorState` | `What`、`Where`、`Fix`、`FixText`、`FixCommand`、`NeedsAction`(true=暗红「要你动手」/ false=锈橙「等一等」) |
| `bd:StatusDot` | `Online`、`Stale`、`DotSize` |
| `bd:StatusRail` | `Items`(`IReadOnlyList<StatusRailItem>`)、`BarWidth`、`Now` |

`QuotaState` 是 `BuddyDeck.App.Controls` 下的 enum：
`Normal / Low / Warning / Critical / Cooldown`。
（第一版文档里漏写了 `Warning`，四档 + 冷却 = 五个成员，以代码为准。）

`StatusRailItem` 是 `readonly record struct(double RemainingRatio, QuotaState State)` ——
刻意做得很小，这样 `StatusRail` 不用依赖 ui 的 ViewModel 类型。

`AccountState` 用 ui 已经定好的 `BuddyDeck.App.Services.AccountState` —— **不重复定义**。

> `EmptyState` / `ErrorState` / `AccountStateBadge` 都是 `ContentControl`，
> **自己构造视觉树**（在 `OnInitialized` + 属性变化时重建）。
> 所以 ui 在页面里直接写属性就能看见东西，**不需要为它们写 ControlTemplate**。

### 4.8 样式 key（`Controls.xaml` + `Typography.xaml`）

| key | 住哪本字典 | 用途 |
|---|---|---|
| `ListRow` | Controls | 列表行容器 Border（下边 1px 线） |
| `ListRowContent` | Controls | 行内边距 Grid |
| `ListRowRail` | Controls | 行左侧 3px 状态条 Rectangle |
| `ListRowTitle` / `ListRowMeta` | **Typography** | 行的主文字 / 次行（有 `BasedOn`，必须同字典） |
| `PrimaryButton` | Controls | 一页最多一个的主动作 |
| `QuietButton` | Controls | 其余全部动作 |
| `FieldInput` | Controls | 表单输入框 |
| `SectionDivider` | Controls | 区块分隔线 |
| `FieldLabel` / `FieldValue` / `FieldValueMono` | **Typography** | 「标签/值」两列 |

---

## 5. 与 ui 的边界

- 我独占：`Themes/`、`Controls/`、`Converters/`、`Views/Presentation/`（纯逻辑）、
  `docs/ui-design-plan.md`、以及我自己的测试项目。
- ui 独占：`App.xaml`、`App.xaml.cs`、`MainWindow.xaml`、`Pages/`、`ViewModels/`、
  `Services/`、`BuddyDeck.App.csproj`。
- **`AccountState` 与 `AccountPresentation` 归 ui**（他先写的，且已在用）。
  我引用它，不改它。需要扩展时先跟他说。
- 需要新增 NuGet 包 → 告诉 ui，让他改 csproj（我不动那个文件）。

---

## 6. 验证记录（实际跑过的）

```
cd D:\workfandaiapi\_next

# 1) 界面项目编译
dotnet build src\BuddyDeck.App\BuddyDeck.App.csproj
→ 已成功生成。0 个警告 0 个错误

# 2) 设计系统测试
dotnet test tests\BuddyDeck.App.Presentation.Tests
→ 已通过! - 失败: 0，通过: 171，已跳过: 0，总计: 171，持续时间: 49 s
```

> ⚠ 我的测试项目**没有**进 `BuddyDeck.slnx`（那是 captain 的文件，我不动），
> 所以根目录直接 `dotnet test` 跑不到它，必须指定项目路径。

### 界面交付的独立复核（t10/t15，不是自述，是自己跑出来的）

| 复核项 | 实测结果 |
|---|---|
| `dotnet build src\BuddyDeck.App` | **0 个警告 0 个错误** |
| `--selftest` **离线**路径 | **退出码 0**，证据文件 1954 字节 / 21 行；7 个查询调用全部返回带原因的失败，6 个 ViewModel 全部正常填充，**无异常** |
| `--selftest` **真实网关在线**路径 | 见下方「曾经的挂死」—— 修复后 **退出码 0**，证据文件 924 字节 / 21 行 |
| 六张 `docs/ui-*.png` | 逐张 `read_image` 看过，**全部非空白**，渲染真实内容（含连上网关后的 buildId、状态点、401 错误条） |
| 六页可导航 | 六张截图**每一张**的顶栏都完整出现 `总览 / 账号 / 接入池 / 聊天测试 / 日志 / 设置`，当前页高亮 —— 导航存在且选中态可辨 |

#### 曾经的挂死：`--selftest` 在线路径（已由 t14 修复）

复核时实测到：`--selftest` 在**离线**路径退出码 0，但**真实网关在线**时
**永久挂死、退出码 -1、证据文件从未产出**。

根因是 **UI 线程上的 sync-over-async 死锁**，三个条件叠加：

1. `App.OnStartup` 在 **UI 线程**上同步调用 `SelfTest.Run`
2. `SelfTest` 内部用 `.GetAwaiter().GetResult()` **阻塞**等异步方法
3. ViewModel 的 await 是 `ConfigureAwait(true)`（共 30 处，**刻意的** ——
   要在 UI 线程上更新绑定属性）
   → UI 线程被 ② 堵住 → continuation 排不进 UI 线程 → 永久挂死

**为什么离线看不出来**：`NotConnectedGatewayClient` **同步返回**，
await 从不让出线程，continuation 根本不需要回 UI 线程。
所以「离线通过」掩盖了这个 bug —— **只有真实网关在线时才暴露。**

用受控实验锁定了因果（同样三条件，只改一个变量）：

```
A) 真实异步(会让出) + ConfigureAwait(true)  + 同步阻塞  ⇒ 挂死
B) 真实异步(会让出) + ConfigureAwait(false) + 同步阻塞  ⇒ 正常返回
C) 同步返回(不让出) + 同步阻塞                          ⇒ 正常返回
```

**A 挂、B 通，变量唯一（`ConfigureAwait` 的值）。**

**修复（t14，已落地）**：把诊断代码挪到线程池 ——
`App.xaml.cs` 改成 `Task.Run(() => SelfTest.Run(...)).GetAwaiter().GetResult()`。
UI 线程空着，continuation 随时能回来。**没有去改 ViewModel 的 30 处
`ConfigureAwait(true)`** —— 那是产品代码的正当需求（UI 线程更新绑定），
为测试改产品代码是本末倒置；挪诊断代码才是对的层次。

修复后复验：**在线路径退出码 0，924 字节证据文件正常产出**，内容为
`GetHealthAsync -> Online=True` + 六个 `/admin/*` 返回 `网关回了 401`
（8787 上跑的是旧 Node 网关，没有这些端点），
**全部是带原因的失败结果，没有异常** —— 契约成立。

### 测试覆盖了什么

| 文件 | 覆盖 |
|---|---|
| `TimeTextTests` | Unix 毫秒 / ISO 字符串两类输入；0 / 负数 / 秒级误传 / 超长 / null / 不认识类型；三种格式化；相对时间与冷却措辞 |
| `QuotaMathTests` | 10/30/60 三档阈值与边界；**冷却优先级高于百分比**；余量比例的 0 / 超支 / 无总量 / NaN；条宽的负数与超长轨道；比例文字 |
| `StatusAppearanceTests` | 每个状态都有映射；**`Expired` 与 `Degraded` 必须不同色**；**冷却不是红色**；竖条色与文字色同色相；徽章词表与 ui 的 `AccountPresentation` 一致 |
| `VisibilityConverterTests` | 反相的两种写法（属性 / 参数）与组合；**反相实例不依赖 ConverterParameter**；`ICollection` 的静默失败（含 `Lines.Count` 与裸 `IEnumerable`） |
| `ThemeResourceTests` | **用 WPF 真的加载五本字典**；无重复 key；具名 key 集合完整性；圆角分级；**无任何阴影**；描边两组 key 的类型契约 |
| `XamlResourceInvariantTests` | **全量扫描**全部非主题 XAML 的 `{Static/Dynamic Resource}` 引用；每个 key 真实存在；**属性类型与资源类型匹配**；含五条自动化自证用例 |
| `GatewayClientNeverThrowsTests` | `IGatewayClient` 的 **17 个操作**打到空端口上「永不抛」；离线实现同契约；错误文案可操作性；参数边界 |

### 这套测试实际抓到的问题（不是摆设）

1. `StaticResource` 跨字典引用 → `XamlParseException`（见 4.1 的规则）。
2. `BasedOn` 用 `DynamicResource` → 运行时禁止；而且**跨字典也会失败** →
   复合样式必须与父样式同字典（见 4.8 的「住哪本字典」列）。
3. `AccountStateBadge` 基类写成 `Control` 没有 `Content` → 改 `ContentControl`。
4. `Wpf.Ui.Controls` 与 `System.Windows.Controls` 的 `TextBlock` / `Button` 二义性。
5. **`BorderThickness` 吃到 `double` 型 key**（`StrokeHairline` 而非 `BorderHairline`）——
   这类「名字对、类型错」编译期 100% 通过，只在运行时报
   `ArgumentException`。`XamlResourceInvariantTests` 能定位到**具体文件与属性**
   （自证见 6.1）。
6. **`IGatewayClient.RenameAccountAsync(null, …)` 抛 `ArgumentNullException`** ——
   见 6.2 的已知缺陷记录。
7. **`--selftest` 在线路径永久挂死**（UI 线程 sync-over-async）——
   见上方「曾经的挂死」，已由 t14 修复并复验。

### 6.1 自证：不变量测试真的会红（做了两次，两种注入点）

「加了一组测试」不等于「这组测试有用」。常见的两种假绿：
**正则写错 → 一处引用都没扫到**；**类型映射写空 → 每种情况都走 `continue`**。
两种情况下「断言没有不匹配」都会自然通过。

所以做了两次故意注入实验，**注入点不同**（一次在数据侧、一次在检查器侧）：

#### 实验一：往页面里注入错误写法（数据侧）

往 `DashboardPage.xaml` 第 129 行**故意注入** ui 踩过的那个错误写法：

```diff
- BorderThickness="{StaticResource BorderHairline}"
+ BorderThickness="{StaticResource StrokeHairline}"
```

测试立刻变红，并**指名道姓**地报出位置与类型差异：

```
失败 XamlResourceInvariantTests.每个资源引用的类型与目标属性匹配
  有 1 处资源类型与属性不匹配（编译期不报，运行时报 ArgumentException）：
  DashboardPage.xaml  BorderThickness="{StaticResource StrokeHairline}"
      期望 Thickness，实际 Double
```

随后逐字节校验还原了该文件（24567 字节，与备份完全一致）。

#### 实验二：往检查器里注入错误映射（检查器侧）

把 `ExpectedTypes["BorderThickness"]` 从 `typeof(Thickness)` 临时改成
`typeof(double)`（**用 edit 工具改**，按团队新约定不动 PowerShell 字符串替换）。

正式测试立刻报出**准确的 19 处**，并逐页列出：

```
有 19 处资源类型与属性不匹配（编译期不报，运行时报 ArgumentException）：
  AccountsPage.xaml    BorderThickness="{StaticResource BorderHairline}"  期望 Double，实际 Thickness
  ChatPage.xaml        BorderThickness="{StaticResource BorderHairline}"  期望 Double，实际 Thickness
  DashboardPage.xaml   BorderThickness="{StaticResource BorderHairline}"  期望 Double，实际 Thickness
  LogsPage.xaml        BorderThickness="{StaticResource BorderHairline}"  期望 Double，实际 Thickness
  PoolsPage.xaml       BorderThickness="{StaticResource BorderHairline}"  期望 Double，实际 Thickness
  SettingsPage.xaml    BorderThickness="{StaticResource BorderHairline}"  期望 Double，实际 Thickness
```

**19 这个数与 ui 实际踩到的处数吻合** —— 这同时证明了两件事：
检查器真的在扫**全量**（六个页面都扫到了），而且数得准。
同一次注入还让两条自证用例连同变红（共 3 条红），随后用 edit 工具还原，171 全绿。

#### 常驻自证（不修改任何产品文件，每次跑测试都在证明）

`XamlResourceInvariantTests` 里有 **5 条常驻自证用例**，它们把**故意写错的数据**
喂给**正式测试用的同一个函数** `CheckTypeMatches`：

| 用例 | 证明什么 |
|---|---|
| `自证_类型检查能抓到BorderThickness吃到double` | ui 踩过的那个错法会被抓到，且报错信息含文件/属性/期望/实际 |
| `自证_类型检查能抓到Background吃到double` | Brush 位置吃到 double |
| `自证_类型检查能抓到Foreground吃到Thickness` | 反向错法（几何给画刷位置） |
| `自证_类型检查能抓到FontSize吃到Brush` | double 位置吃到 Brush |
| `自证_正确的引用不会被误报` | **反向保护**：7 条正确引用一条都不能被误报 —— 一个「见谁都报错」的检查器和「谁都不报错」一样没用 |

> ⚠ 关键设计：自证**不复刻检查逻辑**，而是调**生产用的同一个方法**。
> 复刻的话，证明的只是「我复刻的那份能抓到」，而不是「生产那份能抓到」。

另外还有两条防「扫描器失效」的自证：
`自证_扫描器确实扫到了足够多的引用`（断言扫到 >100 处、覆盖 ≥5 个文件）、
`自证_类型映射表覆盖了关键属性`（防止有人清空映射表来「让测试过」）。

### 6.2 已知缺陷（记录，未修 —— 不在 design 的改动范围内）

| 缺陷 | 现象 | 建议修法 |
|---|---|---|
| `RenameAccountAsync(null, …)` 抛异常 | `Uri.EscapeDataString(id)` 未拦空值，`ArgumentNullException` 穿透出 `IGatewayClient`，违反「永不抛」契约 | 入口判空返回 `Failure`，或用 `id ?? string.Empty` |

> 影响面小（界面上 id 来自列表选中项，正常不会是 null），但它是**契约的破口**。
> 测试 `GatewayClientNeverThrowsTests.已知缺陷_空id会让RenameAccountAsync抛异常`
> 以「当前会抛」的形式记录它 —— 修好那天这条会红，提醒改成「不抛」的断言。
> 用 `Skip` 会把缺陷藏起来，那等于没有检查。
