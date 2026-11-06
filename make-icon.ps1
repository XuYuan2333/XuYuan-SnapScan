Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$icoPath = Join-Path $root 'app.ico'

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngBytes = @()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $m = [float]([math]::Round($s * 0.045))
    $r = [float]([math]::Round($s * 0.18))
    $w = [float]($s - 2 * $m)
    $h = [float]($s - 2 * $m)

    # 圆角蓝色底
    $gp = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $gp.AddArc($m, $m, $d, $d, 180, 90)
    $gp.AddArc($m + $w - $d, $m, $d, $d, 270, 90)
    $gp.AddArc($m + $w - $d, $m + $h - $d, $d, $d, 0, 90)
    $gp.AddArc($m, $m + $h - $d, $d, $d, 90, 90)
    $gp.CloseFigure()
    $bgBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(47, 111, 224))
    $g.FillPath($bgBrush, $gp)
    $bgBrush.Dispose()

    $white = [System.Drawing.Brushes]::White

    # 三个定位角（类二维码取景框）
    $pad = [float]($s * 0.20)
    $box = [float]($s - 2 * $pad)
    $arm = [float]($box * 0.45)
    $th = [float]([math]::Max(1.2, $s * 0.075))

    # 左上
    $g.FillRectangle($white, $pad, $pad, $arm, $th)
    $g.FillRectangle($white, $pad, $pad, $th, $arm)
    # 右上
    $g.FillRectangle($white, $pad + $box - $arm, $pad, $arm, $th)
    $g.FillRectangle($white, $pad + $box - $th, $pad, $th, $arm)
    # 左下
    $g.FillRectangle($white, $pad, $pad + $box - $th, $arm, $th)
    $g.FillRectangle($white, $pad, $pad + $box - $arm, $th, $arm)

    # 右下小模块点阵
    $dot = [float]([math]::Round($s * 0.085))
    $gap = [float]([math]::Round($s * 0.055))
    $x0 = [float]($pad + $box - $arm)
    $y0 = [float]($pad + $box - $arm)
    for ($i = 0; $i -lt 3; $i++) {
        for ($j = 0; $j -lt 3; $j++) {
            if ((($i + $j) % 2) -eq 0 -or $s -ge 32) {
                $x = [float]($x0 + $i * ($dot + $gap))
                $y = [float]($y0 + $j * ($dot + $gap))
                if ($x + $dot -le $pad + $box -and $y + $dot -le $pad + $box) {
                    $g.FillRectangle($white, $x, $y, $dot, $dot)
                }
            }
        }
    }

    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngBytes += , ($ms.ToArray())
    $ms.Dispose()

    if ($s -eq 256) { $bmp.Save((Join-Path $root 'icon-256.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
}

# 组装 ICO（PNG 负载，Vista+ 支持）
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($out)
$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$sizes.Count)

$offset = 6 + 16 * $sizes.Count
for ($k = 0; $k -lt $sizes.Count; $k++) {
    $s = $sizes[$k]
    $data = $pngBytes[$k]
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
for ($k = 0; $k -lt $sizes.Count; $k++) {
    $bw.Write($pngBytes[$k])
}
$bw.Flush()
[System.IO.File]::WriteAllBytes($icoPath, $out.ToArray())
$bw.Dispose()
$out.Dispose()

"icon written: " + (Get-Item -LiteralPath $icoPath).Length + " bytes"
