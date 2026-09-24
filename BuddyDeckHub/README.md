# BuddyDeck Hub

把本机多个 **workbuddy_api** 实例聚合成一个多租户中转站 —— 给你认识的几个人发 key，各自用自己的额度，你随时能停。

设计参考 **one-api / new-api** 那套：成员 → 令牌 → 分组 → 渠道。

> **这个项目不改动 `workbuddy_api` 一行代码。** 它把网关当成一个黑盒（渠道），
> 靠 `WORKBUDDY_API_HOME` 隔离出多个独立实例。详见下面「为什么不改网关」。

---

## 它解决什么

```
之前的状况                          现在
─────────────────────────────      ─────────────────────────────
网关只给一个人用，key 是全局的       每人一把 key，谁用多少一目了然
谁在用、用了多少，不知道            每次调用都有流水，能查到人
有人跑批量把额度打光，只能事后发现    每分钟限速 + 每日配额，提前拦住
出问题要翻网关日志                  管理界面直接看渠道健康和调用记录
```

---

## 快速开始

```bash
cd D:\workfandaiapi\BuddyDeckHub
dotnet run -c Release
```

首次启动会：
1. 在 `%APPDATA%\BuddyDeckHub\` 建 `config.json` 和 `usage.db`
2. **打印一个管理密钥**（形如 `adm-xxxxx`）—— **记下来，后面都要用**
3. 在 `http://127.0.0.1:8890` 监听

```bash
# 换数据目录
BUDDYDECK_HUB_HOME=D:\hub-data dotnet run -c Release

# 换监听地址（改 config.json 也行）
dotnet run -c Release -- --urls http://127.0.0.1:9000
```

---

## 配起来（五步）

管理接口**只允许从本机访问**，所以下面的 `curl` 都得在跑 Hub 的这台机器上执行。

```bash
ADMIN=adm-你的管理密钥
```

### ① 加渠道 —— 指向一个 workbuddy_api 实例

```bash
curl -X POST http://127.0.0.1:8890/admin/channels \
  -H "X-Admin-Key: $ADMIN" -H "Content-Type: application/json" \
  -d '{
    "id": "pool-a",
    "name": "账号池 A",
    "baseUrl": "http://127.0.0.1:8787",
    "apiKey": "sk-workbuddy-local",
    "priority": 10
  }'
```

**一个渠道 = 一个账号池。** 想让 10 个号分成两组轮流用，就起两个实例：

```bash
# 实例 A：前 5 个号
WORKBUDDY_API_HOME=~/.workbuddy-api/a node src/bin.js serve --port 8787 --api-key sk-a

# 实例 B：后 5 个号
WORKBUDDY_API_HOME=~/.workbuddy-api/b node src/bin.js serve --port 8788 --api-key sk-b
```

然后都加成渠道。Hub 会按「最少连接优先」自动分流 —— 不会一个渠道忙死、另一个闲着。

> 如果渠道配了 `nodePath` / `scriptDir` / `dataHome`，**Hub 会自动把实例拉起来**，
> 挂了也会自动重启。不配就当作是你在手动跑。

### ② 探测渠道

```bash
curl -X POST http://127.0.0.1:8890/admin/channels/pool-a/probe \
  -H "X-Admin-Key: $ADMIN"
# {"ok":true,"models":16}
```

### ③ 加成员

```bash
# 免费组：只能用免费模型，不扣配额
curl -X POST http://127.0.0.1:8890/admin/members \
  -H "X-Admin-Key: $ADMIN" -H "Content-Type: application/json" \
  -d '{"name":"张三","note":"老王介绍","group":"free"}'

# 默认组：全部模型，按用量记账
curl -X POST http://127.0.0.1:8890/admin/members \
  -H "X-Admin-Key: $ADMIN" -H "Content-Type: application/json" \
  -d '{"name":"李四","group":"default","dailyQuota":5000000}'
```

返回里有 `id`。

### ④ 发 key

```bash
curl -X POST http://127.0.0.1:8890/admin/members/<成员id>/tokens \
  -H "X-Admin-Key: $ADMIN" -H "Content-Type: application/json" \
  -d '{"name":"主力"}'
```

```json
{
  "id": "a1b2c3",
  "prefix": "bd-7We4-R0",
  "key": "bd-7We4-R0HWSKieaeozLDOedGocW5R842itbIsBps6v2g",
  "warning": "这个 key 只显示这一次，请立刻复制保存。库里只存了哈希，丢了无法找回。"
}
```

**把这把 key 发给对方就完事了。** 让对方把客户端指向：

```
Base URL:  http://<你的局域网IP>:8890/v1
API Key:   bd-7We4-R0HWSKieaeozLDOedGocW5R842itbIsBps6v2g
```

### ⑤ 看谁在用

```bash
curl http://127.0.0.1:8890/admin/overview -H "X-Admin-Key: $ADMIN"
curl "http://127.0.0.1:8890/admin/calls?limit=100" -H "X-Admin-Key: $ADMIN"
```

---

## 三种协议都支持

Hub 是**透明代理**，客户端用什么协议就转什么协议：

| 协议 | 端点 | 谁在用 |
|---|---|---|
| OpenAI Chat | `POST /v1/chat/completions` | pi、Cline、Aider、任何 OpenAI 客户端 |
| Anthropic Messages | `POST /v1/messages` | Claude Code |
| OpenAI Responses | `POST /v1/responses` | Codex |
| 模型列表 | `GET /v1/models` | 全部 |

`/v1/models` 由 Hub 自己回答 —— 它知道**这个人的分组能看哪些模型**，还会标出哪些免费。

```json
{ "id": "deepseek-v4.1-flash", "object": "model",
  "owned_by": "buddydeck", "free": true,
  "rate": { "input": 0.0, "output": 0.0, "note": "免费" } }
```

---

## 计费

照抄 one-api 的**倍率层**：

```
消耗配额 = 输入token × 输入倍率 + 输出token × 输出倍率
        再乘分组倍率
```

**为什么要插这一层**（而不是直接用 token 数）：

1. 换计量口径不用动账本
2. 不同模型天然差价 —— `deepseek-v4-flash` 有 100 万上下文，和 `glm-4.6` 的 168K 不是一个量级
3. **免费模型填倍率 0 就完事**，不用到处写特例判断

标定基准：`deepseek-v4.1-flash` = 1.0（它是默认模型、也是最便宜那档）。

```
免费（倍率 0，不扣配额）    deepseek-v4.1-flash, glm-5.3-flash
轻量                       deepseek-v3-*, glm-4.6/4.7, hunyuan-*
标准                       glm-5.0/5.1（基准 1.0）
强                        glm-5.3, hy3, kimi-k2.x
旗舰                       deepseek-v4-pro, kimi-k3, minimax-m3
```

完整表在 `src/QuotaCalculator.cs`，改倍率只要动那个数组。

### 预扣 + 结算

流式请求开始时不知道会产出多少。做法和中转站一致：

```
① 预扣   按 max_tokens（或估算）先冻结
② 转发   真流式，不缓冲
③ 结算   从流末尾的 usage 拿真值，差额退还
④ 失败   预扣全退
```

### 实测

```
glm-5.3（倍率 2.0 / 4.0）
  上游 usage:  in=14  out=128
  期望配额:    14 × 2.0 + 128 × 4.0 = 540
  实际:        540  ✓
```

---

## 配额的三层闸门

| 层 | 作用 | 超了会怎样 |
|---|---|---|
| **每日配额** | 每人每天能用多少 | 429，错误信息里写明重置时间，并提示免费模型现在就能用 |
| **每分钟限速** | 防一个人跑批量 | 429 |
| **渠道健康** | 某个号被限流就摘掉它 | 自动换渠道，用户无感 |

**免费模型绕开第一层** —— 倍率 0 的东西不该扣配额。这是「免费组」能成立的关键。

实测拦截效果：

```json
{"error":{
  "message":"今日配额已用完（540 / 100）。北京时间 09-25 00:00 重置。
             提示：免费模型（deepseek-v4.1-flash、glm-5.3-flash）不消耗配额，现在就能用。",
  "type":"quota_exceeded","code":"insufficient_quota"}}
```

---

## 渠道故障自动切换

```
用户请求
   ↓
按分组筛出可用渠道
   ↓
按优先级排序，同级按在途请求数（最少连接优先）
   ↓
依次尝试：
   渠道 A → 连不上   → 冷却，换下一个
   渠道 B → 429 限流 → 冷却 5 分钟，换下一个
   渠道 C → 200      → 成功
```

**错误分类决定怎么处理**：

| 上游状况 | 换渠道？ | 冷却 |
|---|---|---|
| 429 / `6004` / `10081`（限流） | ✅ | 5 分钟 |
| 402 / `14018`（额度耗尽） | ✅ | 30 分钟 |
| 401 / 403（token 过期） | ✅ | 10 分钟 |
| 5xx | ✅ | 指数退避 |
| **400**（客户端请求写错了） | ❌ **不换** | — |

最后一条很重要：**请求本身有问题的话，换谁都一样。** 直接返回上游的真实错误，比 Hub 编一个有用，也不会白白消耗其它账号的信任度。

实测：

```
坏渠道优先级 1，好渠道优先级 10
  第一次请求 → 3.58s（试坏的超时后切换），返回正常内容
  第二次请求 → 1.53s（坏渠道在冷却中，直接跳过）
```

---

## 为什么必须真流式

Claude Code 的一次长回答会挂几十秒。**如果代理把响应体缓冲下来，客户端会一直收不到数据，到超时为止。**

```csharp
// 必须先拿响应头，再手动逐块搬
var resp = await httpClient.SendAsync(req,
    HttpCompletionOption.ResponseHeadersRead, ct);

// 而且不能设 AutomaticDecompression —— 那会引入缓冲
new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None }
```

实测流式分片的到达节奏（每个字都是独立到达的）：

```
0 ''   1 '1'   2 ','   3 ' '   4 '2'   5 ','   6 ' '   7 '3' ...
```

**重试只能在流开始前。** 一旦开始往客户端写字节就没法换渠道了 —— 那会变成两个回答拼接。所以重试循环在第一个字节到达之前就完成了判断。

---

## usage 怎么拿到

上游**没有余额查询接口**（`/v2/billing/meter/balance` 之类全是 404），也不告诉你扣了多少。唯一可靠的办法是读每次响应里的 usage，自己记账。

三种协议的字段名不同，而且都有坑：

| 协议 | 输入 | 输出 | 坑 |
|---|---|---|---|
| OpenAI Chat | `prompt_tokens` | `completion_tokens` | 只在最后一个 chunk 出现 |
| Anthropic | `input_tokens` | `output_tokens` | **`message_start` 里恒为 0**，真值在 `message_delta` |
| Responses | `input_tokens` | `output_tokens` | 事件结构不同 |

`UsageSniffer` 用正则扫（SSE 分片经常不是完整 JSON），**关键是用 `Matches()` 遍历而不是 `Match()` 取第一个** —— 否则会拿到 `message_start` 里那个 0，永远记不到真实用量。

> 这个 bug 是拿真实 SSE 回放测出来的，`Match()` 只返回首个匹配。

流式请求还会自动补上 `stream_options.include_usage`，否则客户端不传、上游也不报，Hub 就没法记账。

---

## 为什么不改网关

`workbuddy_api` 里有这么一段（`src/gateway.js`）：

```js
// A non-loopback Host means a DNS-rebinding page or a remote caller: the
// bearer alone is not enough for a local trust boundary.
const host = req.headers.host;
if (host === undefined || !LOOPBACK_HOSTS.has(hostnameOfHost(host))) {
  writeOpenAIError(res, 403, "host_not_allowed",
    "Host header must name the loopback interface");
  return;
}
```

实测：`--host 0.0.0.0` 能启动，但从局域网 IP 访问一律 **403**（带对 key 也是 403）。

**这不是 bug，是作者刻意设的安全边界。** 理由：

1. **它是别人的代码** —— 保持纯净才能继续同步上游更新
2. **这个边界是对的** —— 真正的公网入口该由反代那层负责。把 Host 校验拆掉去"支持公网"，等于把一个个人工具改成不该它承担的暴露面
3. **网关天然支持多实例** —— `WORKBUDDY_API_HOME` 一设就隔离，Hub 只要拉起 N 个进程

所以 Hub 转发时**显式把 Host 写回环**：

```csharp
req.Headers.Host = new Uri(channel.BaseUrl).Authority;
```

---

## 安全

| 项 | 做法 |
|---|---|
| **令牌存储** | PBKDF2-HMAC-SHA256，迭代 21 万次。**只存哈希，明文只显示一次** |
| **令牌缓存** | 缓存键是令牌原文的 SHA256，不存明文 —— 内存转储也捞不到 |
| **管理接口** | **只允许回环访问**。和网关一个道理，管理面不该出现在公网入口 |
| **默认监听** | `127.0.0.1:8890`。对外由反代负责 |
| **IP 白名单** | 支持单 IP 和 CIDR |
| **Host 校验** | Hub 自己不做（那是反代的事），但转发给上游时写回环 |

---

## 对外怎么开

Hub 默认只绑回环，公网要自己加一层：

```
        客户端
          │  https://ai.你的域名
          ▼
   ┌──────────────┐
   │ Caddy / CF   │  TLS + 基础限流
   │ Tunnel       │
   └──────┬───────┘
          │ 隧道到本机
          ▼
   BuddyDeckHub  :8890（只绑回环）
```

**别把 8890 直接暴露出去** —— 管理接口虽然只认回环，但 Hub 少了反代那层的 TLS、限流和访问控制。

Caddy 配置就两行：

```caddy
ai.你的域名 {
    reverse_proxy 127.0.0.1:8890 {
        flush_interval -1      # 关键：关掉反代的响应缓冲，否则流式会卡
    }
}
```

`flush_interval -1` 不能漏 —— 反代默认会攒一批数据再发，流式就废了。

---

## 管理接口一览

全部需要 `X-Admin-Key` 头，且**只接受本机来源**。

| 方法 | 路径 | 作用 |
|---|---|---|
| GET | `/admin/overview` | 成员、渠道、用量、倍率表 |
| GET | `/admin/calls?limit=N` | 调用流水 |
| POST | `/admin/members` | 加成员 |
| PATCH | `/admin/members/{id}` | 改成员（名字/分组/配额/启用） |
| DELETE | `/admin/members/{id}` | 删成员 |
| POST | `/admin/members/{id}/tokens` | 发 key（明文只返回一次） |
| DELETE | `/admin/members/{id}/tokens/{tid}` | 撤销 key |
| POST | `/admin/tokens/{tid}/toggle` | 启用/停用 key |
| POST | `/admin/members/{id}/reset-usage` | 清空今日用量 |
| POST | `/admin/channels` | 加渠道 |
| PATCH | `/admin/channels/{id}` | 改渠道 |
| DELETE | `/admin/channels/{id}` | 删渠道 |
| POST | `/admin/channels/{id}/probe` | 探测渠道可用性 |
| POST | `/admin/groups` | 加分组 |
| PATCH | `/admin/groups/{id}` | 改分组 |

---

## 数据存在哪

```
%APPDATA%\BuddyDeckHub\
├── config.json     渠道 / 成员 / 分组 / 管理密钥。人手改得动
└── usage.db        逐条调用日志 + 每日用量。SQLite，WAL 模式
```

**分两份存是有意的**：配置要能直接看、直接改、直接备份，JSON 比 SQLite 舒服；日志反过来，量大且要聚合查询，必须 SQLite。

表结构：

```sql
calls       逐条流水：时间/成员/令牌/渠道/模型/token数/配额/延迟/状态
daily_usage 每日汇总：配额/次数/输入输出 token（主键 day + member_id）
```

---

## 已知限制

- **没有 Web 界面** —— 现在全靠 curl。界面建议加到现有的 WPF 面板里（新标签页）
- **渠道健康检查只在请求失败时被动更新** —— 没有主动探针定时跑。一个闲置渠道即使挂了，也要等下次有人用才发现
- **记账是「事后」的** —— 上游不报 usage 时用字符数估算（约 3 字符/token），会有偏差
- **跨机器部署没验证过** —— `ChannelSupervisor` 只在本机拉起实例

---

## 项目结构

```
BuddyDeckHub/
├── BuddyDeckHub.csproj     唯一依赖 Microsoft.Data.Sqlite
├── Program.cs              路由 + 管理接口
└── src/
    ├── Models.cs              渠道 / 成员 / 令牌 / 分组
    ├── HubStore.cs            config.json + usage.db
    ├── TokenHasher.cs         PBKDF2 令牌哈希
    ├── AuthService.cs         认证（带 5 分钟缓存）
    ├── QuotaCalculator.cs     倍率表 + 预扣结算
    ├── ChannelRouter.cs       选渠道 + 健康状态 + 错误分类
    ├── ChannelSupervisor.cs   自动拉起网关实例
    ├── UsageSniffer.cs        从流里抠 usage
    └── ProxyService.cs        代理核心（SSE 真流式）
```

---

## 验证过的行为

全部实测，不是推断：

- ✅ 三种协议（Chat / Anthropic / Responses）都返回 200，流式逐字到达
- ✅ usage 抽取：三种协议都对得上上游
- ✅ 计费：`glm-5.3` 的 `14×2.0 + 128×4.0 = 540` 分毫不差
- ✅ 配额拦截：收费模型 429，免费模型放行
- ✅ 令牌认证：错 key / 无 key 都是 401
- ✅ 故障切换：坏渠道超时后自动切好渠道（3.58s），冷却后直接跳过（1.53s）
- ✅ 渠道探测：正确返回 16 个模型

---

## 故障排除

**`/v1/models` 返回 500？**
检查渠道的 `baseUrl`。Hub 会在渠道不通时跳过，但如果所有渠道都不通就没得返回了。

**中文请求体报 400？**
用 curl 时中文要用 `--data-binary @文件`，别用 `-d`（shell 的编码会坏掉）。

**端口被占？**
```bash
netstat -ano | grep ":8890.*LISTENING"
```
不要杀 `publish\` 下的 WorkBuddyPanel —— 那是用户正在用的面板。

**管理密钥忘了？**
它在 `config.json` 里的 `adminKey` 字段。

**流式卡住不出字？**
反代少了 `flush_interval -1`。
