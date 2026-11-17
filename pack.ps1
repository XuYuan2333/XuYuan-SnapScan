# 发布打包：把 out\ 的构建产物 + 运行时 + 说明文档打成发布 zip，统一输出到 dist\
# 用法：
#   powershell -ExecutionPolicy Bypass -File pack.ps1            # 本体 + 全部模型包
#   powershell -ExecutionPolicy Bypass -File pack.ps1 -Only body  # 只打本体
#   powershell -ExecutionPolicy Bypass -File pack.ps1 -Only models # 只打模型包
#   powershell -ExecutionPolicy Bypass -File pack.ps1 -Only body -NoOcr   # 本体不带 OCR 模型（精简版，需要自己导入 OCR 包）
# 结构必须和程序导入逻辑对上：
#   OCR 包 = OCR Model\WeChat + OCR Model\PP-OCR
#   翻译包 = MT Model\<*.gguf>（≥50MB 才会被识别）

param(
    [ValidateSet('all','body','models')]
    [string]$Only = 'all',
    # 本体包默认就把 OCR 模型一起打进去（WeChat + PP-OCR 的全部变体：微信 / 日文 / 极速），装上就能识字，不必再导入 OCR 包
    # 想发精简本体时加 -NoOcr
    [switch]$NoOcr,
    # 兼容旧用法：显式要求带 OCR（现在默认就带，传不传都一样）
    [switch]$WithOcr
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root   = Split-Path -Parent $MyInvocation.MyCommand.Path
$outDir = Join-Path $root 'out'
$dist   = Join-Path $root 'dist'
$stage  = Join-Path $root '_packtmp'
$ver    = 'v1.4.15'

if (-not (Test-Path -LiteralPath $outDir)) { throw "找不到构建产物目录：$outDir，先跑 build19.ps1" }
if (-not (Test-Path -LiteralPath $dist))   { New-Item -ItemType Directory -Path $dist | Out-Null }

function New-Zip {
    param([string]$StageDir, [string]$ZipPath, [string]$Level = 'Optimal')
    if (Test-Path -LiteralPath $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }
    $lv = if ($Level -eq 'Fastest') { [IO.Compression.CompressionLevel]::Fastest } else { [IO.Compression.CompressionLevel]::Optimal }
    [IO.Compression.ZipFile]::CreateFromDirectory($StageDir, $ZipPath, $lv, $false)
    $item = Get-Item -LiteralPath $ZipPath
    Write-Host ("  {0}  {1:N1} MB" -f $item.Name, ($item.Length / 1MB)) -ForegroundColor Green
}

function Reset-Stage {
    param([string]$Dir)
    if (Test-Path -LiteralPath $Dir) { Remove-Item -LiteralPath $Dir -Recurse -Force }
    New-Item -ItemType Directory -Path $Dir | Out-Null
}

# ---------------- 本体 ----------------
if ($Only -eq 'all' -or $Only -eq 'body') {
    Write-Host '[本体] 组装中...'
    Reset-Stage $stage

    # 主程序与托管/原生依赖
    $files = @('QRSnap.exe','QRSnap.exe.config','JadeView_x64.dll','zxing.dll','cwebp.exe','app.ico','bg.jpg',
               'onnxruntime.dll','onnxruntime_providers_shared.dll','Microsoft.ML.OnnxRuntime.dll',
               'System.Buffers.dll','System.Memory.dll','System.Numerics.Vectors.dll',
               'System.Runtime.CompilerServices.Unsafe.dll',
               'msvcp140.dll','msvcp140_1.dll','msvcp140_2.dll','vcruntime140.dll','vcruntime140_1.dll','concrt140.dll')
    foreach ($f in $files) {
        $src = Join-Path $outDir $f
        if (-not (Test-Path -LiteralPath $src)) { $src = Join-Path $root $f }
        if (Test-Path -LiteralPath $src) { Copy-Item -LiteralPath $src -Destination $stage -Force }
        else { Write-Host ("  缺少 " + $f) -ForegroundColor Yellow }
    }

    # llama 运行时（CPU / Vulkan 两套，程序按显卡自动挑）
    foreach ($d in @('llama','llama-vulkan')) {
        $s = Join-Path $root $d
        if (Test-Path -LiteralPath $s) { Copy-Item -LiteralPath $s -Destination $stage -Recurse -Force }
        else { Write-Host ("  缺少运行库目录 " + $d) -ForegroundColor Yellow }
    }

    # WebView2 引导与说明
    foreach ($f in @('MicrosoftEdgeWebview2Setup.exe','README.md','下载说明.txt','启动说明.txt')) {
        $src = Join-Path $root $f
        if (Test-Path -LiteralPath $src) { Copy-Item -LiteralPath $src -Destination $stage -Force }
        else { Write-Host ("  缺少 " + $f) -ForegroundColor Yellow }
    }

    # OCR 模型默认跟着本体一起走（目录结构要和导入逻辑一致：OCR Model\WeChat、OCR Model\PP-OCR）
    # PP-OCR 下的全部变体（japan 日文 / tiny 极速）都要打进去
    $includeOcr = -not $NoOcr
    if ($includeOcr) {
        Write-Host '[本体] 附带 OCR 模型（微信 OCR + PP-OCR 日文/极速）...'
        $ocrDst = Join-Path $stage 'OCR Model'
        New-Item -ItemType Directory -Path $ocrDst | Out-Null
        foreach ($d in @('WeChat','PP-OCR')) {
            $s = Join-Path (Join-Path $root 'OCR Model') $d
            if (Test-Path -LiteralPath $s) { Copy-Item -LiteralPath $s -Destination $ocrDst -Recurse -Force }
            else { Write-Host ("  缺少 OCR Model\" + $d) -ForegroundColor Yellow }
        }
    }

    $bodyName = if ($includeOcr) { "SnapScan-$ver-Body.zip" } else { "SnapScan-$ver-Body-Slim.zip" }
    New-Zip -StageDir $stage -ZipPath (Join-Path $dist $bodyName) -Level 'Optimal'
}

# ---------------- 模型包 ----------------
if ($Only -eq 'all' -or $Only -eq 'models') {
    Write-Host '[模型包] 组装中...'

    # OCR：微信 OCR + PP-OCR 极速（结构 = OCR Model\WeChat、OCR Model\PP-OCR）
    Reset-Stage $stage
    $ocrDst = Join-Path $stage 'OCR Model'
    New-Item -ItemType Directory -Path $ocrDst | Out-Null
    foreach ($d in @('WeChat','PP-OCR')) {
        $s = Join-Path (Join-Path $root 'OCR Model') $d
        if (Test-Path -LiteralPath $s) { Copy-Item -LiteralPath $s -Destination $ocrDst -Recurse -Force }
        else { Write-Host ("  缺少 OCR Model\" + $d) -ForegroundColor Yellow }
    }
    New-Zip -StageDir $stage -ZipPath (Join-Path $dist 'SnapScan-Model-OCR-v1.4.1.zip') -Level 'Optimal'

    # 翻译：每个 gguf 一个包（结构 = MT Model\<gguf>）
    $mtSrc = Join-Path $root 'MT Model'
    $pairs = @(
        @{ file = 'plamo-3-translate-0.8b-Q4_K_M.gguf'; out = "SnapScan-Model-PLaMo3-Translate-0.8B-Q4_K_M-$ver.zip" },
        @{ file = 'plamo-3-translate-0.8b-Q2_K.gguf';   out = "SnapScan-Model-PLaMo3-Translate-0.8B-Q2_K-$ver.zip" },
        @{ file = 'HY-MT1.5-1.8B-Q4_K_M.gguf';         out = 'SnapScan-Model-Pro-HyMT1.5-1.8B-v1.4.1.zip' }
    ) | Where-Object { Test-Path -LiteralPath (Join-Path $mtSrc $_.file) }

    foreach ($p in $pairs) {
        Reset-Stage $stage
        $mtDst = Join-Path $stage 'MT Model'
        New-Item -ItemType Directory -Path $mtDst | Out-Null
        Copy-Item -LiteralPath (Join-Path $mtSrc $p.file) -Destination $mtDst -Force
        # gguf 本身压不动，用 Fastest 省时间
        New-Zip -StageDir $stage -ZipPath (Join-Path $dist $p.out) -Level 'Fastest'
    }

    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}

Write-Host ''
Write-Host '发布包：'
Get-ChildItem -LiteralPath $dist -Filter '*.zip' | Sort-Object Name | ForEach-Object {
    Write-Host ("  {0}  {1:N1} MB" -f $_.Name, ($_.Length / 1MB))
}
