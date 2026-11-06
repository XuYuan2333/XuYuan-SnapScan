$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$rel   = 'E:\下载\豆包\release'
$stage = 'E:\下载\豆包\_mtpack'
$zip   = Join-Path $rel 'SnapScan-Model-HyMT1.5-v1.1.9.zip'
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'MT Model') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'llama')    | Out-Null

Copy-Item -LiteralPath 'E:\下载\豆包\MT Model\HY-MT1.5-1.8B-Q4_K_M.gguf' -Destination (Join-Path $stage 'MT Model') -Force
Copy-Item -Path 'E:\下载\豆包\llama\*' -Destination (Join-Path $stage 'llama') -Recurse -Force
# 模型许可文件跟着一起发
$lic = 'E:\下载\豆包\_mt\llama\lic.txt'
if ((Test-Path -LiteralPath $lic) -and ((Get-Item -LiteralPath $lic).Length -gt 0)) {
    Copy-Item -LiteralPath $lic -Destination (Join-Path $stage 'MT Model\HY-MT1.5-1.8B-Q4_K_M-License.txt') -Force
}

if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
$z = Get-Item -LiteralPath $zip
"MTZIP {0} bytes ({1:N1} MB)  {2}" -f $z.Length, ($z.Length/1MB), $zip
