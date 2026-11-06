$ErrorActionPreference = "Stop"
$root = "C:\Users\18371\Doubao\chats\2026-11-06\new-chat"
$rel  = Join-Path $root "XuYuan-SnapScan-发布"
$out  = Join-Path $root "分引擎发布"
if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
New-Item -ItemType Directory -Force -Path $out | Out-Null

$core = @(
  "QRSnap.exe", "QRSnap.exe.config",
  "XuYuan-SnapScan.ico", "README.md", "下载说明.txt"
)
# 只有 PP-OCR 走 onnxruntime；微信 OCR / Windows OCR 不需要，包里就不放这 11.5 MB
$coreOnnx = @(
  "Microsoft.ML.OnnxRuntime.dll", "onnxruntime.dll",
  "System.Memory.dll", "System.Buffers.dll",
  "System.Numerics.Vectors.dll", "System.Runtime.CompilerServices.Unsafe.dll"
)

$pkgs = @(
  @{ Name = "XuYuan-SnapScan-v1.1.6-微信OCR";        Models = @("WeChat"); Onnx = $false },
  @{ Name = "XuYuan-SnapScan-v1.1.6-PP-OCR-极速";    Models = @("PP-OCR\tiny"); Onnx = $true },
  @{ Name = "XuYuan-SnapScan-v1.1.6-PP-OCR-标准";    Models = @("PP-OCR\small"); Onnx = $true },
  @{ Name = "XuYuan-SnapScan-v1.1.6-PP-OCR-高精";    Models = @("PP-OCR\medium"); Onnx = $true },
  @{ Name = "XuYuan-SnapScan-v1.1.6-仅WindowsOCR";   Models = @(); Onnx = $false }
)

foreach ($p in $pkgs) {
  $dir = Join-Path $out $p.Name
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  $files = $core
  if ($p.Onnx) { $files = $core + $coreOnnx }
  foreach ($f in $files) {
    $src = Join-Path $rel $f
    if (Test-Path -LiteralPath $src) { Copy-Item -LiteralPath $src -Destination (Join-Path $dir $f) -Force }
  }
  New-Item -ItemType Directory -Force -Path (Join-Path $dir "OCR Model") | Out-Null
  foreach ($m in $p.Models) {
    $src = Join-Path (Join-Path $rel "OCR Model") $m
    $dst = Join-Path (Join-Path $dir "OCR Model") $m
    $parent = Split-Path -Parent $dst
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    Copy-Item -LiteralPath $src -Destination $dst -Recurse -Force
  }
  $mb = [math]::Round((Get-ChildItem -LiteralPath $dir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
  Write-Output ("packed {0} : {1} MB" -f $p.Name, $mb)
}
