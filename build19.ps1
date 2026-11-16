# 一键编译：只用 Windows 自带的 .NET Framework 4.x 编译器（csc.exe）
# 工程内相对路径，产物落到 <工程>\out，不再往工程外写任何文件
# 用法：powershell -ExecutionPolicy Bypass -File build19.ps1

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$outDir = Join-Path $root "out"
if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
$out = Join-Path $outDir "QRSnap.exe"
$log = Join-Path $root "build.log"

Stop-Process -Name QRSnap -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { $csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }

$srcs = @('src.cs','ui_jade.cs','PpOcr.cs','Mt.cs','LlamaMt.cs','TransOverlay.cs','MtRealistic.cs',
          'AiTextFix.cs','AiBoost.cs','LayoutBoost.cs','ShotCache.cs','RuntimeCheck.cs','GameMode.cs',
          'OcrPreprocess.cs','AliyunMt.cs','GoogleMt.cs')

Push-Location -LiteralPath $root
try {
    $a = @('/nologo','/target:winexe','/platform:x64','/optimize+','/codepage:65001',
        '/win32icon:app.ico','/win32manifest:app.manifest',
        '/resource:zxing.dll,ZXing.Net.dll','/resource:bg.jpg,Back.jpg','/resource:app.ico,App.ico',
        '/resource:web\index.html,WEB_INDEX','/resource:web\app.css,WEB_CSS','/resource:web\app.js,WEB_JS',
        '/resource:JadeView_x64.dll,JADEVIEWDLL',
        '/r:zxing.dll','/r:System.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Core.dll',
        '/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll',
        '/r:Microsoft.ML.OnnxRuntime.dll','/r:System.Memory.dll',
        '/r:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\netstandard.dll',
        "/out:$out") + $srcs
    & $csc $a *> $log
} finally {
    Pop-Location
}

# 运行期依赖（原生库 / WebP 编码器）跟着产物一起放，方便直接双击
$deps = @('JadeView_x64.dll','zxing.dll','onnxruntime.dll','onnxruntime_providers_shared.dll',
          'Microsoft.ML.OnnxRuntime.dll','System.Buffers.dll','System.Memory.dll',
          'System.Numerics.Vectors.dll','System.Runtime.CompilerServices.Unsafe.dll',
          'app.ico','bg.jpg','cwebp.exe')
foreach ($d in $deps) {
    $f = Join-Path $root $d
    if (Test-Path -LiteralPath $f) { Copy-Item -LiteralPath $f -Destination $outDir -Force }
}
$cfg = Join-Path $root 'QRSnap.exe.config'
if (Test-Path -LiteralPath $cfg) { Copy-Item -LiteralPath $cfg -Destination $outDir -Force }

if (Test-Path -LiteralPath $out) {
    $size = [math]::Round((Get-Item -LiteralPath $out).Length / 1KB, 1)
    Write-Host ("编译完成：" + $out + "  (" + $size + " KB)") -ForegroundColor Green
} else {
    Write-Host "编译失败，日志见 build.log" -ForegroundColor Red
}
Get-Content -LiteralPath $log -Encoding UTF8 | Select-Object -Last 20
