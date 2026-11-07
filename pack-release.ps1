$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = 'E:\下载\豆包'
$src  = Join-Path $root 'build-src'
$rel  = Join-Path $root 'release'
$tmp  = Join-Path $root '_pack'
$ver  = 'v1.4.1'

if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
if (!(Test-Path $rel)) { New-Item -ItemType Directory -Force -Path $rel | Out-Null }
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

function Stage($name) {
  $p = Join-Path $tmp $name
  New-Item -ItemType Directory -Force -Path $p | Out-Null
  return $p
}
function CopyFile($from, $toDir) {
  if (Test-Path $from) { Copy-Item $from (Join-Path $toDir (Split-Path $from -Leaf)) -Force }
}
function ZipDir($dir, $zip) {
  if (Test-Path $zip) { Remove-Item $zip -Force }
  [IO.Compression.ZipFile]::CreateFromDirectory($dir, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
  $z = Get-Item $zip
  '{0}  {1:N1} MB' -f $z.Name, ($z.Length / 1MB)
}

# ---------- 本体 ----------
$body = Stage 'body'
CopyFile (Join-Path $root 'QRSnap.exe') $body
CopyFile (Join-Path $root 'QRSnap.exe.config') $body
foreach ($f in @('Microsoft.ML.OnnxRuntime.dll','onnxruntime.dll','System.Buffers.dll','System.Memory.dll',
                 'System.Numerics.Vectors.dll','System.Runtime.CompilerServices.Unsafe.dll')) {
  CopyFile (Join-Path $root $f) $body
}
$sys = 'C:\Windows\System32'
foreach ($f in @('msvcp140.dll','msvcp140_1.dll','msvcp140_2.dll','vcruntime140.dll','vcruntime140_1.dll','concrt140.dll')) {
  CopyFile (Join-Path $sys $f) $body
}
Copy-Item (Join-Path $root 'llama') (Join-Path $body 'llama') -Recurse -Force
Copy-Item (Join-Path $root 'llama-vulkan') (Join-Path $body 'llama-vulkan') -Recurse -Force
CopyFile (Join-Path $root '_runtime\MicrosoftEdgeWebview2Setup.exe') $body
CopyFile (Join-Path $src 'README.md') $body
CopyFile (Join-Path $src '下载说明.txt') $body

$start = @(
'XuYuan-SnapScan 启动说明',
'========================',
'',
'系统要求',
'- Windows 10 21H2 / Windows 11，64 位。',
'- 本包已带 ONNX Runtime、CPU llama、Vulkan llama、VC++ 运行库。',
'- WebView2 Runtime 是界面必需项：如果启动提示缺少 WebView2，请先运行本目录的 MicrosoftEdgeWebview2Setup.exe。',
'- 建议 .NET Framework 4.8（Win10 21H2 / Win11 一般自带）。',
'',
'模型包（按需导入，光本体不会翻译）',
'- OCR 包：微信 OCR + PP-OCR 极速，导入后识字可用。',
'- PLaMo 包：PLaMo 3 Translate 0.8B Q4_K_M（效率）和 Q2_K（极速），导入后两档翻译可用。',
'- 标准包：HyMT1.5 1.8B，导入后标准档可用（所有语种）。',
'导入方式：设置 → 识别引擎 / 翻译 → 导入模型包，选择 zip 即可。',
'',
'提示',
'- 有 Vulkan 显卡驱动会自动用 GPU；没有则自动回 CPU。',
'- 截图管理员窗口需要以管理员身份运行本程序。',
'- 独占全屏游戏可能截到黑屏，建议无边框窗口。',
'- 首次运行若被 SmartScreen 拦截，选择“更多信息 → 仍要运行”。'
) -join "`r`n"
[IO.File]::WriteAllText((Join-Path $body '启动说明.txt'), $start, (New-Object Text.UTF8Encoding($false)))
ZipDir $body (Join-Path $rel "SnapScan-$ver-Body.zip")

# ---------- OCR 包 ----------
$ocr = Stage 'ocr'
$ocrModel = Join-Path $ocr 'OCR Model'
New-Item -ItemType Directory -Force -Path (Join-Path $ocrModel 'PP-OCR') | Out-Null
Copy-Item (Join-Path $root 'OCR Model\PP-OCR\tiny') (Join-Path $ocrModel 'PP-OCR\tiny') -Recurse -Force
Copy-Item (Join-Path $root 'OCR Model\WeChat') (Join-Path $ocrModel 'WeChat') -Recurse -Force
ZipDir $ocr (Join-Path $rel "SnapScan-Model-OCR-$ver.zip")

# ---------- PLaMo 翻译包：Q4_K_M + Q2_K ----------
$plamo = Stage 'plamo'
$plamoMt = Join-Path $plamo 'MT Model\Game'
New-Item -ItemType Directory -Force -Path $plamoMt | Out-Null
CopyFile (Join-Path $root 'MT Model\Game\plamo-3-translate-0.8b-Q4_K_M.gguf') $plamoMt
CopyFile (Join-Path $root 'MT Model\Game\plamo-3-translate-0.8b-Q2_K.gguf') $plamoMt
ZipDir $plamo (Join-Path $rel "SnapScan-Model-PLaMo3-Translate-0.8B-$ver.zip")

# ---------- 标准大模型包 ----------
$pro = Stage 'pro'
$proMt = Join-Path $pro 'MT Model'
New-Item -ItemType Directory -Force -Path $proMt | Out-Null
CopyFile (Join-Path $root 'MT Model\HY-MT1.5-1.8B-Q4_K_M.gguf') $proMt
ZipDir $pro (Join-Path $rel "SnapScan-Model-Pro-HyMT1.5-1.8B-$ver.zip")

# ---------- 校验和 ----------
$sha = Get-ChildItem $rel -File | Where-Object { $_.Name -notlike 'SHA256-*' } | Sort-Object Name | ForEach-Object {
  $h = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
  "$h  $($_.Name)"
}
[IO.File]::WriteAllLines((Join-Path $rel "SHA256-$ver.txt"), $sha, (New-Object Text.UTF8Encoding($false)))

Remove-Item $tmp -Recurse -Force
Get-ChildItem $rel -File | Select-Object Name, Length
