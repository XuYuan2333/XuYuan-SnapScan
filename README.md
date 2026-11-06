# XuYuan-SnapScan

> 轻量级截图识别工具 · 二维码 / 文字 OCR · 全局快捷键呼出 · 毛玻璃界面

## 功能

- **截图识码**：框选屏幕任意区域，自动识别二维码 / 条形码
- **截图识字**：基于 PP-OCRv6 的中文文字识别，框选即识别
- **全局快捷键**：`Ctrl+Alt+D` 截图识码，`Ctrl+Alt+X` 截图识字（可自定义）
- **系统托盘运行**：识别后最小化到任务栏，不打扰工作流
- **毛玻璃界面**：支持背景图 + 实时调节不透明度与模糊度
- **结果透明显示**：识别结果直接显示在背景图上，无白底遮挡

## 下载选择

| 版本 | 大小 | 模型 | 适合场景 |
|------|------|------|----------|
| **lite** | ~800 KB | 无（Windows 内置 OCR 兜底） | 只用二维码识别，或系统自带 OCR 够用 |
| **tiny** | ~6 MB | PP-OCRv6 Tiny | 追求速度，日常截图识字 |
| **small** | ~31 MB | PP-OCRv6 Small | 平衡速度与精度 |
| **medium** | ~140 MB | PP-OCRv6 Medium | 高精度需求，复杂排版 / 小字 |

> **注意**：tiny / small / medium 版本需要本机已安装 Python 及 `rapidocr` 包（`pip install rapidocr`）。lite 版本无任何外部依赖。

## 使用

1. 解压后双击 `XuYuan-SnapScan.exe`
2. 按 `Ctrl+Alt+D` 截图识码，`Ctrl+Alt+X` 截图识字
3. 框选区域后自动识别，结果显示在主窗口
4. 双击结果或点"复制结果"复制到剪贴板
5. 点右上角"设置"可调整快捷键、模型规格、界面透明度

## 技术栈

- C# / .NET Framework 4.8 / WinForms（单文件 exe，内嵌 ZXing.Net）
- OCR：[RapidOCR](https://github.com/RapidAI/RapidOCR) 3.x（Python 壳调用）
- 模型：PaddleOCR PP-OCRv6（Tiny / Small / Medium）
- 二维码：[ZXing.Net](https://github.com/micjahn/ZXing.Net)

## 致谢

- [RapidAI/RapidOCR](https://github.com/RapidAI/RapidOCR) — 跨平台 OCR 推理引擎
- [PaddlePaddle/PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) — PP-OCRv6 模型
- [ZXing.Net](https://github.com/micjahn/ZXing.Net) — 二维码识别库
- 毛玻璃效果基于 Windows DWM API

## 许可

本项目仅供学习交流使用。模型版权归 PaddlePaddle 所有。
