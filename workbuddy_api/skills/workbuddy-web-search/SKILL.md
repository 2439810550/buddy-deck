---
name: workbuddy-web-search
description: "用 WorkBuddy 账号的联网能力做搜索与网页抓取（新闻、股价、版本号、最新发布等时效性信息）。走本机 workbuddy-api 网关的 /v1/search 与 /v1/webfetch，复用网关已有的登录态，无需自己配置 token；支持时效过滤（近 N 天/月/年）与域名限定/排除。当需要当前网络上的信息、或内置 web_search 不可用时使用。"
license: MIT
metadata:
  author: znaho
  version: "2.0.0"
---

# WorkBuddy Web Search

通过 **本机 `workbuddy-api` 网关**调用 WorkBuddy 的联网能力，让任何 CLI / 编辑器 /
harness 都能用上 WorkBuddy 账号的搜索与网页抓取额度。

```
你的 CLI / harness / 脚本
   │  POST http://127.0.0.1:8787/v1/search
   ▼
workbuddy-api 网关
   │  用网关自己的登录态（自动续期、自动跟随换号）
   ▼
WorkBuddy  /agenttool/v1/search  ·  /agenttool/v1/webfetch
```

**关键点：本 skill 不携带、也不需要任何 token。** 凭据由网关自己管理，
你只需要知道网关的地址和它的 api_key。

---

## 前置条件

1. 装好并登录过 WorkBuddy 桌面 App（网关据此拿凭据）
2. 启动网关：

   ```bash
   node src/bin.js serve
   # base_url : http://127.0.0.1:8787/v1
   # api_key  : <启动时打印>
   ```

3. 让本 skill 能拿到 api_key（三选一，见下）

### api_key 从哪来

网关默认**每次启动随机生成**密钥，所以最省事的做法是固定一个：

```bash
node src/bin.js config --set apiKey=sk-your-key
```

固定之后，本 skill 会**自动从网关的 `config.json` 读到它**，不用再配任何东西。

也可以显式指定（优先级高于配置文件）：

```powershell
# PowerShell
$env:WORKBUDDY_API_KEY   = "sk-your-key"
$env:WORKBUDDY_GATEWAY_URL = "http://127.0.0.1:8787"   # 非默认端口/地址时才需要
```

```bash
# bash
export WORKBUDDY_API_KEY=sk-your-key
```

解析顺序：`WORKBUDDY_GATEWAY_URL` / `WORKBUDDY_API_KEY` 环境变量 →
网关状态目录的 `config.json`（`host` / `port` / `apiKey`）→ 默认 `http://127.0.0.1:8787`。
状态目录默认 `~/.workbuddy-api`，可用 `WORKBUDDY_API_HOME` 覆盖。

---

## 用法

### 命令行

```sh
node scripts/wbsearch.mjs "DeepSeek V4 发布" --max 5
node scripts/wbsearch.mjs "AI 芯片" --freshness d7          # 近 7 天
node scripts/wbsearch.mjs "python asyncio" --sites docs.python.org
node scripts/wbsearch.mjs "某话题" --exclude csdn.net
node scripts/wbsearch.mjs "关键词" --json                    # 机器可读

node scripts/wbsearch.mjs --url https://example.com --prompt "总结要点"
node scripts/wbsearch.mjs --url https://example.com --json
```

输出默认是**给模型看的 markdown**（标题 / URL / 摘要 + 来源列表），可以直接
塞进上下文。

### 作为模块

```js
import { search, fetchPage } from './scripts/wbsearch.mjs';

const markdown = await search('关键词', { max: 5, freshness: 'd7' });
const page = await fetchPage('https://example.com', { prompt: '总结要点' });
```

### 直接用 HTTP（任何语言）

不想用 Node 也能接 —— 就是一个普通的 HTTP 调用：

```sh
curl http://127.0.0.1:8787/v1/search \
  -H "Authorization: Bearer $KEY" -H "Content-Type: application/json" \
  -d '{"query":"关键词","max_results":5,"freshness":"d7","include_domains":["docs.python.org"]}'
```

```sh
curl http://127.0.0.1:8787/v1/webfetch \
  -H "Authorization: Bearer $KEY" -H "Content-Type: application/json" \
  -d '{"url":"https://example.com","prompt":"总结要点"}'
```

---

## 参数

`POST /v1/search`：

| 字段 | 别名 | 说明 |
|---|---|---|
| `query` | `q` | **必填**，至少 2 个字符 |
| `max_results` | `maxResults` | `1`–`20`，默认 `5` |
| `freshness` | | `d1`–`d30` / `m1`–`m12` / `y1`–`y5`；单独写 `d` 等于 `d1` |
| `include_domains` | `includeDomains` / `sites` | 只要这些域名（**本地过滤**，见下） |
| `exclude_domains` | `excludeDomains` / `exclude` | 排除这些域名（折进 query 的 `-site:`） |
| `format` | | `markdown`（默认，回 `text/plain`）或 `json` |

数组字段同时接受数组与逗号分隔的字符串。数字字段同时接受数字与数字字符串。

`POST /v1/webfetch`：

| 字段 | 说明 |
|---|---|
| `url` | **必填**，http/https |
| `prompt` | 让上游按这个意图提取内容（有重点，不是整页倒出来） |
| `content_format` | 抓取格式，默认上游的 `markdown` |
| `timeout` | 上游抓取超时（秒），默认 `20` |
| `max_length` | `maxLength`，正文上限，默认 200000 |
| `format` | `markdown`（默认）或 `json`（含 title/truncated/statusCode 等） |

返回（`format: "json"` 时）：

```json
{
  "object": "search_result",
  "query": "关键词",
  "provider": "0",
  "totalResults": 5,
  "responseTimeMs": 3390,
  "results": [
    { "title": "...", "url": "https://...", "snippet": "...", "site": "..." }
  ]
}
```

---

## 两个必须知道的坑

1. **`include_domains` 是网关本地过滤的。** 上游的 `allowed_domains` 实测
   **不生效** —— 限定 `docs.python.org` 仍会返回百度、CSDN。所以网关在拿到
   结果后**自己按域名筛**（相等或子域，避免 `a.com` 误配 `nota.com`），
   并且有筛选时会把 `max_results` 拉满再筛，免得筛完不够数。
   `exclude_domains` 则沿用官方 CLI 的 `-site:` 语法折进 query。

2. **上游只需 `Content-Type` + `Authorization: Bearer`。** 官方 CLI 还会发
   `X-Agent-Intent`、`X-Conversation-ID`、`X-IDE-Type`、`X-User-Id`、自定义 UA
   等一堆 header —— 逐项实测**全部可省**，去掉仍是 200。但 `Bearer ` 前缀
   不可省。这些都在网关侧处理好了，对本 skill 不可见。

---

## 错误处理

失败时脚本以非零码退出，并把**下一步该做什么**写进 stderr：

| 现象 | 原因 | 处理 |
|---|---|---|
| `连不上本机网关` | 网关没起 | `node src/bin.js serve` |
| `网关拒绝了这次请求（401）` | api_key 不对/没带 | `config --set apiKey=...` 或设 `WORKBUDDY_API_KEY` |
| `workbuddy upstream ... 401` | WorkBuddy 登录态失效 | 在桌面 App 重新登录一次 |
| `freshness 格式非法` | 参数写错 | 用 `d7` / `m1` / `y1` 这类写法 |
| `联网请求超时` | 网络慢或上游慢 | 重试，或加大超时 |

**认证失败绝不静默降级**：拿不到结果就报错，不会返回空列表让你以为"没搜到"。

---

## 与旧版直连实现的区别

本 skill 的 v1 是**直连上游**的：自己读 `token.json`、自己打
`copilot.tencent.com`、token 过期就报错要你重新生成。v2 改成**走后端网关**：

| | v1（直连） | v2（后端网关，当前） |
|---|---|---|
| 凭据 | 自己维护 `token.json` | 网关的 `CredentialStore`，**无需配置** |
| token 过期 | 报错，要你重跑 auth | 网关自动续期 |
| 换账号 | 要重新生成 token | 自动跟随 |
| 凭据泄露面 | token 文件随 skill 摆着 | 只存在于网关状态目录 |
| 网页抓取 | 无 | **有**（`/v1/webfetch`） |
| 其他客户端 | 各接各的 | 同一个 base_url + api_key |

---

## 注意

- 驱动的是**你自己账号的额度**，请遵守 WorkBuddy 的服务条款。
- 客户端接口，非官方开放 API；WorkBuddy 更新后可能失效（此时先跑
  `node src/bin.js doctor`，再看 `docs/UPSTREAM.md`）。
- 网关默认只绑回环地址。**不要**为了远程使用把它暴露到公网 ——
  那等于把你的账号额度开放出去。
