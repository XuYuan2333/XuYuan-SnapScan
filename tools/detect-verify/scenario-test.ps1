# 游戏场景模拟窗口：日文字幕 + 英文设置列 + 设置页，按阶段切换内容与背景。
# 用途：验证「文字没变就不许动遮罩」——真换页才重译，背景乱闪时遮罩必须纹丝不动。
# 用法：pwsh .\scenario-test.ps1 -Seconds 80
#   阶段：A 日文对话（15s）→ B 英文设置页（15s）→ C 文字不变只换底色（15s）→ D 文字不变、背景每 0.6s 变色（15s）

param(
    [int]$Seconds = 90,
    [int]$Left = 320,
    [int]$Top = 160,
    [int]$Width = 1180,
    [int]$Height = 620,
    [int]$PhaseSeconds = 15,
    [int]$FlickerMs = 600,
    [switch]$NoShots
)

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class DpiFix { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); }
'@
[DpiFix]::SetProcessDPIAware() | Out-Null

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# 背景色池：D 段高频轮换，文字与位置完全不动，专门用来诱出「背景一变就撤遮罩」的老毛病
$script:bgs = @('#141824', '#1c2418', '#241820', '#182430', '#2a1810', '#101c1c')

$script:phases = @(
    @{ name = 'A-jp';      bg = '#141824'; title = 'SnapScan 场景 A（日文对话 + 英文 HUD）'; flicker = $false },
    @{ name = 'B-en';      bg = '#0d1420'; title = 'SnapScan 场景 B（英文设置页，整页换内容）'; flicker = $false },
    @{ name = 'C-same';    bg = '#2a1810'; title = 'SnapScan 场景 C（文字与 A 相同，只换底色）'; flicker = $false },
    @{ name = 'D-flicker'; bg = '#101c1c'; title = 'SnapScan 场景 D（文字不变，背景每 0.6s 变色）'; flicker = $true }
)
$script:phaseIndex = 0
$script:phaseStart = Get-Date
$script:bgIndex = 0

$jpA = @(
    '雨が降り始めた。',
    '傘を持ってきただろうか。',
    '駅前の喫茶店で少し休んでいこう。'
)
$jpB = @(
    '「もう帰ろうよ」',
    '彼女は小さくそう言った。',
    '窓の外は、まだ雨が降っている。'
)
$en = @(
    @('GUI Scale', 'Large'),
    @('Render Distance', '12 Chunks'),
    @('Simulation Distance', '8 Chunks'),
    @('VSync', 'On'),
    @('Max Framerate', 'Unlimited'),
    @('Graphics', 'Fancy'),
    @('Smooth Lighting', 'Maximum'),
    @('Particles', 'All')
)

$form = New-Object System.Windows.Forms.Form
$form.FormBorderStyle = 'None'
$form.StartPosition = 'Manual'
$form.Location = New-Object System.Drawing.Point($Left, $Top)
$form.Size = New-Object System.Drawing.Size($Width, $Height)
$form.BackColor = [System.Drawing.ColorTranslator]::FromHtml('#141824')
$form.ShowInTaskbar = $false
$form.TopMost = $true

$form.add_Paint({
    param($src, $e)
    $g = $e.Graphics
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'ClearTypeGridFit'
    $ph = $script:phases[$script:phaseIndex]
    $bgHex = if ($ph.flicker) { $script:bgs[$script:bgIndex % $script:bgs.Count] } else { $ph.bg }
    $g.Clear([System.Drawing.ColorTranslator]::FromHtml($bgHex))

    $snap = $ph.name
    $fTitle = New-Object System.Drawing.Font('Segoe UI', 13, [System.Drawing.FontStyle]::Bold)
    $g.DrawString($ph.title, $fTitle, [System.Drawing.Brushes]::LightSlateGray, 24, 18)

    if ($snap -eq 'A-jp' -or $snap -eq 'C-same' -or $snap -eq 'D-flicker') {
        # A / C / D 文字完全一致：C、D 只动背景，正好用来验「原文没变不重贴」
        $fJp = New-Object System.Drawing.Font('Yu Gothic UI', 20, [System.Drawing.FontStyle]::Regular)
        $y = 400
        foreach ($l in $jpA) {
            $g.DrawString($l, $fJp, [System.Drawing.Brushes]::WhiteSmoke, 40, $y)
            $y += 52
        }
        $fL = New-Object System.Drawing.Font('Segoe UI', 14, [System.Drawing.FontStyle]::Regular)
        $fV = New-Object System.Drawing.Font('Segoe UI', 14, [System.Drawing.FontStyle]::Bold)
        $yy = 90
        foreach ($row in $en) {
            $g.DrawString($row[0], $fL, [System.Drawing.Brushes]::Gainsboro, 620, $yy)
            $g.DrawString($row[1], $fV, [System.Drawing.Brushes]::White, 900, $yy)
            $yy += 40
        }
    }
    elseif ($snap -eq 'B-en') {
        $fL = New-Object System.Drawing.Font('Segoe UI', 16, [System.Drawing.FontStyle]::Regular)
        $fV = New-Object System.Drawing.Font('Segoe UI', 16, [System.Drawing.FontStyle]::Bold)
        $yy = 120
        foreach ($row in $en) {
            $g.DrawString($row[0], $fL, [System.Drawing.Brushes]::Gainsboro, 60, $yy)
            $g.DrawString($row[1], $fV, [System.Drawing.Brushes]::White, 620, $yy)
            $yy += 56
        }
    }
    else {
        $fJp = New-Object System.Drawing.Font('Yu Gothic UI', 20, [System.Drawing.FontStyle]::Regular)
        $y = 400
        foreach ($l in $jpB) {
            $g.DrawString($l, $fJp, [System.Drawing.Brushes]::WhiteSmoke, 40, $y)
            $y += 52
        }
    }

    $fFoot = New-Object System.Drawing.Font('Consolas', 10)
    $g.DrawString("phase=$snap bg=$bgHex t=$([int]((Get-Date)-$script:phaseStart).TotalSeconds)s", $fFoot, [System.Drawing.Brushes]::DimGray, 24, $Height - 30)
})

# 换页：放慢到 15 秒一次——翻译要时间，切太快根本来不及看到「稳定态」
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = $PhaseSeconds * 1000
$timer.add_Tick({
    $script:phaseIndex = ($script:phaseIndex + 1) % $script:phases.Count
    $script:phaseStart = Get-Date
    $form.Invalidate()
    Write-Host ("[{0}] phase -> {1}" -f (Get-Date -Format 'HH:mm:ss.fff'), $script:phases[$script:phaseIndex].name)
})

# D 段背景高频变色：0.6 秒一跳，文字一个像素都不动
$flicker = New-Object System.Windows.Forms.Timer
$flicker.Interval = $FlickerMs
$flicker.add_Tick({
    if (-not $script:phases[$script:phaseIndex].flicker) { return }
    $script:bgIndex++
    $form.Invalidate()
})

$shotTimer = New-Object System.Windows.Forms.Timer
$shotTimer.Interval = 1000
$script:shots = 0
$shotTimer.add_Tick({
    $script:shots++
    if ($NoShots) { return }
    if ($script:shots % 5 -eq 0) {
        try {
            $bmp = New-Object System.Drawing.Bitmap($Width, $Height)
            $g2 = [System.Drawing.Graphics]::FromImage($bmp)
            $g2.CopyFromScreen($Left, $Top, 0, 0, (New-Object System.Drawing.Size($Width, $Height)))
            $g2.Dispose()
            $p = Join-Path $PSScriptRoot ("shot-{0}.png" -f $script:shots)
            $bmp.Save($p, [System.Drawing.Imaging.ImageFormat]::Png)
            $bmp.Dispose()
        } catch { }
    }
})

$killTimer = New-Object System.Windows.Forms.Timer
$killTimer.Interval = $Seconds * 1000
$killTimer.add_Tick({ $form.Close() })

$form.add_Shown({
    $timer.Start()
    $flicker.Start()
    if (-not $NoShots) { $shotTimer.Start() }
    $killTimer.Start()
    Write-Host ("[{0}] window at {1},{2} size {3}x{4} phase=A-jp phase={5}s" -f (Get-Date -Format 'HH:mm:ss.fff'), $Left, $Top, $Width, $Height, $PhaseSeconds)
})

[System.Windows.Forms.Application]::Run($form)
Write-Host ("[{0}] window closed" -f (Get-Date -Format 'HH:mm:ss.fff'))
