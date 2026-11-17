# XuYuan-SnapScan v1.4.16

Windows 截图 OCR / 翻译工具（WinForm + JadeView 2 + WebView2）：二维码、文字识别、截图翻译、固定区域游戏字幕翻译。本体已内置微信 OCR + PP-OCR，装完直接能识字；翻译模型按需导入。

## 功能

- **截图识码** `Ctrl+Alt+D`
- **截图识字** `Ctrl+Alt+X`
- **截图翻译** `Ctrl+Alt+F`
- **显示 / 隐藏译文** `Ctrl+Alt+G`
- **游戏字幕翻译**：只认你画的固定区域，遮罩常驻不闪；按文字位置把相邻行并成段，块间并发；动态 ROI 只重扫真正变化的行，没变的部分沿用上一轮结果；每款游戏独立翻译缓存；`Ctrl+Alt+T` 开关翻译（手动模式按一次翻一次、画面一变遮罩即收；自适应模式只在新字幕出现时重译）
- **灵敏度四档**：低 / 中 / 高 / 极高
- **分块框调试**：设置里打开「显示分块框（调试）」，屏上用彩色矩形标出自动分出来的文字块，方便核对
- **多 OCR 引擎**：微信 OCR / PP-OCR 极速 / Windows OCR
- **多翻译档**：标准 HyMT1.5 1.8B、效率 PLaMo Q4_K_M、极速 PLaMo Q2_K
- **模型包导入**：设置 → 识别引擎 / 翻译 → 导入模型包

## 下载

本体和翻译模型都在 **<https://github.com/XuYuan2333/XuYuan-SnapScan/releases>**。

| 文件 | 大小 | 内容 |
|---|---:|---|
| `SnapScan-v1.4.16-Body.zip` | 126.0 MB | 本体 + 微信 OCR + PP-OCR（日文 / 极速），装着就能识字 |
| `SnapScan-Model-OCR-v1.4.1.zip` | 37.8 MB | 备用 OCR 包（本体已内置，一般不用下） |
| `SnapScan-Model-PLaMo3-Translate-0.8B-Q4_K_M-v1.4.2.zip` | 573 MB | 效率档 PLaMo Q4_K_M |
| `SnapScan-Model-PLaMo3-Translate-0.8B-Q2_K-v1.4.2.zip` | 410 MB | 极速档 PLaMo Q2_K |
| `SnapScan-Model-Pro-HyMT1.5-1.8B-v1.4.1.zip` | 1.1 GB | 标准档 HyMT1.5 1.8B |

装本体 + Q4 就是日常游戏字幕那一套。翻译模型包自 v1.4.2 起没有变化，不重复上传。

> 极速档 Q2_K（410 MB）和标准档 HyMT1.5（1.1 GB）超出单仓库附件配额，需要时用 `pack.ps1 -Only models` 本地打包。

## 翻译模型选择

| 档位 | 模型 | 文件大小 | 预计占用 | 优点 | 缺点 |
|---|---|---:|---:|---|---|
| 标准 | HyMT1.5 1.8B Q4_K_M | 1080 MB | 约 2.0 GB | 语种覆盖最广，长句最稳 | 体积最大、最慢 |
| 效率 | PLaMo 3 Translate 0.8B Q4_K_M | 546 MB | 约 1.0 GB | 翻译专用，质量和速度平衡最好 | 比 Q2 略大 |
| 极速 | PLaMo 3 Translate 0.8B Q2_K | 391 MB | 约 0.8 GB | 体积最小、低配友好 | 量化更低，部分句子略差 |

- 日常游戏字幕：**效率 Q4_K_M**
- 内存 / 显存紧张：**极速 Q2_K**
- 多语种、长文本：**标准 HyMT1.5**

普通翻译超过设置的空闲时间自动释放模型；游戏模式模型常驻，关闭游戏模式或切换模型时释放。

## 使用

1. 解压本体到任意目录，运行 `QRSnap.exe`；
2. 设置 → 识别引擎 / 翻译 → 导入模型包（选 zip，不用手动解压）；
3. 截图类功能直接按热键；
4. 游戏字幕：设置 → 游戏专属配置 → 画固定区域 → 开游戏模式 → `Ctrl+Alt+T` 开关翻译。

## 系统要求

- Windows 10 21H2 / Windows 11，64 位；
- .NET Framework 4.8；
- WebView2 Runtime（缺失时运行本体里的 `MicrosoftEdgeWebview2Setup.exe`）；
- 有 Vulkan 显卡自动用 GPU，没有则回 CPU；
- 独占全屏游戏可能截到黑屏，建议无边框窗口。

## 模型与许可

- HyMT1.5：使用对应模型许可证，商用前请确认。
- PLaMo 3 Translate：PLaMo Community License，个人 / 非商业免费。
- 模型文件不包含在本体中，按需下载导入。

## 构建

```powershell
powershell -ExecutionPolicy Bypass -File build19.ps1
powershell -ExecutionPolicy Bypass -File pack.ps1 -Only body
```

`build19.ps1` 用 `csc.exe` 编译到 `out\QRSnap.exe`；`pack.ps1` 输出发布包到 `dist\`。

## 说明

- 本项目不上传用户 Key、截图或翻译内容。
- 发布包只包含程序和模型，不包含用户配置。
