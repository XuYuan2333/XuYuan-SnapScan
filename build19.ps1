Stop-Process -Name QRSnap -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
Push-Location 'E:\下载\豆包\build-src'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$a = @('/nologo','/target:winexe','/platform:x64','/optimize+','/codepage:65001','/win32icon:app.ico','/win32manifest:app.manifest',
'/resource:zxing.dll,ZXing.Net.dll','/resource:bg.jpg,Back.jpg','/resource:app.ico,App.ico',
'/resource:web\index.html,WEB_INDEX','/resource:web\app.css,WEB_CSS','/resource:web\app.js,WEB_JS',
'/resource:E:\下载\豆包\build-src\JadeView_x64.dll,JADEVIEWDLL',
'/r:zxing.dll','/r:System.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Core.dll',
'/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll',
'/r:E:\下载\豆包\build-src\Microsoft.ML.OnnxRuntime.dll','/r:E:\下载\豆包\build-src\System.Memory.dll',
'/r:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\netstandard.dll',
'/out:E:\下载\豆包\QRSnap.exe','src.cs','ui_jade.cs','PpOcr.cs','Mt.cs','LlamaMt.cs','TransOverlay.cs','MtRealistic.cs','AiTextFix.cs','AiBoost.cs','LayoutBoost.cs','ShotCache.cs','RuntimeCheck.cs','GameMode.cs')
& $csc $a *> 'E:\下载\豆包\build.log'
Pop-Location
Get-Content -LiteralPath 'E:\下载\豆包\build.log' -Encoding UTF8 | Select-Object -Last 20
