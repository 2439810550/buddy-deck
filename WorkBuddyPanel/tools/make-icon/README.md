# 图标生成

`WorkBuddyPanel/Assets/app.ico` 和那几张 `app-*.png` 是**生成出来的**，
不是手工画的。别直接改它们 —— 改脚本再重新生成。

## 跑法

```bash
cd WorkBuddyPanel
python tools/make-icon/make_whale_icon.py
```

需要 Pillow（`pip install pillow`）。

生成的东西：

```
Assets/app.ico              118 KB，9 档（16/20/24/32/40/48/64/128/256）
Assets/app-16/32/48 ...     各尺寸单独一份，给安装器/文档用
Assets/icon-preview.png     各尺寸实际效果对比图（用来肉眼检查）
```

## 素材

`D:\za\weixinzhushou\animations\assets\idle.png`（512×512 鲸鱼娘立绘）

有 12 张动作帧可选，改脚本里的 `SOURCE_FRAME` 就行：

```
idle  crouch  drag  focus  happy  jump  land  proud  sleep  surprise  wave  blink
```

## 为什么不能直接用原图

原图是**全身立绘**，缩到 16px 会糊成一坨 —— 动漫立绘有几十种颜色、
大量渐变和细线，而 16px 只有 256 个像素。

所以脚本做的是**分层**，每个尺寸用不同的图：

```
256 / 128 / 64 / 48  →  头肩立绘（清晰完整）
32 / 24 / 16         →  简化版：裁得更紧 + 模糊抹细节 + 提对比让色块分明
```

小尺寸那几张不是「同一张缩下去」，而是专门处理过的。这是专业图标的
通用做法（Chrome、VSCode 都这么干）。

**16px 仍然会是个蓝色小团** —— 那是这个形象的物理极限，不是没做好。
32px 以上都没问题。

## 换裁切 / 换表情

改 `make_whale_icon.py` 顶部四个常量：

```python
SOURCE_FRAME  = "idle.png"           # 换表情
CROP_BIG      = (85, 10, 425, 300)   # 大尺寸裁切框（左, 上, 右, 下）
CROP_SMALL    = (115, 25, 395, 235)  # 小尺寸裁切框（更紧）
BIG_THRESHOLD = 48                   # 小于这个尺寸用简化版
```

改完重新跑脚本，然后看一眼 `Assets/icon-preview.png` 确认效果。

## ico_pack.py

自己写的 ICO 打包器。

**为什么不用 Pillow 的 `save(sizes=[...])`**：那个参数是拿**同一张图**
缩成各个尺寸，小尺寸那几档就得不到简化处理。

**为什么不用 `append_images=[...]`**：试过，**实测不生效** ——
存出来的 ico 只有 812 字节、里面只有一档 16×16。

所以自己打包。ICO 格式本身很简单（6 字节文件头 + 每档 16 字节目录项 +
数据区，每档用 PNG 压缩），60 行就写完了。`verify_ico()` 会把文件读回来
逐档检查 —— 不能只信写的过程。

## 图标在哪儿生效

改完 `app.ico` 之后，它在四个地方起作用，各自独立：

| 位置 | 由什么控制 |
|---|---|
| **exe 文件图标** | `WorkBuddyPanel.csproj` 的 `<ApplicationIcon>` |
| **桌面/开始菜单快捷方式** | 安装器创建快捷方式时自动继承 exe 的图标 |
| **托盘图标**（右下角） | `TrayIcon.cs` 从嵌入资源读 —— 见下面的说明 |
| **安装包本身** | `installer.iss` 的 `SetupIconFile` |

### 托盘为什么要在代码里读

`<ApplicationIcon>` 只影响 **exe 的文件图标**，运行时读不到它。
托盘需要在代码里拿到图标数据，所以同一个文件还要作为 `<Resource>`
嵌进程序集：

```xml
<Resource Include="Assets\app.ico" />
```

`TrayIcon.CreateTrayIcon()` 用 `pack://application:,,,/Assets/app.ico`
读它（注意是**三个逗号**，WPF 的固定写法），读不到就退回
`CreateFallbackGlyph()`（运行时画的字母图标）。

留兜底的理由：托盘图标画不出来 = 用户在托盘里找不到程序，
那比「图标不好看」严重得多。
