$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$stage = 'E:\下载\豆包\_stage'
$rel   = 'E:\下载\豆包\release'
$log   = 'E:\下载\豆包\pack.log'
if (-not (Test-Path -LiteralPath $rel)) { New-Item -ItemType Directory -Force -Path $rel | Out-Null }
$tmp = 'E:\下载\豆包\_packtmp'
if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Recurse -Force }
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
Set-Content -LiteralPath $log -Value '' -Encoding ASCII

$pairs = @(
  @{ key = 'WeChatOCR'; out = 'SnapScan-Model-WeChatOCR-v1.1.8.zip' },
  @{ key = 'Tiny';      out = 'SnapScan-Model-PP-OCR-Tiny-v1.1.8.zip' },
  @{ key = 'Medium';    out = 'SnapScan-Model-PP-OCR-Medium-v1.1.8.zip' }
)

foreach ($p in $pairs) {
  $work = Join-Path $stage ('w_' + $p.key)
  $md = Get-ChildItem -LiteralPath $work -Recurse -Directory | Where-Object { $_.Name -eq 'OCR Model' } | Select-Object -First 1
  if (-not $md) { Add-Content -LiteralPath $log -Value ("MISSING " + $p.key); continue }

  $sub = Get-ChildItem -LiteralPath $md.FullName -Directory
  $subNames = ($sub | ForEach-Object { $_.Name }) -join ','
  $rawSize = (Get-ChildItem -LiteralPath $md.FullName -Recurse -File | Measure-Object -Property Length -Sum).Sum

  $stageRoot = Join-Path $tmp $p.key
  if (Test-Path -LiteralPath $stageRoot) { Remove-Item -LiteralPath $stageRoot -Recurse -Force }
  $modelDir = Join-Path $stageRoot 'OCR Model'
  New-Item -ItemType Directory -Force -Path $modelDir | Out-Null
  foreach ($d in $sub) {
    Copy-Item -LiteralPath $d.FullName -Destination $modelDir -Recurse -Force
  }

  # PP-OCR 要用 onnxruntime 推理，把这套库单独放 Runtime 目录跟着模型包走（本体因此能保持很小）
  if ($p.key -ne 'WeChatOCR') {
    $rt = Join-Path $stageRoot 'Runtime'
    New-Item -ItemType Directory -Force -Path $rt | Out-Null
    $topPkg = Get-ChildItem -LiteralPath $work -Directory | Select-Object -First 1
    foreach ($fn in @('Microsoft.ML.OnnxRuntime.dll','onnxruntime.dll','System.Buffers.dll','System.Memory.dll','System.Numerics.Vectors.dll','System.Runtime.CompilerServices.Unsafe.dll','QRSnap.exe.config')) {
      $srcFile = Join-Path $topPkg.FullName $fn
      if (Test-Path -LiteralPath $srcFile) { Copy-Item -LiteralPath $srcFile -Destination $rt -Force }
      else { Add-Content -LiteralPath $log -Value ('  RUNTIME MISSING ' + $fn) }
    }
  }

  $outPath = Join-Path $rel $p.out
  if (Test-Path -LiteralPath $outPath) { Remove-Item -LiteralPath $outPath -Force }
  [IO.Compression.ZipFile]::CreateFromDirectory($stageRoot, $outPath, [IO.Compression.CompressionLevel]::Optimal, $false)
  $zipSize = (Get-Item -LiteralPath $outPath).Length
  Add-Content -LiteralPath $log -Value ($p.key + ' | sub=' + $subNames + ' | raw=' + $rawSize + ' | zip=' + $zipSize + ' | ' + $p.out)
}
Add-Content -LiteralPath $log -Value 'PACK DONE'
