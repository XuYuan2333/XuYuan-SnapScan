$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Split-Path -Parent (Split-Path -Parent $root)   # tools\detect-verify → 项目根
$bin = Join-Path $root "bin"
if (-not (Test-Path -LiteralPath $bin)) { New-Item -ItemType Directory -Path $bin | Out-Null }

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { $csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }

$refs = @(
  "/r:$proj\Microsoft.ML.OnnxRuntime.dll", "/r:$proj\System.Memory.dll",
  "/r:$proj\System.Buffers.dll", "/r:$proj\System.Numerics.Vectors.dll",
  "/r:$proj\System.Runtime.CompilerServices.Unsafe.dll",
  "/r:System.dll", "/r:System.Core.dll", "/r:System.Drawing.dll",
  "/r:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\netstandard.dll"
)
$srcs = @((Join-Path $root 'AppConfigStub.cs'), (Join-Path $proj 'PpOcr.cs'),
          (Join-Path $proj 'AutoRegion.cs'), (Join-Path $root 'Verify.cs'))
$out = Join-Path $bin 'Verify.exe'
& $csc (@('/nologo','/target:exe','/platform:x64','/optimize+','/codepage:65001',"/out:$out") + $refs + $srcs)
if ($LASTEXITCODE -ne 0) { Write-Host "verify 编译失败"; exit $LASTEXITCODE }

foreach ($d in @('onnxruntime.dll','onnxruntime_providers_shared.dll','Microsoft.ML.OnnxRuntime.dll',
  'System.Memory.dll','System.Buffers.dll','System.Numerics.Vectors.dll','System.Runtime.CompilerServices.Unsafe.dll'))
{
  $f = Join-Path $proj $d
  if (-not (Test-Path -LiteralPath $f)) { $f = Join-Path (Join-Path $proj 'out') $d }
  if (Test-Path -LiteralPath $f) { Copy-Item -LiteralPath $f -Destination $bin -Force }
}
foreach ($d in @('concrt140.dll','msvcp140.dll','msvcp140_1.dll','msvcp140_2.dll','vcruntime140.dll','vcruntime140_1.dll'))
{
  $f = Join-Path (Join-Path $proj 'out') $d
  if (Test-Path -LiteralPath $f) { Copy-Item -LiteralPath $f -Destination $bin -Force }
}
$cfg = Join-Path $proj 'QRSnap.exe.config'
if (Test-Path -LiteralPath $cfg) { Copy-Item -LiteralPath $cfg -Destination (Join-Path $bin 'Verify.exe.config') -Force }
Write-Host "verify 编译完成：$out"
