# 本地构建脚本（工程内自包含路径，所有产物只落在本目录）
# 用法：powershell -ExecutionPolicy Bypass -File build-local.ps1
# 输出：out\QRSnap.exe，编译日志 build.log

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$outDir = Join-Path $root "out"
if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
$out = Join-Path $outDir "QRSnap.exe"
$log = Join-Path $root "build.log"

$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path -LiteralPath $csc)) { $csc = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path -LiteralPath $csc)) { Write-Host "找不到 .NET Framework 4.x 编译器 csc.exe" -ForegroundColor Red; exit 1 }

$jade    = Join-Path $root "JadeView_x64.dll"
$ort     = Join-Path $root "Microsoft.ML.OnnxRuntime.dll"
$mem     = Join-Path $root "System.Memory.dll"
$netstd  = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\netstandard.dll"

Get-Process QRSnap -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 700

$srcs = @('src.cs','ui_jade.cs','PpOcr.cs','Mt.cs','LlamaMt.cs','TransOverlay.cs','MtRealistic.cs',
          'AiTextFix.cs','AiBoost.cs','LayoutBoost.cs','ShotCache.cs','RuntimeCheck.cs','GameMode.cs',
          'OcrPreprocess.cs')

Push-Location -LiteralPath $root
try {
    $cscArgs = @('/nologo','/target:winexe','/platform:x64','/optimize+','/codepage:65001',
              '/win32icon:app.ico','/win32manifest:app.manifest',
              '/resource:zxing.dll,ZXing.Net.dll','/resource:bg.jpg,Back.jpg','/resource:app.ico,App.ico',
              '/resource:web\index.html,WEB_INDEX','/resource:web\app.css,WEB_CSS','/resource:web\app.js,WEB_JS',
              "/resource:$jade,JADEVIEWDLL",
              '/r:zxing.dll','/r:System.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Core.dll',
              '/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll',
              "/r:$ort","/r:$mem","/r:$netstd",
              "/out:$out") + $srcs
    & $csc $cscArgs 2>&1 | Out-File -LiteralPath $log -Encoding utf8
} finally {
    Pop-Location
}

$cwebpSrc = Join-Path $root "cwebp.exe"; if (Test-Path -LiteralPath $cwebpSrc) { Copy-Item -LiteralPath $cwebpSrc -Destination (Join-Path $outDir "cwebp.exe") -Force };
if (Test-Path -LiteralPath $out) {
    $size = [math]::Round((Get-Item -LiteralPath $out).Length / 1KB, 1)
    Write-Host ("编译完成：" + $out + "  (" + $size + " KB)") -ForegroundColor Green
    Get-Content -LiteralPath $log -Encoding utf8 | Select-Object -Last 15
} else {
    Write-Host "编译失败，日志见 build.log" -ForegroundColor Red
    Get-Content -LiteralPath $log -Encoding utf8 | Select-Object -Last 30
}
