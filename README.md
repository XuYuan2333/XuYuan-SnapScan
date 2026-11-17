# XuYuan-SnapScan

Windows 截图 OCR / 翻译工具：二维码、文字识别、截图翻译、固定区域游戏字幕翻译。界面使用 WinForm + JadeView 2 + WebView2。

## 功能

- **截图识码**：`Ctrl+Alt+D`
- **截图识字**：`Ctrl+Alt+X`
- **截图翻译**：`Ctrl+Alt+F`
- **显示 / 隐藏译文**：`Ctrl+Alt+G`
- **游戏字幕翻译**：只识别固定的字幕区域，遮罩常驻；按文字位置把相邻行合并成段，块之间并发翻译；动态 ROI 只重扫变化的行；每款游戏单独缓存译文
- **游戏翻译开关**：`Ctrl+Alt+T`。手动模式按一次翻译一次；自适应模式在新字幕出现时自动翻译
- **灵敏度四档**：低 / 中 / 高 / 极高
- **分块框调试**：打开后，屏幕上用矩形标出自动分割出来的文字块，方便检查区域和合块
- **OCR 引擎**：微信 OCR（通用）、RapidOCR（游戏模式推荐）、PP-OCR 日文（片假名）
- **翻译档位**：标准 HyMT1.5 1.8B、效率 PLaMo Q4_K_M、极速 PLaMo Q2_K
- **模型包导入**：设置 → 识别引擎 / 翻译 → 导入模型包，选 zip 即可，不用手动解压

## 下载

发布包在 GitHub Releases：

<https://github.com/XuYuan2333/XuYuan-SnapScan/releases>

Gitee 仓库同步源码和 tag；Gitee 附件容量放不下 120 MB 左右的本体包，下载请用 GitHub Releases。

| 文件 | 大小 | 内容 | 下载后怎么用 |
|---|---:|---|---|
| `SnapScan-v1.4.18-Body.zip` | 120.5 MB | 本体 + 微信 OCR + RapidOCR + PP-OCR 日文 + 运行库 + WebView2 引导 | 解压到任意目录，运行 `QRSnap.exe`；只识字的话下这个就够 |
| `SnapScan-Model-OCR-v1.4.1.zip` | 46.1 MB | 微信 OCR + RapidOCR + PP-OCR 日文模型备份 | 本体已内置；只有模型目录丢失时才需要在「设置 → 识别引擎」导入 |
| `SnapScan-Model-PLaMo3-Translate-0.8B-Q4_K_M-v1.4.2.zip` | 573 MB | 效率档 PLaMo Q4_K_M | 「设置 → 翻译 → 导入模型包」，档位选「效率」 |
| `SnapScan-Model-PLaMo3-Translate-0.8B-Q2_K-v1.4.2.zip` | 410 MB | 极速档 PLaMo Q2_K | 「设置 → 翻译 → 导入模型包」，档位选「极速」 |
| `SnapScan-Model-Pro-HyMT1.5-1.8B-v1.4.1.zip` | 1.1 GB | 标准档 HyMT1.5 1.8B | 「设置 → 翻译 → 导入模型包」，档位选「标准」 |

日常游戏字幕：下载本体，再按机器配置选一个翻译包。只要识字、不翻译，只下本体即可。

## 翻译档怎么选

| 档位 | 模型 | 文件大小 | 预计占用 | 适合 |
|---|---|---:|---:|---|
| 标准 | HyMT1.5 1.8B | 1080 MB | 约 2.0 GB | 多语种、长句、质量优先 |
| 效率 | PLaMo 3 Translate 0.8B Q4_K_M | 546 MB | 约 1.0 GB | 日常游戏字幕，速度和质量平衡 |
| 极速 | PLaMo 3 Translate 0.8B Q2_K | 391 MB | 约 0.8 GB | 内存 / 显存紧张、低配机器 |

普通翻译超过设置的空闲时间会自动释放模型；游戏模式模型常驻，关闭游戏模式或切换模型时释放。

## 使用

1. 解压本体到任意目录，运行 `QRSnap.exe`；
2. 需要翻译时，在「设置 → 翻译 → 导入模型包」导入对应 zip，再选档位；
3. 识码、识字、截图翻译直接按上面的热键；
4. 游戏字幕：设置 → 游戏专属配置 → 添加固定字幕区域 → 开启游戏模式 → 用 `Ctrl+Alt+T` 开关翻译。

游戏模式的翻译档位和遮罩模式跟随「设置」页，不在游戏页单独配置。

## 系统要求

- Windows 10 21H2 / Windows 11，64 位；
- .NET Framework 4.8；
- WebView2 Runtime（缺失时运行本体里的 `MicrosoftEdgeWebview2Setup.exe`）；
- 有 Vulkan 显卡自动用 GPU，没有则回 CPU；
- 独占全屏游戏可能截到黑屏，建议用无边框窗口。

## 模型与许可

- HyMT1.5：使用对应模型许可证，商用前请确认。
- PLaMo 3 Translate：PLaMo Community License，个人 / 非商业免费。
- 模型文件不包含在本体中，按需下载导入。

## 说明

- 本项目不上传用户 Key、截图或翻译内容。
- 发布包只包含程序和模型，不包含用户配置。
