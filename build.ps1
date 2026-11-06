# 一键重新编译 QRSnap.exe
# 只用 Windows 自带的 .NET Framework 4.x 编译器（csc.exe），不需要 Visual Studio、不需要 dotnet SDK。
# 用法：powershell -ExecutionPolicy Bypass -File build.ps1

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$out  = Join-Path (Split-Path -Parent $root) "QRSnap.exe"

$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path -LiteralPath $csc)) { $csc = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path -LiteralPath $csc)) { Write-Host "找不到 .NET Framework 4.x 编译器 csc.exe" -ForegroundColor Red; exit 1 }

# onnxruntime 依赖：工程根需有 managed / native / shim 这些 dll（release 包里已带）
$ort    = Join-Path $root "Microsoft.ML.OnnxRuntime.dll"
$mem    = Join-Path $root "System.Memory.dll"
$netstd = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\netstandard.dll"
foreach ($f in @($ort, $mem, $netstd)) {
    if (-not (Test-Path -LiteralPath $f)) { Write-Host ("缺少依赖：" + $f) -ForegroundColor Red; exit 1 }
}

# JadeView 原生 dll（放在工程根或 tools 目录，二者取其一）
$jade = Join-Path $root "JadeView_x64.dll"
if (-not (Test-Path -LiteralPath $jade)) { $jade = Join-Path $root "tools\JadeView_x64.dll" }
if (-not (Test-Path -LiteralPath $jade)) { Write-Host "缺少 JadeView_x64.dll（JadeView 2.x 原生库）" -ForegroundColor Red; exit 1 }

Get-Process QRSnap -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 700

Push-Location -LiteralPath $root
try {
    & $csc /nologo /target:winexe /platform:x64 /optimize+ `
        /win32icon:app.ico `
        /win32manifest:app.manifest `
        /resource:zxing.dll,ZXing.Net.dll `
        /resource:bg.jpg,Back.jpg `
        /resource:app.ico,App.ico `
        /resource:web\index.html,WEB_INDEX `
        /resource:web\app.css,WEB_CSS `
        /resource:web\app.js,WEB_JS `
        "/resource:$jade,JADEVIEWDLL" `
        /r:zxing.dll /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Core.dll `
        "/r:$ort" "/r:$mem" "/r:$netstd" `
        "/out:$out" src.cs ui_jade.cs PpOcr.cs
} finally {
    Pop-Location
}

if (Test-Path -LiteralPath $out) {
    $size = [math]::Round((Get-Item -LiteralPath $out).Length / 1KB, 1)
    Write-Host ("编译完成：" + $out + "  (" + $size + " KB)") -ForegroundColor Green
} else {
    Write-Host "编译失败" -ForegroundColor Red
}