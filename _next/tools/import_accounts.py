#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
把旧版 workbuddy 账号库（accounts.cn.json）导入 BuddyDeck 的 Vault 格式。

用法：
  python tools/import_accounts.py                     # 列出账号，不写文件
  python tools/import_accounts.py --label 简单快乐     # 只导出匹配标签的账号
  python tools/import_accounts.py --label 简单快乐 --out .local/vault.json

为什么需要它：
  旧版把账号存在 ~/.workbuddy-api/accounts.cn.json（明文 JSON），
  新版读自己的 Vault 文档。两者字段几乎一一对应，但**标签编码不同** ——
  见下面 fix_label() 的说明。
"""

import argparse
import json
import os
import sys
from pathlib import Path

# 旧版默认位置
LEGACY = Path.home() / ".workbuddy-api" / "accounts.cn.json"


def fix_label(raw: str) -> str:
    """
    旧版面板是 C# WPF，历史上把 UTF-8 中文按 GBK 解过一次再存回去，
    结果是「双重编码」的乱码，例如：
        '简单快乐'  ->  '绠€鍗曞揩涔?'
    还原方式：把当前字符串按 cp1252/latin-1 编回字节，再按 GBK 解码。

    如果还原失败（说明它本来就是好的），原样返回。
    """
    if not raw:
        return raw
    for enc in ("latin-1", "cp1252"):
        try:
            return raw.encode(enc).decode("gbk")
        except (UnicodeEncodeError, UnicodeDecodeError):
            continue
    return raw


def looks_mojibake(s: str) -> bool:
    """常见 GBK 误读特征：出现了大量 CJK 扩展/生僻字且含 € 等符号。"""
    if not s:
        return False
    markers = ("锛", "€", "鍗", "绠", "鏄", "鐢", "涓", "榛", "璁")
    return any(m in s for m in markers)


def to_vault_entry(acc: dict) -> dict:
    """旧版 account -> 新版 VaultEntry。字段名基本一致，逐个显式映射。"""
    label = acc.get("label", "")
    if looks_mojibake(label):
        label = fix_label(label)

    entry = {
        "id": acc.get("id", ""),
        "uid": acc.get("uid", ""),
        "enterpriseId": acc.get("enterpriseId", "") or "",
        "domain": acc.get("domain", ""),
        "accessToken": acc.get("accessToken", ""),
        "refreshToken": acc.get("refreshToken", ""),
        "expiresAtMs": acc.get("expiresAtMs", 0),
        "label": label,
    }
    # 可选字段：有才带，避免写进一堆 null
    for opt in ("refreshExpiresAtMs", "addedAt", "lastSeenAt", "lastCheckinAt"):
        if acc.get(opt) not in (None, ""):
            entry[opt] = acc[opt]
    return entry


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", default=str(LEGACY), help="旧版账号库路径")
    ap.add_argument("--label", default=None, help="按标签筛选（支持子串匹配）")
    ap.add_argument("--out", default=None, help="输出 Vault 文件路径；不填则只列出")
    args = ap.parse_args()

    src = Path(args.src)
    if not src.exists():
        print(f"找不到旧版账号库: {src}", file=sys.stderr)
        return 1

    raw = src.read_text(encoding="utf-8")
    doc = json.loads(raw)
    accounts = doc.get("accounts", [])
    print(f"旧版账号库: {src}")
    print(f"账号总数  : {len(accounts)}\n")

    # 全部列出（标签还原后）
    for i, acc in enumerate(accounts, 1):
        label = acc.get("label", "")
        fixed = fix_label(label) if looks_mojibake(label) else label
        flag = "  <-- 已还原" if fixed != label else ""
        print(f"  {i:2}. {acc.get('uid','?')[:8]}  {fixed}{flag}")

    if not args.label:
        print("\n（未指定 --label，未写文件）")
        return 0

    # 只按【还原后】的标签做子串匹配。
    # 早先版本这里还写了一条 `args.label in raw`（在原始 JSON 文本里找），
    # 那是错的：原始文本里的标签是乱码，中文关键词要么永远不命中、
    # 要么在别的字段里偶然命中，两种都会给出莫名其妙的结果。
    picked = []
    for acc in accounts:
        label = acc.get("label", "")
        fixed = fix_label(label) if looks_mojibake(label) else label
        if args.label in fixed:
            picked.append(to_vault_entry(acc))

    if not picked:
        print(f"\n没有标签匹配 '{args.label}' 的账号", file=sys.stderr)
        return 2

    vault = {
        "version": 1,
        "activeId": picked[0]["id"],
        "accounts": picked,
    }

    if not args.out:
        print(f"\n匹配到 {len(picked)} 个账号（未写文件）：")
        print(json.dumps(vault, ensure_ascii=False, indent=2)[:1200])
        return 0

    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    # 关键：ensure_ascii=False + UTF-8 无 BOM，中文才不会被转成 \uXXXX
    out.write_text(json.dumps(vault, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"\n已写出 {len(picked)} 个账号 -> {out}")
    print(f"文件大小 {out.stat().st_size} 字节")
    return 0


if __name__ == "__main__":
    sys.exit(main())
