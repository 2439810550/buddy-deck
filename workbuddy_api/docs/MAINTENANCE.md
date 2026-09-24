# 维护手册

面向后续维护者（包括未来的自己）。本文档回答两个问题：
**上游变了怎么同步**，以及**怎么保证没改坏**。

---

## 0. 一分钟体检

```bash
cd D:\workcode\nodejs\workbuddy_api

node src/bin.js doctor        # 登录态 / 凭据格式 / 目录来源
node src/bin.js status        # 账号 + 积分 + 目录来源
node src/bin.js models        # 模型清单
node scripts/verify.mjs       # 端到端 39 项检查
```

`doctor` 一眼能看出的三件事：**凭据是不是加密的**、**key helper 能不能用**、
**目录是实时还是兜底**。任何异常先看它。

---

## 1. 与 DSH 插件同步（最主要的上游）

`dsh-workbuddy-connect` 是这条链路的参照实现——它先适配 WorkBuddy 的变化，
我们再跟进。插件更新后按下面流程同步。

### 1.1 确认版本差异

```powershell
$p = "C:\Users\2023060041\.dsh\profiles\web\node_modules\dsh-workbuddy-connect"
(Get-Content "$p\package.json" -Raw | ConvertFrom-Json).version
Get-ChildItem $p\lib | Select-Object Name, Length, LastWriteTime
```

记住上次同步的版本（记录在下方"同步历史"）。`lib\variants-*.js` 的哈希后缀会变，
不要硬编码文件名。

### 1.2 拉取新常量做对比

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

### 1.3 同步内置花名册

```bash
node -e "<把 cn / ai 写入 src/models.cn.json 与 src/models.ai.json>"
```

花名册是**离线兜底**，改动通常包括：模型增删、上下文窗口调整、
`maxOutputTokens` 调整、推理档位声明变化、计费倍率变化。

> **注意**：花名册里的 `billing` 会随促销变化（`rateUnknown` ↔ 具体倍率）。
> 这是正常的，照抄即可——运行时以实时目录为准。

### 1.4 逐项核对协议常量

用 grep 对比这些关键值（**这是同步的核心步骤**）：

```powershell
$p = "C:\Users\2023060041\.dsh\profiles\web\node_modules\dsh-workbuddy-connect\lib\variants-*.js"
Select-String -Path $p -Pattern 'CHAT_BASE|BILLING_BASE|GLOBAL_BASE|CLIENT_UA =|/v2/|/v3/|/console/|_FILENAME = |_ENV = |desktopFilename|ownFilename' |
  ForEach-Object { "$($_.LineNumber): $($_.Line.Trim())" }
```

对照 `src/variants.js` 与 `src/upstream.js` 检查：

- [ ] base URL 是否变化
- [ ] 目录端点与 UA 策略是否变化
- [ ] CLI UA 字符串是否变化
- [ ] 内置文件名 / 环境变量名是否变化
- [ ] 是否出现新的请求头或端点

### 1.5 检查凭据层

```powershell
Select-String -Path $p -Pattern 'wbEncrypted|AtRestKey|buildAuthenticatedContextAad|HELPER_SCRIPT|loggerGet|desktopAuthCandidates|xdgBase|dedupeCandidates'
```

若加密方案有变（新 `suite`、新框架、新算法），改
`src/desktop-credential-protection.js`。**先写临时脚本单独验证 helper**，
确认拿到 payload 再动解密逻辑：

```bash
ELECTRON_RUN_AS_NODE=1 "<WorkBuddy 二进制>" -e 'process.stdout.write(String(process._linkedBinding("electron_browser_workbuddy_storage").loggerGet()))'
```

### 1.6 跑验证

```bash
node scripts/verify.mjs
```

必须 **39 项全绿**。任何 FAIL 都意味着同步引入了回归。

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
   取 `UninstallString` 反推安装根。本机即 `D:\sqsoftware\workbuddy`——
   注意**装在 D 盘**，所以只查 `%LOCALAPPDATA%` 是不够的。
2. **常规根**：`%LOCALAPPDATA%\Programs\WorkBuddy`、
   `%PROGRAMFILES%\WorkBuddy` 等。

### 2.3 环境变量救急

| 变量 | 用途 |
|---|---|
| `WORKBUDDY_ELECTRON_BIN` | 直接指定 App 二进制（key helper） |
| `WORKBUDDY_AUTH_FILE` | 直接指定 CN 登录文件 |
| `WORKBUDDY_AI_AUTH_FILE` | 直接指定国际版登录文件 |
| `WORKBUDDY_API_HOME` | 改本工具状态目录（测试时隔离用） |

---

## 3. 回归验证

### 3.1 一键验证

```bash
node scripts/verify.mjs
```

它做这些事：
- 在**临时目录**起一个网关（不碰你的真实配置）
- 随机端口，避免和正在运行的服务冲突
- 覆盖：鉴权、路由守卫、目录、流式/非流式对话、工具调用、
  请求归一化、错误处理、凭据格式分类

**要求**：已登录的桌面 App + 网络。测试会真实消耗少量积分。

### 3.2 验证清单（手工补测）

`verify.mjs` 覆盖不到的，改动相关模块时手工补：

- [ ] **token 刷新**：`status` 连续跑两次，第二次应仍 signed in；
      改刷新逻辑后确认 App 的登录文件哈希**未变**
- [ ] **国际版**：`node src/bin.js status --provider workbuddy-ai`
      （需要装了国际版 App 才有意义）
- [ ] **账号切换**：在 App 里换账号，`status` 应立即跟随
- [ ] **`logout`**：只删 `auth.*.json`，不动 App 文件
- [ ] **非回环绑定**：`serve --host 0.0.0.0` 必须要求密钥
- [ ] **长对话 / 图片输入**：`glm-5.1` 是纯文本模型，发图应被上游拒

### 3.3 关键不变量（改代码时别破坏）

1. **App 的登录文件永远只读** —— 任何写入都是 bug。
   验证方式：改前后对比文件 sha256。
2. **状态隔离** —— 本工具只写 `~/.workbuddy-api`。
3. **`stream: false` 永不发给上游** —— 上游必拒。
4. **回环绑定 + Host 校验** —— 删任何一层都是安全回归。
5. **零第三方依赖** —— `package.json` 的 `dependencies` 必须保持为空。

---

## 4. 改动流程建议

```
1. 读 docs/ARCHITECTURE.md 找到对应模块
2. 读 docs/UPSTREAM.md 确认协议契约没理解错
3. 改代码（保持依赖方向单向）
4. node scripts/verify.mjs
5. 手工补测 3.2 里相关项
6. 更新本文件"同步历史" + 必要时更新 UPSTREAM.md
```

### 文档同步规则

| 改了什么 | 要更新哪个文档 |
|---|---|
| 新增/删除模块、改依赖方向 | `ARCHITECTURE.md` |
| 上游端点、请求头、错误码、凭据格式 | `UPSTREAM.md` |
| 同步流程、验证清单、环境变量 | 本文件 |
| 用户可见的命令、参数、接入方式 | `README.md` |

---

## 5. 同步历史

| 日期 | 插件版本 | 本工具版本 | 主要变化 |
|---|---|---|---|
| 2026-09-19 | 0.5.4 | 0.1.0 | 初始提取：认证 + 上游协议 + OpenAI 网关 |
| 2026-09-24 | 0.6.1 | 0.2.0 | 见下 |

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
- **Linux / WSL 未实测**：XDG 探测与 WSL 挂载路径照搬插件实现，本机无法验证。
- **macOS 未实测**：key helper 的默认路径
  （`/Applications/WorkBuddy.app/Contents/MacOS/Electron`）来自插件；
  Windows 上已验证用 `WorkBuddy.exe` 同样可行。
- **`nickname` 不解密**：5.6+ 里它是加密的，我们只解密 token 字段
  （见 `ARCHITECTURE.md` 的说明）。所以 `status` 在加密凭据下不显示昵称。
- **企业账号未实测**：本机是个人账号，企业计费分支依据插件实现编写。

---

## 7. 临时脚本约定

排查问题时可以在 `scripts/` 下写 `_` 前缀的临时脚本
（如 `scripts/_dbg.mjs`），**排查完删掉**，不要提交。
`scripts/verify.mjs` 是唯一的常驻脚本。

写临时脚本时记得：导入用相对路径 `../src/xxx.js`（脚本在 `scripts/` 下）。
