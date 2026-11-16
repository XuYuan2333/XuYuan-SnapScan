# detect-verify（游戏模式自测工具）

两个彼此独立的验证手段，都用项目自带的模型，不依赖主程序的 UI：

1. `Verify.exe`（`Verify.cs` + `build.ps1`）：脱离主程序验证 `AutoRegion` 的选块逻辑。注入假检测器绕开 OCR 质量，专门测「块数有没有被截断」；再注真实 PP-OCR 跑一张合成画面，确认放宽尺寸闸后不会把噪声也召回来。
2. `scenario-test.ps1`：在屏幕上画出模拟游戏窗口（左半日文字幕 3 行、右半英文设置列 8 行），每 6 秒切一次内容，第三段是「文字完全不变、只换背景色」，用来验证「原文没变不重贴遮罩」。

## 用法：选块回归

```powershell
pwsh .\build.ps1                       # 编译出 bin\Verify.exe（自动带齐 onnxruntime 依赖）
.\bin\Verify.exe '..\..\OCR Model\PP-OCR\tiny'
```

输出两类结论：

- `[cap] 假检测器 44 框：cap=24 → 24 块，cap=0(不限) → 44 块` —— 截断取消后必须全留，这是回归红线。
- `[ocr] 合成 12 行 → blocks=N` —— 真实 OCR 链路的块数，用来对比改动前后有没有召回变化。

## 用法：真实画面动测

```powershell
# 1. 模拟游戏画面（另开一个窗口跑，45 秒后自己关）
pwsh .\scenario-test.ps1 -Seconds 45

# 2. 主程序带钩子启动（游戏模式 + 翻译 + 分区日志）
cd ..\..\out
$env:QRSNAP_GAME_ON='1'; $env:QRSNAP_GAME_TRANS='1'; $env:QRSNAP_DBG_ZONE='1'
Start-Process .\QRSnap.exe
# 主窗口必须移出屏幕，否则它会把自己的矩形当「自己窗口的块」丢掉：
# SetWindowPos(hWnd, 0, -3000, -3000, 994, 698, 0x0010)
```

日志在 `%LOCALAPPDATA%\XuYuan-SnapScan\jade-data\native.log`，要核对的关键行：

| 日志 | 看什么 |
|---|---|
| `[game] zone ocr region=1 lines=N` | 整区识字的行数（N 明显大于逐块结果就说明整区生效） |
| `[game] pretranslate lines=N hit=M` / `[game] shown=` | 一批翻译与上屏的行数，hit≈lines 表示缓存命中 |
| `[zone] shown ... bounds=x,y,w,h` | 遮罩真实贴屏矩形，和画面文字位置对一下 |
| `清空遮罩后统一重贴` | 只允许在真换页时出现；文字只换背景的阶段必须是 0 |
| `hide changed` / `zone move` | 原文没变时不该出现 hide，出现就说明「原文一致不重贴」没生效 |
| `translate skip 选区文字本身就是中文` | 日文被压成汉字时的误跳过，正常情况下应该极少 |

## 为什么不用假图片

`AutoRegion.GroupBlocks` 会把上下相邻（行距 ≤ 行高 1.5 倍）的行并成一块，
垂直堆叠的文字无论画多少行最终都只出来一块，没法用来验证「框数上限」。
所以上限验证走假检测器：给 4 行 × 11 列共 44 个互不相邻的框（横向 80、纵向 100，均超过合并阈值），
保证 44 个框各自成块，再对比 `maxRegions=24` 与 `maxRegions=0` 的保留数量。

## 注意

- 依赖项目根目录的 `Microsoft.ML.OnnxRuntime.dll` 与 `out\` 下的原生依赖，`build.ps1` 会自动拷。
- 模型目录传 PP-OCR 的 tiny 目录（`det.onnx` + `rec.onnx` + `keys.txt`）。
- `scenario-test.ps1` 会按 125% DPI 缩放，脚本声明了 DPI 感知，坐标与日志里的屏幕坐标一致。
- 动测会临时改用户 ini（`gameon`/`gameautoregion`/`gameregions`），**先备份**，测完必须还原并核对。
