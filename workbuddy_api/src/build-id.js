/**
 * 代码指纹：让面板能判断「端口上那个网关是不是最新代码」。
 *
 * ## 为什么需要它
 *
 * 实际踩过的事故：
 *
 * ```
 * 用户改了网关代码（或我改的），但 8787 上那个网关是几小时前起的
 * 孤儿进程，加载的还是旧代码。面板点「启动网关」时看到端口上有
 * 人监听，就「采用」了它 —— 因为 /healthz 只回 {"ok":true}，
 * 分不出新旧。
 *
 * 表现：代码明明改了、提交了、重启面板了，功能却还是旧的。
 * 用户看到的是「视频还是生成不了」，排查方向完全跑偏。
 * ```
 *
 * ## 怎么算指纹
 *
 * 拿 `src/` 下所有 .js 文件的**修改时间 + 大小**，拼起来做个短哈希。
 *
 * 为什么不用 git commit：
 *   · 用户机器上可能没有 git
 *   · 也可能不是 git 仓库（解压安装的场景）
 *   · 而且「工作区有未提交改动」时 git 无法反映 —— 但代码确实变了
 *
 * 为什么不用文件内容哈希：
 *   · 每次启动都要读全部源码（几十 KB），没必要
 *   · mtime + size 已经足够区分「代码变了没有」
 *
 * 指纹变了 = 代码变了。指纹一样 = 大概率没变（同秒内改两次且大小
 * 相同才会漏判，实际不可能）。
 *
 * @module workbuddy-api/build-id
 */

import { readdirSync, statSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { createHash } from "node:crypto";

/** 本模块所在目录 = src/。 */
const SRC_DIR = dirname(fileURLToPath(import.meta.url));

/**
 * 算一次指纹。**只在启动时算一次**（模块加载时），之后是常量。
 *
 * 这样做的理由：指纹代表「这个进程启动时加载的代码」。
 * 如果每次请求都重算，代码改了之后指纹会变 —— 那反而失去了
 * 「这个进程加载的是哪份代码」的语义。
 */
function computeBuildId() {
  let entries;
  try {
    entries = readdirSync(SRC_DIR);
  } catch {
    return "unknown";
  }

  const parts = [];
  for (const name of entries.sort()) {
    if (!name.endsWith(".js")) continue;
    try {
      const st = statSync(join(SRC_DIR, name));
      parts.push(`${name}:${st.size}:${Math.floor(st.mtimeMs)}`);
    } catch {
      // 读不到就跳过 —— 指纹少算一个文件也比启动失败强
    }
  }

  if (parts.length === 0) return "unknown";
  return createHash("sha256").update(parts.join("|")).digest("hex").slice(0, 12);
}

/** 这个进程加载的代码指纹。6 个字符够区分，太长没人看。 */
export const BUILD_ID = computeBuildId();

/** 这个进程是什么时候启动的（ISO 字符串）。 */
export const STARTED_AT = new Date().toISOString();

/**
 * 当前磁盘上的代码指纹 —— **和 BUILD_ID 可能不一样**。
 *
 * 不一样就说明：代码在你鼻子底下改了，但这个进程还在跑旧的。
 * 这在开发时是常态（编辑文件但没重启服务）。
 */
export function currentBuildId() {
  return computeBuildId();
}

/** 磁盘上的代码和正在跑的是不是同一份。 */
export function isStale() {
  const now = currentBuildId();
  return now !== "unknown" && BUILD_ID !== "unknown" && now !== BUILD_ID;
}
