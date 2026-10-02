# 维护手册

面向后续维护者（包括未来的自己）。本文档回答三个问题：
**哪些是上游的、哪些是自己的**（见 [`PATCHES.md`](./PATCHES.md)）、
**上游变了怎么同步**、**怎么保证没改坏**。

---

## 0. 一分钟体检

```bash
cd workbuddy_api

node src/bin.js doctor        # 登录态 / 凭据格式 / 二进制定位 / 目录来源
node src/bin.js status        # 账号 + 积分 + 目录来源
node src/bin.js models        # 模型清单
node scripts/verify.mjs       # 端到端检查（会消耗少量积分）
```

上面四条都打真上游但前三条只读。想完全不花钱，用 3.1 节的离线三件套。

`doctor` 一眼能看出的三件事：**凭据是不是加密的**、**key helper 能不能用**、
**目录是实时还是兜底**。任何异常先看它。

---

## 1. 与 DSH 插件同步（最主要的上游）

`dsh-workbuddy-connect` 是这条链路的参照实现——它先适配 WorkBuddy 的变化，
我们再跟进。插件更新后按下面流程同步。

> ⚠️ **动手前先读 [`PATCHES.md`](./PATCHES.md)**。它列出哪些文件是上游核心
> （要逐行对比）、哪些是本地独有 feature（同步时完全不用管）、以及本地在哪些
> 地方**故意**偏离了上游。**不要用上游目录覆盖 `workbuddy_api/`。**

### 1.1 确认版本差异

**推荐：直接克隆上游仓库**（源码可读、能看 commit log，比读混淆后的 `lib` 好得多）：

```powershell
$t = "$env:TEMP\wbc-upstream"
if (Test-Path $t) { Remove-Item -Recurse -Force $t }
git clone --quiet https://github.com/corrinehu/dsh-workbuddy-connect $t
cd $t
git log --oneline v0.6.1..v0.7.1          # 上次基线..本次目标
git diff --stat v0.6.1 v0.7.1 -- src      # 只看源码，lib/ 是构建产物
```

上游版本标签形如 `v0.7.1`；本工具的同步基线记在
[`PATCHES.md`](./PATCHES.md) 第 1 节和下方"同步历史"。

**备选：读本机已安装的插件**（只想知道"装的是哪版"时更快）：

```powershell
$p = "$env:USERPROFILE\.dsh\profiles\web\node_modules\dsh-workbuddy-connect"
(Get-Content "$p\package.json" -Raw | ConvertFrom-Json).version
```

注意 `lib\variants-*.js` 的哈希后缀会随构建变化，**不要硬编码文件名**。

### 1.2 拉取新常量做对比（只在读 `lib` 时需要）

插件的导出是**混淆别名**（`g`/`h` = 两个内置花名册等），别名会随构建变化。
先从导出行确认当前别名：

```powershell
Select-String -Path "$p\lib\variants-*.js" -Pattern '^export \{' | ForEach-Object { $_.Line }
```

然后取值对比（示例，别名以实际为准）：

```js
import { g as cn, h as ai, f as ver } from "file:///.../lib/variants-XXXX.js";
console.log(ver, cn.length, ai.length);
```

> 克隆了上游仓库的话这一步可以跳过——直接读 `src/catalog.ts` 里的
> `FALLBACK_WORKBUDDY_MODELS` / `FALLBACK_WORKBUDDY_AI_MODELS` 常量即可。

### 1.3 同步内置花名册

上游把花名册作为**常量写在 `src/catalog.ts`** 里
（`FALLBACK_WORKBUDDY_MODELS` / `FALLBACK_WORKBUDDY_AI_MODELS`），
不是 JSON。先把两边拉平成同样的形状再比，别靠肉眼看 TS：

```powershell
# 在克隆下来的上游仓库里，抽出 id / 窗口 / maxTokens / 倍率
$js = Get-Content src\catalog.ts -Raw
foreach ($name in @('FALLBACK_WORKBUDDY_MODELS','FALLBACK_WORKBUDDY_AI_MODELS')) {
  $block = [regex]::Match($js, "$name[^=]*=\s*\[(?s)(.*?)\n\]").Groups[1].Value
  [regex]::Matches($block, "id:\s*'([^']+)'") | ForEach-Object { $_.Groups[1].Value }
}
```

然后与 `src/models.cn.json` / `src/models.ai.json` 逐字段对比。

花名册是**离线兜底**，改动通常包括：模型增删、上下文窗口调整、
`maxOutputTokens` 调整、推理档位声明变化、计费倍率变化。

> **注意**：花名册里的 `billing` 会随促销变化（`rateUnknown` ↔ 具体倍率）。
> 这是正常的，照抄即可——运行时以实时目录为准。
>
> 上游目录**每天在变**（`space-bunny`、`auto`、`kimi-k3-1` 这类 id 会出现又消失），
> 所以内置花名册只是兜底。**如果对比发现只有实时目录才有的差异，不要急着改**——
> 先确认它是稳定成员还是当日波动。

### 1.4 逐项核对协议常量

**这是同步的核心步骤。** 在克隆下来的上游仓库里 grep 源码
（不要读本机 `lib/` 里的混淆构建产物）：

```powershell
cd $env:TEMP\wbc-upstream
Select-String -Path src\upstream.ts,src\variants.ts,src\client-identity.ts,src\app-version.ts `
  -Pattern 'CHAT_BASE|BILLING_BASE|GLOBAL_BASE|CLIENT_UA|X-IDE|X-Product|/v2/|/v3/|/console/|displayMsg|workbuddy-desktop' |
  ForEach-Object { "$($_.Filename):$($_.LineNumber): $($_.Line.Trim())" }
```

对照 `src/variants.js` 与 `src/upstream.js` 检查：

- [ ] base URL 是否变化
- [ ] 目录端点与 UA 策略是否变化
- [ ] CLI UA 字符串是否变化
- [ ] 内置文件名 / 环境变量名是否变化
- [ ] 是否出现新的请求头或端点（如 `X-IDE-*` 就是这样发现的）
- [ ] `classifyUpstreamError` 的分类规则是否变化（如裸 401 的处理）
- [ ] 错误文案提取规则是否变化（`displayMsg`）

> 逐项看改动的**语义**，不要只看有没有新常量。本轮最值钱的两项
> （裸 401、`displayMsg`）都是**逻辑**变化，不是常量变化，
> 只 grep 常量名会漏掉。

### 1.5 检查凭据层

```powershell
cd $env:TEMP\wbc-upstream
Select-String -Path src\desktop-credential-protection.ts,src\auth.ts `
  -Pattern 'wbEncrypted|AtRestKey|buildAuthenticatedContextAad|HELPER_SCRIPT|loggerGet|desktopAuthCandidates|xdgBase|dedupeCandidates|electronProfileFor|envVar' |
  ForEach-Object { "$($_.Filename):$($_.LineNumber): $($_.Line.Trim())" }
```

注意 `envVar` / `electronProfileFor` 这几项**只在本地是缺口**
（本地二进制定位是简化版，见 [`PATCHES.md`](./PATCHES.md) 3.3 / 6.1）——
把上游的值抄回来之前，先判断本地要不要真的做每产品定位。

若加密方案有变（新 `suite`、新框架、新算法），改
`src/desktop-credential-protection.js`。**先写临时脚本单独验证 helper**，
确认拿到 payload 再动解密逻辑：

```bash
ELECTRON_RUN_AS_NODE=1 "<WorkBuddy 二进制>" -e 'process.stdout.write(String(process._linkedBinding("electron_browser_workbuddy_storage").loggerGet()))'
```

### 1.6 跑验证

```bash
node scripts/test-agent.mjs    # 离线协议测试
node scripts/test-miora.mjs    # 离线画图测试
node scripts/test-usage.mjs    # 离线用量测试
node scripts/test-search.mjs   # 离线搜索/抓取测试
node src/bin.js doctor         # 登录态 / 凭据格式 / 二进制定位
node src/bin.js models         # 目录还能不能刷新
```

同步协议层时这几条足够，且**不消耗积分**。改了对话/计费主链路再加跑
`node scripts/verify.mjs`（会花钱）。任何 FAIL 都意味着同步引入了回归。

---

## 2. WorkBuddy App 自身更新

App 更新比插件更快，且**不需要等插件**——症状会直接出现在 `doctor` 里。

### 2.1 常见影响面

| App 侧变化 | 症状 | 处理 |
|---|---|---|
| 换登录文件位置 | `doctor` 说 missing | 更新 `desktopAuthCandidates`；临时用 `WORKBUDDY_AUTH_FILE` 顶一下 |
| 新的加密方案 | 解密失败 / signed out | `desktop-credential-protection.js` |
| 安装路径变化 | key helper 找不到 | Windows 已查注册表，一般自动适配；否则用 `WORKBUDDY_ELECTRON_BIN` |
| 版本号格式变化 | UA 构造抛错 | `client-identity.js` 的 `validAppVersion` |

### 2.2 Windows 定位二进制的两条路

1. **注册表**（首选）：`HKCU\...\Uninstall\*` 里 `DisplayName` 匹配 `WorkBuddy`，
   取 `UninstallString` 反推安装根。**装在哪个盘都能覆盖**（本机装在 `E:\asoftware\
   workbuddy`，就不是默认位置），所以只查 `%LOCALAPPDATA%` 是不够的。
2. **常规根**：`%LOCALAPPDATA%\Programs\WorkBuddy`、
   `%PROGRAMFILES%\WorkBuddy` 等。

> 本地的注册表正则 `/DisplayName\s+REG_SZ\s+WorkBuddy\b/u` **也会匹配国际版**
> 的 `WorkBuddy AI 5.6.2`。要支持两个版本共存前必须先修，见 `PATCHES.md` 6.1。

### 2.3 环境变量救急

| 变量 | 用途 |
|---|---|
| `WORKBUDDY_ELECTRON_BIN` | 直接指定 App 二进制（key helper） |
| `WORKBUDDY_AUTH_FILE` | 直接指定 CN 登录文件 |
| `WORKBUDDY_AI_AUTH_FILE` | 直接指定国际版登录文件 |
| `WORKBUDDY_API_HOME` | 改本工具状态目录（测试时隔离用） |

---

## 3. 回归验证

### 3.1 零成本验证（优先跑这个）

一次同步大多数时候只需要这几条——**不碰网络、不消耗积分、可重复**：

```bash
node scripts/test-agent.mjs     # 117 项协议测试（假上游）
node scripts/test-miora.mjs     # 27 项画图测试（假上游）
node scripts/test-usage.mjs     # 39 项用量账本测试
node scripts/test-search.mjs    # 34 项搜索/抓取测试（假上游 + 假网关）
node src/bin.js doctor          # 登录态 / 凭据格式 / 二进制定位
node src/bin.js models          # 目录刷新（只读，不花积分）
```

前四个启动的是**假上游**，改动协议解析、头部构造、错误分类、SSE 重组、
域名过滤时它们是最快的回归网。`doctor` / `models` 打真上游但只读，用来确认
"凭据还能解开、目录还能刷新"。

### 3.2 一键端到端验证（会花钱）

```bash
node scripts/verify.mjs
```

它做这些事：
- 在**临时目录**起一个网关（不碰你的真实配置）
- 随机端口，避免和正在运行的服务冲突
- 覆盖：鉴权、路由守卫、目录、流式/非流式对话、工具调用、
  请求归一化、错误处理、凭据格式分类

**要求**：已登录的桌面 App + 网络。测试会**真实消耗少量积分**——
改动了对话/计费主链路时再跑。

### 3.3 验证清单（手工补测）

脚本覆盖不到的，改动相关模块时手工补：

- [ ] **token 刷新**：`status` 连续跑两次，第二次应仍 signed in；
      改刷新逻辑后确认 App 的登录文件哈希**未变**
- [ ] **国际版**：`node src/bin.js status --provider workbuddy-ai`
      （需要装了国际版 App 才有意义）
- [ ] **账号切换**：在 App 里换账号，`status` 应立即跟随
- [ ] **`logout`**：只删 `auth.*.json`，不动 App 文件
- [ ] **非回环绑定**：`serve --host 0.0.0.0` 必须要求密钥
- [ ] **长对话 / 图片输入**：`glm-5.1` 是纯文本模型，发图应被上游拒

### 3.4 关键不变量（改代码时别破坏）

1. **App 的登录文件永远只读** —— 任何写入都是 bug。
   验证方式：改前后对比文件 sha256。
2. **状态隔离** —— 本工具只写 `~/.workbuddy-api`。
3. **`stream: false` 永不发给上游** —— 上游必拒。
4. **回环绑定 + Host 校验** —— 删任何一层都是安全回归。
5. **零第三方依赖** —— `package.json` 的 `dependencies` 必须保持为空。
6. **UA 与 `X-IDE-Version` 同源** —— 两者必须来自同一次身份解析，
   不允许各算各的（`chatIdentity()`）。
7. **`X-IDE-*` 只出现在对话/探测请求上** —— 刷新、目录、计费不带。

---

## 4. 改动流程建议

```
1. 读 docs/PATCHES.md 确认这块是上游核心还是本地 feature
2. 读 docs/ARCHITECTURE.md 找到对应模块
3. 读 docs/UPSTREAM.md 确认协议契约没理解错
4. 改代码（保持依赖方向单向）
5. 零成本验证：test-agent / test-miora / test-usage / test-search + doctor/models
6. 改了对话主链路再跑 node scripts/verify.mjs
7. 手工补测 3.3 里相关项
8. 更新"同步历史" + 必要时更新 UPSTREAM.md / PATCHES.md
```

### 文档同步规则

| 改了什么 | 要更新哪个文档 |
|---|---|
| 新增/删除模块、改依赖方向 | `ARCHITECTURE.md` |
| 上游端点、请求头、错误码、凭据格式 | `UPSTREAM.md` |
| 同步流程、验证清单、环境变量 | 本文件 |
| 用户可见的命令、参数、接入方式 | `README.md` |
| **本地改了上游语义 / 新增本地模块** | **`PATCHES.md`** |

> 最后一行是关键：**任何"故意与上游不同"的改动都要登记到 `PATCHES.md`**，
> 否则下次同步的人（可能就是未来的你）会把它当成"上游又变了"而覆盖掉。

---

## 5. 同步历史

| 日期 | 插件版本 | 本工具版本 | 主要变化 |
|---|---|---|---|
| 2026-09-19 | 0.5.4 | 0.1.0 | 初始提取：认证 + 上游协议 + OpenAI 网关 |
| 2026-09-24 | 0.6.1 | 0.2.0 | 见下 |
| 2026-10-08 | 0.7.1（协议部分） | 0.2.0 | 见下 |

### 0.6.1 → 0.7.1 同步内容（只同步协议层）

上游 0.6.1 到 0.7.1 有 40 个提交，但**大部分是 DSH 宿主专属**（插件更新提醒、
设置服务 0.2.0 适配、浏览器 UI、图片访问路径）。这些在独立工具里没有对应物，
见 [`PATCHES.md`](./PATCHES.md) 第 2.3 节。**本轮只落 3 项协议改动**：

1. **`X-IDE-Type/Name/Version` 归属头**（上游 `6059b71`，issue 54，
   **首发 v0.6.3**）——对话与探测请求新增。本地把 UA 与 `X-IDE-Version`
   收敛到**同一次**身份解析（新增 `WorkBuddyClient.chatIdentity()`），
   两者不允许各算各的。
2. **裸 401 → `session_dead`**（上游 `ad0306f`，PR #58，**首发 v0.6.4**）
   ——401 在这个上游没有业务含义，无条件判为会话失效；判断放在积分标记
   **之前**。本地原先会让裸 401 落到 `client`，对外错报成 HTTP 400。
3. **`extractDisplayErrorMessage()`**（同上）——错误文案优先取
   `displayMsg.zh` → `displayMsg.en` → `msg`，网关不再把整包 JSON 糊进错误里。

**顺手修掉一个本地移植 bug（上游没有这个问题）**

4. `client-identity.js` 的 `resolveAppVersion()` 返回字段名是 `version`，而
   `chatUserAgent()` 读 `identity.clientVersion` —— 后果是**已安装 App 的真实
   版本号一直被丢弃**，UA 恒退回编译常量 `5.5.6`（本机实装 5.6.2）。
   已统一为 `clientVersion`，并让 `chatIdentity()` 在 resolver 抛错或版本非法时
   退回 fallback，绝不让一条消息因读不到安装树而失败。

**未同步（有意，列为技术债）**

5. 每产品 Electron 定位（issue #59/#60）、Windows 注册表 asar 兼容（#66）、
   effort 探测完整状态机——理由与触发条件见 `PATCHES.md` 第 6 节。
6. 花名册**无需改动**：`models.cn.json` / `models.ai.json` 与上游 v0.7.1 的
   内置常量逐字段一致（16 + 20 行，含窗口、maxTokens、倍率）。

**本地刻意与上游不同的一点**

7. `gateway.js` 的 `upstreamErrorMessage()` **保留** `(http N)`；上游 `shim.ts`
   在 401/403 且非 `session_dead` 时会删掉它（因为 DSH 宿主按文本里的
   `401`/`403` 字样分类）。本网关按 `kind` 分类，不解析文本，所以这里保留数字
   是诊断信息。**同步时不要照抄那行 `statusNote`。**

### 0.5.4 → 0.6.1 同步内容

**协议变更（重要）**

1. **CN 目录端点迁移**：`/console/enterprises/personal/models` → `/v3/config`。
   两个 variant 现在读同一路径，**差异只在 UA**（CN 发 CLI UA 拿 CLI 花名册）。
   旧端点保留，但**仅用于读取促销标签**（best-effort，见下）。
2. **促销标签二次请求**：`/v3/config` 不携带 `badge:*` 标签，新增
   `fetchPromoBadges()` 从 Console 文档按 id 合并。
3. **`resolveUpstreamBilling` 接受 `extraTags`**，并对标签去重。
4. **`free` 判定改用归一化后的倍率**（`x0.00` 与 `x0.00 credits` 视为同一值）。

**凭据层变更（重要）**

5. **WorkBuddy 5.6+ 加密凭据**：`accessToken` / `refreshToken` 变成
   `{$wbEncrypted:1, envelope}`。新增 `desktop-credential-protection.js`
   处理 AES-256-GCM 解密，key 来自 App 自身二进制的 `workbuddyStorage` 绑定。
6. **Linux 登录文件探测支持 XDG**：`XDG_CONFIG_HOME` → `XDG_DATA_HOME`，
   并对候选去重。

**数据变更**

7. CN 花名册：移除 `auto`，新增 `minimax-m2.7`；多个模型的
   `maxOutputTokens` 与推理档位声明调整。

**本工具新增**

8. `doctor` 增加凭据格式与 key helper 诊断。
9. `verify.mjs` 增加凭据格式离线检查（39 项）。

---

## 6. 已知限制

- **国际版未实测**：本机只装了国内版。国际版代码路径（App UA、`/v3/config`
  App 分流、企业端点区域门）依据插件实现与文档编写，但**没有真实账号验证过**。
  有国际版账号时优先补测 `status --provider workbuddy-ai`。
- **二进制定位是简化版，且可能跨产品误匹配**：本地只认
  `WORKBUDDY_ELECTRON_BIN`、且 Windows 注册表正则
  `/DisplayName\s+REG_SZ\s+WorkBuddy\b/u` 也会匹配国际版的
  `WorkBuddy AI 5.6.2`。**同时装两个版本的 Windows 机器上，国际版可能解析到
  国内版的二进制。** 详见 `UPSTREAM.md` 与 `PATCHES.md` 第 6.1 节。
- **Linux / WSL 未实测**：XDG 探测与 WSL 挂载路径照搬插件实现，本机无法验证。
- **macOS 未实测**：key helper 的默认路径
  （`/Applications/WorkBuddy.app/Contents/MacOS/Electron`）来自插件；
  Windows 上已验证用 `WorkBuddy.exe` 同样可行。
- **`nickname` 不解密**：5.6+ 里它是加密的，我们只解密 token 字段
  （见 `ARCHITECTURE.md` 的说明）。所以 `status` 在加密凭据下不显示昵称。
- **企业账号未实测**：本机是个人账号，企业计费分支依据插件实现编写。
- **effort 探测只有原语**：`probeEffort()` 是单次请求，没有上游
  `probeModel()` 那样的扫描状态机，也没有按区域区分拒绝码。见 `PATCHES.md` 6.3。

---

## 7. 临时脚本约定

排查问题时可以在 `scripts/` 下写 `_` 前缀的临时脚本
（如 `scripts/_dbg.mjs`），**排查完删掉**，不要提交。
常驻脚本只有 `scripts/verify.mjs` 与 `scripts/test-*.mjs`。

写临时脚本时记得：导入用相对路径 `../src/xxx.js`（脚本在 `scripts/` 下）。
