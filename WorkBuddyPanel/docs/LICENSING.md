# 发卡工具与协作方式

给程序加机器码绑定授权。这份文档说明**两个开发者怎么配合**。

---

## 一、核心事实：签发权只能有一个

签名授权的安全性来自「**只有你能签**」：

```
私钥 → 只有持有它的人能签发授权
公钥 → 公开，用来验证签名
```

**这个体系里没有「两个人都有签发权」的选项** —— 要么共用一把私钥（等于一个人），要么一个人签。

### 推荐：私钥只在一人手上

```
持有者（发卡方）
    ├── 私钥  绝不外传，不进 git
    └── 负责所有发卡、用户机器码记录、收款

另一个开发者
    ├── 有公钥（在仓库里，本来就是公开的）
    ├── 能编译、能打包、能测试
    └── 不发卡（测试时找发卡方要一个测试授权）

用户
    └── 把机器码发给发卡方 → 收到 license.lic
```

**为什么这样分：**

| | |
|---|---|
| **可追责** | 每张授权都能对应到「谁、什么时候、给哪台机器签的」 |
| **密钥面最小** | 私钥只在一台机器上，泄露概率最低 |
| **用户数据集中** | 谁的机器码、谁付了钱，只有一处记录 |

**代价：** 发卡方是瓶颈。测试流程也要走一遍发卡。

---

## 二、三个公钥必须一致

改密钥对之后，**这三个地方必须是同一个公钥**：

```
① WorkBuddyPanel/keys/public.key            进仓库，给人看
② %USERPROFILE%\.workbuddy-license\public.key  发卡工具用
③ WorkBuddyPanel/Services/LicenseGuard.cs 里的 PublicKey 常量
   ↑ 这个是编进主程序的，改它必须重新编译主程序
```

**不一致的后果：** 签出来的授权在主程序里验不过 —— 用户会看到「授权文件无效」。

`license-tool` 界面上有个「复制公钥」按钮，复制的就是 ②。生成密钥对后会提示贴进 ③。

---

## 三、仓库里放什么、不放什么

```
✓ 进仓库
    WorkBuddyPanel/keys/public.key              公钥，本来就是公开的
    WorkBuddyPanel/tools/make-license/          命令行发卡工具
    WorkBuddyPanel/tools/license-tool/          图形发卡工具
    WorkBuddyPanel/Services/LicenseGuard.cs     含公钥常量
    WorkBuddyPanel/docs/LICENSING.md            这份文档

✗ 不进仓库（已在 .gitignore）
    %USERPROFILE%\.workbuddy-license\private.key   私钥，绝不外传
    *.lic                                          签出来的授权（含用户信息）
```

**为什么发卡工具的代码可以进仓库：**

```
工具代码里没有私钥 —— 私钥是运行时从用户目录读的
拿到工具代码 ≠ 能发卡，还得有私钥文件
```

---

## 四、密钥存在哪

**放在用户目录，不跟 exe 绑：**

```
%USERPROFILE%\.workbuddy-license\
    private.key    私钥（口令加密）
    public.key     公钥
```

**为什么不在 exe 旁边：**

```
✗ exe 放在哪、往上几层能找到 keys/ —— 这是绑在目录结构上的
✗ 换个目录跑、换台机器，就找不到了
✗ 密钥是「你个人的东西」，不是程序的一部分
```

两个工具（图形版、命令行版）用**同一套查找逻辑**：

```
① 优先  %USERPROFILE%\.workbuddy-license\
② 兼容  从 exe 往上找 keys/（旧的开发位置，找到会提示搬迁）
③ 都没有  用 ①（首次运行时在那里生成）
```

**换机器怎么办：** 把 `.workbuddy-license` 整个目录拷过去。

**备份：** 拷到密码管理器或离线介质。丢了签不了新授权（旧授权还能用），泄露了别人能自己签。

---

## 五、日常流程

### 发卡方

**第一次（只做一次）：**

```powershell
cd WorkBuddyPanel
dotnet run --project tools/make-license -- --gen
#   → 设个口令，记住它
#   → 输出的公钥贴进 Services/LicenseGuard.cs
#   → 私钥自己存好
```

**或者用图形版：**

```powershell
# 先发布一个自包含版（双击即用，不需要装 .NET）
cd WorkBuddyPanel/tools/license-tool
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:DebugType=none -o ../../发卡工具
```

```
双击 WorkBuddyPanel/发卡工具/发卡工具.exe
点「生成密钥对」→ 设口令 → 复制公钥 → 贴进 LicenseGuard.cs
```

（发布产物不进 git —— 138MB，需要时自己 publish 一下就行。源码在 `tools/license-tool/`）

**之后每发一张卡：**

```
图形版：贴机器码 → 填名字 → 填天数 → 点「签发并保存」→ 把 .lic 发给用户
命令行：dotnet run --project tools/make-license -- --issue <机器码> --to 张三 --days 365
```

### 用户

```
① 装好程序，第一次打开看到「需要授权」窗口
② 点「复制」拿到机器码，发给发卡方
③ 收到 license.lic，改名成 license.lic，放到程序目录
④ 点「我已放好，重新检查」→ 进主界面
```

### 另一个开发者

```
✓ 改代码、编译、打包
✓ 测试授权流程 —— 找发卡方要一个测试授权（就说"给我签一个测试码"）
✗ 不需要私钥
```

**如果你（发卡方）也参与开发：** 本地那个私钥就是你的工作密钥，直接用来测。

---

## 六、两个发卡工具的区别

| | 图形版 `license-tool` | 命令行版 `make-license` |
|---|---|---|
| 场景 | 手工发卡，一张一张 | 批量、脚本调用 |
| 生成密钥 | ✓ | ✓ |
| 签发单个 | ✓ 贴机器码点一下 | ✓ 传参数 |
| 批量签发 | ✗ | ✓ `--batch` |
| 看授权内容 | ✗ | ✓ `--info` |
| 适合 | 日常零星发卡 | CI、一次发几十个 |

**它们共用同一套密钥和签名逻辑，签出来的文件完全一样，随便用哪个。**

---

## 七、技术细节

### 签名方案

用 [`Standard.Licensing`](https://github.com/junian/Standard.Licensing)（现成库，240 万下载量）。

授权文件是 600 字节左右的 XML：

```xml
<License>
  <Expiration>Sun, 25 Oct 2026 00:00:00 GMT</Expiration>
  <ProductFeatures>
    <Feature name="MachineId">E387198897C29D6E</Feature>
    <Feature name="Edition">pro</Feature>
  </ProductFeatures>
  <Customer><Name>张三</Name></Customer>
  <Signature>MEUCIHtPfhuoQnWwTCha6fGlEsM+...</Signature>
</License>
```

### 机器码

```
主板 UUID + CPU ID → SHA256 → 前 16 位大写十六进制
```

- 换主板 → 变（合理，等于换了台机器）
- 换硬盘 → 不变
- 重装系统 → 不变
- 换 CPU → 一般不变（主板 UUID 兜住）

**不用网卡 MAC** —— 虚拟网卡、Wi-Fi 开关、装 VPN 都会让它变。

### 能防住什么

| 攻击 | 结果 |
|---|---|
| 改机器码 | ✓ 签名失效 |
| 改到期时间 | ✓ 签名失效 |
| 用别人的授权 | ✓ 机器码不匹配 |
| 伪造授权 | ✓ 没有私钥签不出来 |
| **反编译主程序改掉校验** | ✗ **防不住** |

最后一条是 .NET 的固有限制（IL 可反编译）。要再提高门槛得加混淆（ConfuserEx / Obfuscator）。对「想用 AI 工具的人」这个用户群，签名够了。

---

## 八、测试

```bash
# 授权模块的单元/集成测试（6 个场景）
cd WorkBuddyPanel

# 无密钥时：应该在 bin/Release/net8.0-windows/ 下放一个 license.lic 才能启动
# 具体测法见 docs/LICENSING.md
```

**实测过的场景：**

```
① 无授权文件    → 弹激活窗口，给出机器码和路径
② 正确授权      → 进入主界面
③ 篡改机器码    → BadSignature，拒绝
④ 别人的授权    → MachineMismatch，列出两边机器码
⑤ 过期授权      → Expired，显示到期日
⑥ 永久授权      → 通过
```

---

## 九、常见问题

**Q: 用户换电脑了怎么办？**
A: 让他发新的机器码，重新签一张。旧的那张作废不了（还在他手上），但绑的是旧机器，在新机器上用不了。

**Q: 用户重装系统？**
A: 机器码不变，授权继续有效。

**Q: 想给某个用户延期？**
A: 重新签一张（有效期设长一点），让他替换掉旧的。

**Q: 授权文件泄露了怎么办？**
A: 它绑了机器码，在别的机器上用不了。这是机器码绑定的意义。

**Q: 主程序改了，要不要重新签授权？**
A: 不用。授权只跟机器码和到期时间有关，跟程序版本无关。

**Q: 换密钥对了呢？**
A: 所有已发出的授权全部作废。别轻易换。真要换，三个公钥位置都要更新，主程序要重新编译打包。
