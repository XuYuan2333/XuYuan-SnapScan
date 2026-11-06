# XuYuan-SnapScan

> 轻量级截图识别工具 · 二维码 / 文字 OCR · 全局快捷键呼出 · JadeView 2.0 网页界面 + 毛玻璃

## 功能

- **截图识码**：圈选屏幕任意区域，自动识别二维码 / 条形码
- **截图识字**：支持自由圈选（任意形状）或方形框选，圈住即识别
- **全局快捷键**：`Ctrl+Alt+D` 截图识码，`Ctrl+Alt+X` 截图识字（可自定义）
- **多识别引擎**：微信 OCR（推荐）/ PP-OCR 极速 / PP-OCR 标准 / PP-OCR 高精 / Windows 内置 OCR，设置里选择，缺模型的项置灰
- **导入模型包**：设置里点一下、选个 zip 就装好引擎，不用自己解压、不用挪目录
- **界面**：JadeView 2.0 + HTML/CSS/JS（复用系统 WebView2，不打包浏览器内核），软件图标 + 背景图 + 毛玻璃
- **无托盘**：程序只以任务栏按钮存在，关窗口＝退出
- **外观可调**：设置里实时调节「透明度」（0–100，默认 30）与「毛玻璃强度」（0–20，默认 3）
- **结果区**：全透明只留边框，背景图完整透出；文本可直接选中、编辑、`Ctrl+C` 复制，长文本自动出细滚动条

## 下载哪个包

分成两层：**程序本体**（每次更新都换）+ **模型包**（下过一次就够了，换版本不用重下）。

1. 下 `SnapScan-v1.1.8-Body.zip`（本体，约 2.4 MB），解压得到 `QRSnap.exe`
2. 想要哪个引擎，就下对应那个模型包（**不用解压**，zip 原样留着）
3. 打开软件 → 右上角齿轮 → 设置里的「识别引擎」→ 点「导入模型包」→ 选中刚下的 zip
4. 导入完列表立刻能选，不用重启

> 只想用它认字、又不挑引擎的话，本体直接就能跑：会用 Windows 系统自带的 OCR 兜底。

| 你想要的 | 下载这个模型包 | 包大小 | 说明 |
|---|---|---|---|
| 中文最准（**推荐**） | `SnapScan-Model-WeChatOCR-v1.1.8.zip` | 21.9 MB | 微信 OCR，随包离线，不装微信也能用 |
| 体积最小、速度最快 | `SnapScan-Model-PP-OCR-Tiny-v1.1.8.zip` | 9.8 MB | PP-OCR tiny，约 200 ms |
| 均衡（中英混排都稳） | `SnapScan-Model-PP-OCR-Small-v1.1.8.zip` | 29.3 MB | PP-OCR small |
| 高精度（小字、复杂排版） | `SnapScan-Model-PP-OCR-Medium-v1.1.8.zip` | 100.3 MB | PP-OCR medium，体积最大 |
| 完全不想下模型 | （不下） | 0 | 直接用 Windows 系统 OCR |

> 模型包只含 `OCR Model`（PP-OCR 三档另带一份推理库），导入后模型进 `QRSnap.exe` 同级的 `OCR Model`，推理库放到 exe 旁边。
> 想同时用多个引擎，就依次把几个模型包都导入一遍，互不覆盖，设置里会一项项多出来。

## 版本变更

- **v1.1.8**：新增「导入模型包」——设置里选一个 zip，程序自己把模型落进 `OCR Model` 目录并刷新引擎列表；发行方式改成本体 + 纯模型包（模型包不含 exe，只要下过一次，换版本不用重下）
- **v1.1.7**：修 Esc 取消截屏后主窗口不再出现（取消/失败/异常都会还原窗口，且框选期间把 Esc 注册成系统热键，遮罩没抢到焦点也能取消）；修截屏会拍到上一轮「识别中／识别完成」提示（抓屏前先清提示条并等 DWM 合成）；提示条移到屏幕居中靠下，改成 30 ms 帧的淡入上滑 + 淡出下滑、渐变底加细描边；框选期间重复按热键不再叠出多层遮罩
- **v1.1.6**：删除托盘图标与「隐藏＝最小化」；关闭主窗口即退出程序；截图期间真正隐藏窗口，结束后按原状态还原（勾选「显示本软件窗口」才强制置顶）；分引擎包去掉用不到的 onnxruntime（微信包 28.8→24.4 MB，Windows 包 6.9→2.5 MB）
- **v1.1.5**：JadeView 2.0 网页界面重做、微信 OCR 随包离线、按引擎分包

## 运行要求

| 项目 | 要求 |
|------|------|
| 系统 | Windows 10 / 11 x64 |
| 运行时 | .NET Framework 4.8（Win10 1903+ / Win11 自带） |
| 浏览器组件 | Microsoft Edge WebView2 Runtime（Win11 自带；Win10 装了新版 Edge 就已具备） |
| Python / rapidocr | **不需要**，PP-OCR 由程序自带 onnxruntime 直接推理 |

## 目录结构（发布包）

```
QRSnap.exe                    主程序（图标、背景图、Web 界面已内嵌）
QRSnap.exe.config             程序集绑定配置，onnxruntime 依赖必需
Microsoft.ML.OnnxRuntime.dll  OCR 推理托管层
onnxruntime.dll               OCR 推理原生库
System.Memory.dll / System.Buffers.dll / System.Numerics.Vectors.dll
System.Runtime.CompilerServices.Unsafe.dll
XuYuan-SnapScan_config.ini    配置（首次运行自动生成）
OCR Model\                    模型根目录，只认这一处（导入模型包时就落在这里）
  PP-OCR\tiny\    det.onnx / rec.onnx / keys.txt    PP-OCRv6 Tiny   约 6 MB
  PP-OCR\small\   ...                              PP-OCRv6 Small  约 30 MB
  PP-OCR\medium\  ...                              PP-OCRv6 Medium 约 132 MB
  WeChat\
    runtime\Weixin.exe                    微信宿主进程（只承载 OCR 插件，不登录、不联网）
    runtime\4.1.15.56\mmmojo_64.dll       mojo 通信库
    plugin\wxocr.dll + text_det / text_rec .xnet + paragraph_det + 字符表
```

> 模型不放 exe 里，是因为 medium 一档就 132 MB。程序**只扫描 exe 同级的 `OCR Model` 目录**，
> 目录里没有的引擎在设置里直接置灰不可选；「导入模型包」就是往这个目录里放东西。

## 使用

1. 解压后双击 QRSnap.exe（没有托盘图标；关掉窗口就是退出程序，不留后台）
2. 按 `Ctrl+Alt+D` 截图识码，`Ctrl+Alt+X` 截图识字
3. 圈选区域后自动识别，结果显示在主窗口
4. 点右上角齿轮进设置：切换引擎、导入模型包、开关自由圈选、改快捷键、调透明度 / 毛玻璃
5. 截图时本窗口会先隐藏，截完按原状态还原（原来最小化就仍最小化、原来被挡住就仍被挡住）；设置里勾了「显示本软件窗口」才强制提到最前

## 识别引擎说明

| 引擎 | 依赖 | 说明 |
|------|------|------|
| 微信 OCR | `OCR Model\WeChat` | **推荐**。中文最准；引擎与模型随包分发，本地推理、不联网，**不依赖本机是否装了微信** |
| PP-OCR 极速 | `OCR Model\PP-OCR\tiny` | 轻量最快，约 200 ms |
| PP-OCR 标准 | `OCR Model\PP-OCR\small` | 均衡，多语种字典 |
| PP-OCR 高精 | `OCR Model\PP-OCR\medium` | 高精度，小字 / 复杂排版更稳，约 700 ms |
| Windows OCR | 系统 | 系统兜底，任何机器都能跑 |

> 以上引擎全部是本地推理，都不需要联网。设置里只列出 `OCR Model` 目录里真正有的模型。

## 技术栈

- C# / .NET Framework 4.8，单文件 `csc.exe` 编译
- 界面：JadeView 2.x 原生库 + WebView2（HTML/CSS/JS）
- OCR：onnxruntime 1.16.3（托管）+ 1.20.1（原生），PP-OCRv6 的检测（DB）与识别（CTC）全部纯 C# 实现，`PpOcr.cs`
- 微信 OCR：剥离自微信 4.x 的 `wxocr.dll` + `mmmojo_64.dll`，手写 mojo + protobuf 协议，纯 P/Invoke
- 二维码：[ZXing.Net](https://github.com/micjahn/ZXing.Net)

## 自行编译

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

只需要 Windows 自带的 `csc.exe`，不需要 Visual Studio / dotnet SDK。

## 致谢

- [PaddlePaddle/PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) — PP-OCRv6 模型
- [microsoft/onnxruntime](https://github.com/microsoft/onnxruntime) — 推理引擎
- [ZXing.Net](https://github.com/micjahn/ZXing.Net) — 二维码识别库
- JadeView 2.x — 网页界面宿主

## 许可与模型版权

本项目代码仅供学习交流使用。

- PP-OCRv6 模型版权归 PaddlePaddle 所有（Apache-2.0）。
- `OCR Model\WeChat` 里的 `Weixin.exe`、`mmmojo_64.dll`、`wxocr.dll`、`.xnet` 均为**腾讯微信**的二进制文件，
  版权归腾讯所有，这里只是把它们放进包里以便离线使用；**公开分发或商用请自行评估授权**。
  介意的话可以删掉整个 `OCR Model\WeChat` 目录，程序会自动回落到 PP-OCR 或 Windows OCR。
