$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$src  = 'E:\下载\豆包\build-src'
$rel  = 'E:\下载\豆包\release'
$body = Join-Path $rel 'SnapScan-v1.1.9-Body.zip'
$stage = 'E:\下载\豆包\_bodytmp'
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

Copy-Item -LiteralPath 'E:\下载\豆包\QRSnap.exe' -Destination $stage -Force
Copy-Item -LiteralPath 'E:\下载\豆包\QRSnap.exe.config' -Destination $stage -Force
Copy-Item -LiteralPath (Join-Path $src 'README.md') -Destination $stage -Force
Copy-Item -LiteralPath (Join-Path $src '下载说明.txt') -Destination $stage -Force

if (Test-Path -LiteralPath $body) { Remove-Item -LiteralPath $body -Force }
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $body, [IO.Compression.CompressionLevel]::Optimal, $false)
$b = Get-Item -LiteralPath $body
"BODY {0} bytes  {1}" -f $b.Length, $body
