#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""盘点团队未完成的任务，供 captain 做收口汇报。"""

import json
import pathlib

TEAM = pathlib.Path(r"D:\workfandaiapi\_next\.agent-teams\buddydeck-next\team.json")
OUT = pathlib.Path(r"D:\workfandaiapi\_next\.local\todo.txt")

d = json.loads(TEAM.read_text(encoding="utf-8"))
tasks = d.get("tasks", [])
members = {m.get("name"): m.get("role", "") for m in d.get("members", [])}

DONE = {"completed", "cancelled"}
todo = [t for t in tasks if t.get("status") not in DONE]

lines = []
lines.append(f"全部任务: {len(tasks)}    已完成/取消: {len(tasks) - len(todo)}    未完成: {len(todo)}")
lines.append("")
lines.append("=== 成员 ===")
for name, role in members.items():
    owned = [t for t in todo if t.get("assignee") == name]
    lines.append(f"  {name:14} {role}   未完成 {len(owned)} 条")
lines.append("")
lines.append("=== 未完成任务 ===")
for t in sorted(todo, key=lambda x: x.get("id", "")):
    deps = t.get("dependencies") or []
    dep = f"  deps={deps}" if deps else ""
    lines.append(f"  {t.get('id'):5} [{t.get('status'):12}] {t.get('subject','')[:70]}")
    lines.append(f"        归属: {t.get('assignee','(共享池)')}{dep}")

OUT.write_text("\n".join(lines), encoding="utf-8")
print("written", OUT)
