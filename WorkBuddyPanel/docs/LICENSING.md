# 授权发卡

给程序加机器码绑定授权 —— 授权文件对了才能用。

---

## 一分钟上手

```bash
cd WorkBuddyPanel

# ① 生成密钥对（只做一次）
dotnet run --project tools/make-license -- --gen
#    → 口令自己设一个，记住它
#    → 把输出的公钥贴进 Services/LicenseGuard.cs 的 PublicKey 常量

# ② 给用户签授权（拿到他的机器码后）
dotnet run --project tools/make-license -- --issue A1B2C3D4E5F60718 --to 张三 --days 365
#    → 产出 license-A1B2C3D4E5F60718.lic

# ③ 把 .lic 发给用户，让他改名成 license.lic，放到 exe 同目录
```

---

## 用户那边是什么体验

**没授权时，程序弹一个窗口：**

```
┌─ 需要授权 ───────────────────────────────┐
│                                          │
│ 把这串机器码发给作者                      │
│  ┌──────────────────────────┐  ┌──────┐ │
│  │   A1B2C3D4E5F60718       │  │ 复制 │ │
│  └──────────────────────────┘  └──────┘ │
│                                          │
│ 收到授权文件后，放进这个目录：             │
│  D:\...\WorkBuddyPanel\                  │
│  [打开这个文件夹]                         │
│                                          │
│  ▸ 机器信息（排查问题时用）                │
│                                          │
│         [我已放好，重新检查]  [退出]      │
└──────────────────────────────────────────┘
```

**复制机器码 → 发你 → 收到 .lic → 放进去 → 点「重新检查」→ 进主界面。**

不用重启程序。

---

## 发卡工具

```bash
# 看帮助
dotnet run --project tools/make-license -- --help

# 签一年
dotnet run --project tools/make-license -- --issue <机器码> --to 张三 --days 365

# 指定到期日
dotnet run --project tools/make-license -- --issue <机器码> --to 张三 --until 2027-12-31

# 永久授权
dotnet run --project tools/make-license -- --issue <机器码> --to 张三 --perpetual

# 批量签（文本文件，一行一个机器码，可以跟名字）
dotnet run --project tools/make-license -- --batch machines.txt --days 365 --outdir licenses

# 看一个授权文件的内容
dotnet run --project tools/make-license -- --info license-xxx.lic
```

`machines.txt` 长这样：

```
A1B2C3D4E5F60718 张三
1122334455667788 李四
99AABBCCDDEEFF00 王五
```

---

## 原理

用 [`Standard.Licensing`](https://github.com/junian/Standard.Licensing)（现成库，240 万下载量）。

```
发卡工具（你留着）
    ├── 私钥  keys/private.key    ← 加密存储，绝不进 git
    └── 签出  license.lic（ECDSA 签名）
                    ↓ 发给用户
程序（发给别人）
    └── 公钥  嵌在 Services/LicenseGuard.cs 里
               只能验签，提取不出私钥
```

**授权文件长这样**（596 字节，能直接发微信）：

```xml
<License>
  <Expiration>Sun, 25 Oct 2026 00:00:00 GMT</Expiration>
  <ProductFeatures>
    <Feature name="MachineId">E387198897C29D6E</Feature>
    <Feature name="Edition">pro</Feature>
  </ProductFeatures>
  <Customer><Name>张三</Name></Customer>
  <Signature>MEUCIHtPfhuoQnWwTCha6fGlEsM+dUplfmya3rBJpQ...</Signature>
</License>
```

---

## 机器码怎么来的

```
主板 UUID（Win32_ComputerSystemProduct.UUID）
  + CPU ID（Win32_Processor.ProcessorId）
    → SHA256 → 前 16 位大写十六进制
```

**为什么两个一起用：**

- 主板 UUID 在虚拟机/部分主板里是空的或全 F —— 单独用会有机器取不到
- CPU ID 换 CPU 会变 —— 单独用用户升级就要重新授权
- 组合后：换主板会变（合理），换硬盘/重装系统不变

**不用网卡 MAC** —— 虚拟网卡、Wi-Fi 开关、装 VPN 都会让它变，用户会莫名其妙被判定换机器。

---

## 能防住什么

| 攻击 | 结果 |
|---|---|
| 改机器码 | ✓ 签名失效，拒绝 |
| 改到期时间 | ✓ 签名失效，拒绝 |
| 用别人的授权文件 | ✓ 机器码对不上，拒绝 |
| 自己伪造一个授权 | ✓ 没有私钥签不出来 |
| **反编译 exe 把校验改成 return true** | ✗ **防不住** |

最后一条是 .NET 的固有限制 —— IL 可以反编译（ILSpy 一键），别人找到校验方法改成永远返回 true 就行，大约 30 分钟工作量。

**要再提高门槛**：加混淆（ConfuserEx / Obfuscator），能让反编译变成一堆不可读的代码。对"想用 AI 工具的人"这个用户群，签名授权已经够了。

---

## 维护模式也校验

计划任务拉的 `--maintain` 也会校验授权。**不校验的话，删掉授权文件后照样每天签到续期** —— 那是我们自己的账号，等于白用工信。

但失败时**静默退出（退出码 1）**，不弹窗：那是计划任务拉起来的，屏幕前没人，弹窗只会挂着不走。

---

## 测试

6 个场景都实测过：

```
① 无授权文件        → NoLicenseFile，窗口给出机器码和路径
② 正确授权          → 通过
③ 篡改机器码        → BadSignature，拒绝
④ 别人的授权        → MachineMismatch，列出两边机器码
⑤ 过期授权          → Expired，显示到期日
⑥ 永久授权          → 通过
```

---

## 密钥管理

**`keys/private.key` 绝不能丢、绝不能泄露。**

```
丢了   → 签不了新授权，只能重新生成密钥对（旧授权全作废）
泄露   → 别人能自己签授权，整套方案失效
```

已经在 `.gitignore` 里排除了。**备份一份到密码管理器或离线介质。**

---

## 文件清单

```
Services/MachineId.cs              机器码采集（WMI + 哈希）
Services/LicenseGuard.cs           校验逻辑 + 嵌公钥
ActivationWindow.xaml(.cs)         激活窗口
App.xaml.cs                        启动时插的校验（含 --maintain 分支）
tools/make-license/                发卡工具（你自己用）
keys/                              密钥对（私钥不进 git）
```
