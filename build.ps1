# 一键重新编译 QRSnap.exe
# 只用到 Windows 自带的 .NET Framework 4.x 编译器（csc.exe），不需要装 Visual Studio。
# 用法：右键本文件 -> 使用 PowerShell 运行；或在命令行执行  powershell -ExecutionPolicy Bypass -File build.ps1

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$out  = Join-Path (Split-Path -Parent $root) "QRSnap.exe"

$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path -LiteralPath $csc)) {
    $csc = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
}
if (-not (Test-Path -LiteralPath $csc)) {
    Write-Host "找不到 .NET Framework 4.x 编译器 csc.exe" -ForegroundColor Red
    exit 1
}

# 切到源码目录，用相对路径，避免路径含空格时 /resource 参数解析出错
Push-Location -LiteralPath $root
try {
    & $csc /nologo /target:winexe /platform:anycpu /optimize+ `
        /win32icon:app.ico `
        /win32manifest:app.manifest `
        /resource:zxing.dll,ZXing.Net.dll `
        /resource:bg.jpg,Back.jpg `
        /resource:app.ico,App.ico `
        /r:zxing.dll /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
        /out:"$out" src.cs
} finally {
    Pop-Location
}

if (Test-Path -LiteralPath $out) {
    $size = [math]::Round((Get-Item -LiteralPath $out).Length / 1KB, 1)
    Write-Host ("编译完成：" + $out + "  (" + $size + " KB)") -ForegroundColor Green
} else {
    Write-Host "编译失败" -ForegroundColor Red
}
