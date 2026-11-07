# XuYuan-SnapScan v1.4.2

Windows 截图识别 / 离线翻译工具：二维码、文字 OCR、截图翻译、手动游戏字幕翻译。
界面为 JadeView 2 + WebView2，模型按需导入，本体不内置大模型。

## 功能

- **截图识码** `Ctrl+Alt+D`
- **截图识字** `Ctrl+Alt+X`
- **截图翻译** `Ctrl+Alt+F`
- **游戏字幕翻译**：固定多个区域，只保留手动触发；按 `Ctrl+Alt+F` 翻译一次，翻译中再按强制开新一轮
- **显示/隐藏译文** `Ctrl+Alt+G`，也可点遮罩上的关闭按钮
- **多 OCR 引擎**：微信 OCR / PP-OCR 极速 / Windows OCR
- **多翻译档**：标准 HyMT1.5、效率 PLaMo Q4_K_M、极速 PLaMo Q2_K
- **模型包导入**：设置 → 识别引擎 / 翻译 → 导入模型包
- **游戏区域悬停预览**：鼠标移到区域行/删除按钮，屏幕上高亮对应区域

## 下载

| 文件 | 大小 | 内容 |
|---|---:|---|
| `SnapScan-v1.4.2-Body.zip` | 约 70 MB | 本体、ONNX Runtime、CPU/Vulkan llama、VC++ 运行库、WebView2 引导 |
| `SnapScan-Model-OCR-v1.4.1.zip` | 约 27 MB | 微信 OCR + PP-OCR 极速 |
| `SnapScan-Model-PLaMo3-Translate-0.8B-Q4_K_M-v1.4.2.zip` | 约 547 MiB | 效率档 PLaMo Q4_K_M |
| `SnapScan-Model-PLaMo3-Translate-0.8B-Q2_K-v1.4.2.zip` | 约 391 MiB | 极速档 PLaMo Q2_K |
| `SnapScan-Model-Pro-HyMT1.5-1.8B-v1.4.1.zip` | 约 1.04 GB | HyMT1.5 1.8B Q4_K_M（标准） |

本体不带翻译/OCR 模型；PLaMo 的 Q4_K_M 和 Q2_K 是两个独立包，按需分别导入。

## 翻译模型选择

| 档位 | 模型 | 文件大小 | 预计占用 | 优点 | 缺点 |
|---|---|---:|---:|---|---|
| 标准 | HyMT1.5 1.8B Q4_K_M | 1080 MB | 约 2.0 GB | 语种覆盖最广，长句和复杂文本最稳 | 体积最大、加载和单句速度最慢 |
| 效率 | PLaMo 3 Translate 0.8B Q4_K_M | 546 MB | 约 1.0 GB | 翻译专用，53 语言，质量和速度平衡最好 | 比 Q2 略大 |
| 极速 | PLaMo 3 Translate 0.8B Q2_K | 391 MB | 约 0.8 GB | 体积最小、低配机器更友好 | 量化更低，部分句子质量略低于 Q4 |

> 普通翻译：超过设置的空闲时间自动释放模型；游戏模式：模型常驻，关闭游戏模式或切换模型时释放。`0` 表示普通翻译也不自动释放。

推荐：

- 日常游戏字幕：**效率 Q4_K_M**
- 内存/显存紧张、追求更快：**极速 Q2_K**
- 多语种、长文本、质量优先：**标准 HyMT1.5**

OCR 引擎：

- 微信 OCR：中文最准，推荐
- PP-OCR 极速：轻量、快
- Windows OCR：系统兜底

## 使用

1. 解压本体到任意目录，运行 `QRSnap.exe`。
2. 设置 → 识别引擎 / 翻译 → 导入模型包，选需要的 zip。
3. `Ctrl+Alt+D` 识码，`Ctrl+Alt+X` 识字，`Ctrl+Alt+F` 翻译。
4. 游戏模式：设置 → 游戏专属配置 → 开启游戏模式 → 添加固定字幕区域 → 按 `Ctrl+Alt+F` 手动翻译。
5. PLaMo 只导入 Q4 也能用效率档；需要极速档时再单独导入 Q2 包。
5. 清除 API Key 会自动关闭所有依赖 Key 的 AI 功能。

## 系统要求

- Windows 10 21H2 / Windows 11，64 位
- .NET Framework 4.8
- WebView2 Runtime（缺时运行本体里的 `MicrosoftEdgeWebview2Setup.exe`）
- 有 Vulkan 显卡驱动会自动用 GPU，没有则回 CPU
- 游戏建议无边框窗口；独占全屏可能截黑屏或挡遮罩

## 模型与许可

- HyMT1.5：使用对应模型许可证，商用前请确认。
- PLaMo 3 Translate：PLaMo Community License，个人/非商业免费，商业用途需查看官方条款。
- 模型文件不包含在本体中，按需下载导入。

## 构建

```powershell
powershell -ExecutionPolicy Bypass -File build19.ps1
powershell -ExecutionPolicy Bypass -File pack-release.ps1
```

`build19.ps1` 用 `csc.exe` 编译；`pack-release.ps1` 生成本体、OCR、PLaMo、HyMT 模型包和 SHA256。

## 说明

- 本项目不上传用户 Key、截图或翻译内容。
- 发布包只包含程序和模型，不包含用户配置。
