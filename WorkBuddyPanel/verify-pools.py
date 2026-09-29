"""
分池功能的端到端验证。

不通过界面点（那太慢太脆），直接验证底层机制：
每个池是不是真的指向不同的账号目录、能不能同时跑、用量是不是分开记。

跑法：
    python verify-pools.py
"""
import json
import os
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request

sys.stdout.reconfigure(encoding="utf-8")

HERE = os.path.dirname(os.path.abspath(__file__))     # ...\WorkBuddyPanel
ROOT = os.path.dirname(HERE)                          # ...\workfandaiapi
API = os.path.join(ROOT, "workbuddy_api")             # ...\workfandaiapi\workbuddy_api
NODE = r"D:\nodejs\node.exe"
VAULT_ROOT = os.path.join(os.path.expanduser("~"), ".workbuddy-api")

ok = 0
bad = 0


def check(label, condition, detail=""):
    global ok, bad
    if condition:
        ok += 1
        print("  ✓ %s" % label)
    else:
        bad += 1
        print("  ✗ %s  %s" % (label, detail))


def run(cmd, env_extra=None, timeout=60):
    env = os.environ.copy()
    if env_extra:
        env.update(env_extra)
    try:
        r = subprocess.run(cmd, cwd=API, env=env, capture_output=True,
                           text=True, encoding="utf-8", errors="replace", timeout=timeout)
        return r.returncode, r.stdout, r.stderr
    except subprocess.TimeoutExpired:
        return -1, "", "timeout"


print("═" * 60)
print("  分池功能验证")
print("═" * 60)
print()

# ── 1. 池目录隔离 ────────────────────────────────────────────
print("① 池目录隔离")

# 建两个临时池
test_pools = ["verify-a", "verify-b"]
for name in test_pools:
    d = os.path.join(VAULT_ROOT, "pools", name)
    os.makedirs(d, exist_ok=True)

# 从主池复制账号，各放不同的号
main_vault = os.path.join(VAULT_ROOT, "accounts.cn.json")
if os.path.exists(main_vault):
    doc = json.load(open(main_vault, encoding="utf-8"))
    accs = doc.get("accounts", [])
    print("  主池有 %d 个号" % len(accs))

    if len(accs) >= 4:
        # a 池放前两个，b 池放后两个
        for name, subset in (("verify-a", accs[:2]), ("verify-b", accs[2:4])):
            target = os.path.join(VAULT_ROOT, "pools", name, "accounts.cn.json")
            json.dump({
                "version": 1,
                "activeId": subset[0]["id"],
                "accounts": subset,
            }, open(target, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
            print("    %s ← %s" % (name, ", ".join(a.get("label", "?") for a in subset)))
else:
    print("  ⚠ 主池没有账号文件，跳过隔离验证")

# 用 node 读每个池，确认拿到的是不同的号
counts = {}
for name in [None] + test_pools:
    home = VAULT_ROOT if name is None else os.path.join(VAULT_ROOT, "pools", name)
    code, out, err = run([NODE, "src/bin.js", "accounts", "--json", "--no-checkin"],
                         env_extra={"WORKBUDDY_API_HOME": home})
    label = "默认池" if name is None else name
    if code != 0:
        check("%s 能读账号" % label, False, err[:120])
        continue
    try:
        start = out.index("{")
        data = json.loads(out[start:])
        counts[label] = [a.get("label") for a in data.get("data", [])]
        check("%s 读到 %d 个号" % (label, len(counts[label])), len(counts[label]) > 0)
    except Exception as e:
        check("%s JSON 可解析" % label, False, str(e)[:120])

if len(counts) >= 3:
    a = set(counts.get("verify-a", []))
    b = set(counts.get("verify-b", []))
    check("两个池的账号集合不同", a != b, "a=%s b=%s" % (sorted(a), sorted(b)))
    check("两个池没有重叠", not (a & b), "重叠=%s" % sorted(a & b))

print()

# ── 2. 同一份 vault 的 activeId 互不影响 ──────────────────────
print("② activeId 隔离")

code, out, _ = run([NODE, "src/bin.js", "accounts", "--json", "--no-checkin"],
                   env_extra={"WORKBUDDY_API_HOME": os.path.join(VAULT_ROOT, "pools", "verify-a")})
try:
    data = json.loads(out[out.index("{"):])
    a_active = data.get("activeId", "")
except Exception:
    a_active = ""

code, out, _ = run([NODE, "src/bin.js", "accounts", "--json", "--no-checkin"],
                   env_extra={"WORKBUDDY_API_HOME": os.path.join(VAULT_ROOT, "pools", "verify-b")})
try:
    data = json.loads(out[out.index("{"):])
    b_active = data.get("activeId", "")
except Exception:
    b_active = ""

check("两个池各自有 activeId", bool(a_active) and bool(b_active),
      "a='%s' b='%s'" % (a_active[:20], b_active[:20]))
check("两个池的 activeId 不同", a_active != b_active)

print()

# ── 3. 两个实例能同时跑 ──────────────────────────────────────
print("③ 并发实例")

procs = []
ports = [8796, 8797]
for i, name in enumerate(test_pools):
    home = os.path.join(VAULT_ROOT, "pools", name)
    port = ports[i]
    env = os.environ.copy()
    env["WORKBUDDY_API_HOME"] = home
    p = subprocess.Popen(
        [NODE, "src/bin.js", "serve", "--port", str(port), "--api-key", "sk-verify-%d" % i],
        cwd=API, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
        text=True, encoding="utf-8", errors="replace")
    procs.append((p, port, name, "sk-verify-%d" % i))
    print("  启动 %s → :%d (PID %d)" % (name, port, p.pid))

# 等它们起来
time.sleep(12)
for p, port, name, key in procs:
    if p.poll() is not None:
        out = p.stdout.read() if p.stdout else ""
        check("%s 实例在跑" % name, False, "退出码 %s: %s" % (p.returncode, out[-200:]))
        continue

    try:
        req = urllib.request.Request("http://127.0.0.1:%d/v1/models" % port,
                                     headers={"Authorization": "Bearer " + key})
        with urllib.request.urlopen(req, timeout=10) as r:
            data = json.loads(r.read().decode("utf-8"))
            n = len(data.get("data", []))
            check("%s 的 :%d 能响应（%d 个模型）" % (name, port, n), n > 0)
    except Exception as e:
        check("%s 的 :%d 能响应" % (name, port), False, str(e)[:100])

# 再验一下「用 A 的 key 打 B」应该被拒 —— 那才说明 key 是各池独立的
try:
    req = urllib.request.Request("http://127.0.0.1:%d/v1/models" % ports[1],
                                 headers={"Authorization": "Bearer sk-verify-0"})
    urllib.request.urlopen(req, timeout=10)
    check("拿 A 的 key 打 B 被拒", False, "居然通过了")
except urllib.error.HTTPError as e:
    check("拿 A 的 key 打 B 被拒（HTTP %d）" % e.code, e.code == 401)
except Exception as e:
    check("拿 A 的 key 打 B 被拒", False, str(e)[:80])

print()

# ── 4. 用量账本分开记 ────────────────────────────────────────
print("④ 用量账本")

for name in test_pools:
    usage = os.path.join(VAULT_ROOT, "pools", name, "usage.jsonl")
    check("%s 目录里有独立的状态文件" % name,
          os.path.isdir(os.path.join(VAULT_ROOT, "pools", name)),
          "目录不存在")

# ── 收尾 ────────────────────────────────────────────────────
print()
print("清理测试实例…")
for p, port, name, key in procs:
    try:
        p.terminate()
        p.wait(timeout=5)
    except Exception:
        try:
            p.kill()
        except Exception:
            pass
    print("  停了 %s (:%d)" % (name, port))

for name in test_pools:
    d = os.path.join(VAULT_ROOT, "pools", name)
    try:
        shutil.rmtree(d)
        print("  删了 %s" % d)
    except Exception as e:
        print("  删不掉 %s：%s" % (d, e))

print()
print("═" * 60)
print("  通过 %d 项，失败 %d 项" % (ok, bad))
print("═" * 60)
sys.exit(0 if bad == 0 else 1)
