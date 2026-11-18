$ErrorActionPreference = "Stop"
# 程序化生成 PeakCSR 专属图标 (512x512 渲染后缩放, 保证边缘平滑)
Add-Type -AssemblyName System.Drawing

$root = $PSScriptRoot
$S = 512
$outPath = Join-Path $root "icon.png"

$bmp = New-Object System.Drawing.Bitmap($S, $S)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.Clear([System.Drawing.Color]::Transparent)

function New-RoundRectPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc(($x + $w - $d), $y, $d, $d, 270, 90)
    $p.AddArc(($x + $w - $d), ($y + $h - $d), $d, $d, 0, 90)
    $p.AddArc($x, ($y + $h - $d), $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# 1. 深蓝渐变圆角底板
$pad = 10
$rect = New-Object System.Drawing.Rectangle($pad, $pad, ($S - $pad * 2), ($S - $pad * 2))
$bgPath = New-RoundRectPath $pad $pad ($S - $pad * 2) ($S - $pad * 2) 92
$bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    $rect,
    [System.Drawing.Color]::FromArgb(255, 32, 52, 78),
    [System.Drawing.Color]::FromArgb(255, 8, 15, 25),
    90)
$g.FillPath($bg, $bgPath)

# 2. 亮蓝描边
$borderPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 46, 116, 240), 14)
$g.DrawPath($borderPen, $bgPath)

# 3. 背景雪山 (暗色, 制造层次)
$backBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(96, 150, 172, 200))
$backPeak = @(
    (New-Object System.Drawing.Point(52, 250)),
    (New-Object System.Drawing.Point(150, 108)),
    (New-Object System.Drawing.Point(248, 250))
)
$g.FillPolygon($backBrush, $backPeak)

# 4. 主峰 (米白, 贴合 PEAK 木牌)
$peakBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 240, 234, 214))
$mainPeak = @(
    (New-Object System.Drawing.Point(136, 252)),
    (New-Object System.Drawing.Point(258, 66)),
    (New-Object System.Drawing.Point(380, 252))
)
$g.FillPolygon($peakBrush, $mainPeak)

# 5. 主峰右侧阴影面 (增加立体感)
$shadeBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(48, 70, 100, 140))
$shade = @(
    (New-Object System.Drawing.Point(258, 66)),
    (New-Object System.Drawing.Point(380, 252)),
    (New-Object System.Drawing.Point(258, 252))
)
$g.FillPolygon($shadeBrush, $shade)

# 6. 雪线折角
$snowPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 46, 116, 240), 11)
$snowPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$snowPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$snowPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
$g.DrawLines($snowPen, @(
    (New-Object System.Drawing.Point(216, 132)),
    (New-Object System.Drawing.Point(258, 166)),
    (New-Object System.Drawing.Point(300, 132))
))

# 7. 交叉双剑 (含刀身/护手/握柄/柄头)
function Draw-Sword([System.Drawing.Graphics]$gfx, [System.Drawing.PointF]$tip, [System.Drawing.PointF]$pommel) {
    $dx = $pommel.X - $tip.X
    $dy = $pommel.Y - $tip.Y
    $len = [Math]::Sqrt($dx * $dx + $dy * $dy)
    $ux = $dx / $len
    $uy = $dy / $len
    # 垂直单位向量
    $px = -$uy
    $py = $ux

    $steel = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 226, 236, 248))
    $gold = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 190, 64))
    $dark = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 96, 62, 34))
    $edgePen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 255, 107, 43), 5)

    # 刀身: 从剑尖到护手位置(全长 66%)
    $guardT = 0.66
    $gx = $tip.X + $dx * $guardT
    $gy = $tip.Y + $dy * $guardT
    $halfW = 15.0

    $blade = @(
        (New-Object System.Drawing.PointF($tip.X, $tip.Y)),
        (New-Object System.Drawing.PointF(($gx + $px * $halfW), ($gy + $py * $halfW))),
        (New-Object System.Drawing.PointF(($gx - $px * $halfW), ($gy - $py * $halfW)))
    )
    $gfx.FillPolygon($steel, $blade)

    # 刀身中脊高光
    $midPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(180, 255, 255, 255), 3)
    $gfx.DrawLine($midPen, $tip.X, $tip.Y, $gx, $gy)

    # 护手 (垂直于刀身的金色横条)
    $gh = 46.0
    $gt = 13.0
    $guard = @(
        (New-Object System.Drawing.PointF(($gx + $px * $gh), ($gy + $py * $gh))),
        (New-Object System.Drawing.PointF(($gx - $px * $gh), ($gy - $py * $gh))),
        (New-Object System.Drawing.PointF(($gx - $px * $gh - $ux * $gt), ($gy - $py * $gh - $uy * $gt))),
        (New-Object System.Drawing.PointF(($gx + $px * $gh - $ux * $gt), ($gy + $py * $gh - $uy * $gt)))
    )
    $gfx.FillPolygon($gold, $guard)

    # 握柄 (深棕)
    $handleEnd = $pommel
    $hx = $gx - $ux * 12
    $hy = $gy - $uy * 12
    $hwid = 11.0
    $handle = @(
        (New-Object System.Drawing.PointF(($hx + $px * $hwid), ($hy + $py * $hwid))),
        (New-Object System.Drawing.PointF(($hx - $px * $hwid), ($hy - $py * $hwid))),
        (New-Object System.Drawing.PointF(($handleEnd.X - $px * $hwid), ($handleEnd.Y - $py * $hwid))),
        (New-Object System.Drawing.PointF(($handleEnd.X + $px * $hwid), ($handleEnd.Y + $py * $hwid)))
    )
    $gfx.FillPolygon($dark, $handle)

    # 柄头 (金色圆球)
    $gfx.FillEllipse($gold, ($pommel.X - 15), ($pommel.Y - 15), 30, 30)

    # 橙色描边强化轮廓
    $gfx.DrawPolygon($edgePen, $blade)
}

# 两把剑交叉, 剑尖朝上外张 (整体略上移, 与底部长标之间留白)
Draw-Sword $g (New-Object System.Drawing.PointF(156, 200)) (New-Object System.Drawing.PointF(356, 358))
Draw-Sword $g (New-Object System.Drawing.PointF(356, 200)) (New-Object System.Drawing.PointF(156, 358))

# 8. PCSR 字标
$fontFamily = New-Object System.Drawing.FontFamily("Impact")
$font = New-Object System.Drawing.Font($fontFamily, 78, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$textBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 214, 102))
$format = New-Object System.Drawing.StringFormat
$format.Alignment = [System.Drawing.StringAlignment]::Center
$format.LineAlignment = [System.Drawing.StringAlignment]::Center

# 文字描边 (深蓝), 先描后填
$outlineBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 12, 26, 48))
$textRect = New-Object System.Drawing.RectangleF(0, 392, $S, 96)
foreach ($ox in @(-4, 0, 4)) {
    foreach ($oy in @(-4, 0, 4)) {
        if ($ox -eq 0 -and $oy -eq 0) { continue }
        $r = New-Object System.Drawing.RectangleF($ox, (392 + $oy), $S, 96)
        $g.DrawString("PCSR", $font, $outlineBrush, $r, $format)
    }
}
$g.DrawString("PCSR", $font, $textBrush, $textRect, $format)

$g.Dispose()

# 9. 缩放到 256x256 输出, 抗锯齿更平滑
$final = New-Object System.Drawing.Bitmap(256, 256)
$fg = [System.Drawing.Graphics]::FromImage($final)
$fg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$fg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$fg.Clear([System.Drawing.Color]::Transparent)
$fg.DrawImage($bmp, 0, 0, 256, 256)
$fg.Dispose()

$final.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
$final.Dispose()
$bmp.Dispose()

Write-Host "[PeakCSR] Logo generated: $outPath (256x256)" -ForegroundColor Green
