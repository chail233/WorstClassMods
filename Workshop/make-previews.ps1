# 生成三个 Mod 的创意工坊预览图（800x600）。改文案/配色后重跑本脚本即可重新生成。
# 运行：powershell -ExecutionPolicy Bypass -File Workshop\make-previews.ps1
Add-Type -AssemblyName System.Drawing

function New-RoundedRect([float]$X, [float]$Y, [float]$W, [float]$H, [float]$R) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($X, $Y, 2 * $R, 2 * $R, 180, 90)
    $path.AddArc($X + $W - 2 * $R, $Y, 2 * $R, 2 * $R, 270, 90)
    $path.AddArc($X + $W - 2 * $R, $Y + $H - 2 * $R, 2 * $R, 2 * $R, 0, 90)
    $path.AddArc($X, $Y + $H - 2 * $R, 2 * $R, 2 * $R, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-Preview {
    param(
        [string]$OutPath, [string]$Title, [string]$Subtitle, [string]$Tag,
        [string]$ColorTop, [string]$ColorBottom,
        [scriptblock]$Decorate
    )
    $w = 800; $h = 600
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

    # 对角渐变背景
    $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, `
        ([System.Drawing.ColorTranslator]::FromHtml($ColorTop)), `
        ([System.Drawing.ColorTranslator]::FromHtml($ColorBottom)), 35.0
    $g.FillRectangle($bg, $rect)

    # 左上角来源标签
    $tagFont = New-Object System.Drawing.Font 'Microsoft YaHei', 13
    $tagBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(200, 255, 255, 255))
    $g.DrawString($Tag, $tagFont, $tagBrush, 34, 24)

    # 各自的装饰元素
    & $Decorate $g $w $h

    # 中央文字底板（半透明圆角面板，保证任何背景下可读）
    $titleFont = New-Object System.Drawing.Font 'Microsoft YaHei', 50, ([System.Drawing.FontStyle]::Bold)
    $subFont   = New-Object System.Drawing.Font 'Microsoft YaHei', 19
    $titleSize = $g.MeasureString($Title, $titleFont)
    $subSize   = $g.MeasureString($Subtitle, $subFont)
    $boxW = [Math]::Max($titleSize.Width, $subSize.Width) + 90
    $boxH = $titleSize.Height + $subSize.Height + 60
    $boxX = ($w - $boxW) / 2; $boxY = ($h - $boxH) / 2 - 20
    $panel = New-RoundedRect $boxX $boxY $boxW $boxH 26
    $panelBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(80, 0, 0, 0))
    $g.FillPath($panelBrush, $panel)
    $panelPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(120, 255, 255, 255)), 2
    $g.DrawPath($panelPen, $panel)

    # 标题与副标题
    $white = [System.Drawing.Brushes]::White
    $subBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(225, 255, 255, 255))
    $g.DrawString($Title, $titleFont, $white, `
        ($w - $titleSize.Width) / 2, $boxY + 26)
    $g.DrawString($Subtitle, $subFont, $subBrush, `
        ($w - $subSize.Width) / 2, $boxY + 26 + $titleSize.Height + 12)

    # 右下角署名
    $byFont = New-Object System.Drawing.Font 'Microsoft YaHei', 12
    $byBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(160, 255, 255, 255))
    $bySize = $g.MeasureString('by chail', $byFont)
    $g.DrawString('by chail', $byFont, $byBrush, $w - $bySize.Width - 30, $h - $bySize.Height - 22)

    $g.Dispose()
    $bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host ("已生成 {0}  ({1} bytes)" -f $OutPath, (Get-Item $OutPath).Length)
}

$root = $PSScriptRoot

# ---- PlaygroundMusic：紫粉渐变 + 均衡器声浪 + 音符 ----
$musicDeco = {
    param($g, $w, $h)
    $rnd = New-Object Random 20241115
    # 均衡器声浪（底部一排圆角竖条）
    for ($x = 56; $x -lt $w - 60; $x += 38) {
        $barH = $rnd.Next(36, 170)
        $bar = New-RoundedRect $x ($h - 40 - $barH) 22 $barH 10
        $alpha = 90 + $rnd.Next(70)
        $barBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb($alpha, 255, 255, 255))
        $g.FillPath($barBrush, $bar)
    }
    # 两枚音符（圆 + 符杆）
    $noteBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(230, 255, 209, 102))
    foreach ($p in @(140, 90), (610, 130)) {
        $g.FillEllipse($noteBrush, $p[0], $p[1], 30, 22)
        $stem = New-RoundedRect ($p[0] + 26) ($p[1] - 78) 5 80 2
        $g.FillPath($noteBrush, $stem)
    }
}

# ---- TrickMole：黑板绿 + 粉笔虚线框 + 粉笔涂鸦 ----
$chalkDeco = {
    param($g, $w, $h)
    # 粉笔虚线边框
    $chalkPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(200, 240, 240, 230)), 3
    $chalkPen.DashStyle = [System.Drawing.Drawing2D.DashStyle]::Dash
    $frame = New-RoundedRect 26 26 ($w - 52) ($h - 52) 18
    $g.DrawPath($chalkPen, $frame)
    # 粉笔涂鸦：左上 & 右下弧线，黄色重点下划线
    $whiteChalk = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(170, 255, 255, 250)), 4
    $yellowChalk = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(220, 255, 224, 102)), 5
    $g.DrawArc($whiteChalk, 60, 90, 120, 70, 200, 160)
    $g.DrawArc($whiteChalk, 200, 70, 80, 60, 150, 120)
    $g.DrawArc($whiteChalk, 560, 460, 140, 80, 20, 150)
    $g.DrawLine($yellowChalk, 330, 470, 470, 470)
    # 一颗粉笔星星（五角星多边形，顶点手工展开；PointF 需显式类型数组才能命中 DrawPolygon 重载）
    $cx = 680; $cy = 120; $R1 = 34; $R2 = 14
    $pts = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($cx, $cy - $R1),
        [System.Drawing.PointF]::new($cx + $R2 * 0.588, $cy - $R2 * 0.809),
        [System.Drawing.PointF]::new($cx + $R1 * 0.588, $cy - $R1 * 0.809),
        [System.Drawing.PointF]::new($cx + $R2 * 0.951, $cy + $R2 * 0.309),
        [System.Drawing.PointF]::new($cx + $R1 * 0.951, $cy + $R1 * 0.309),
        [System.Drawing.PointF]::new($cx, $cy + $R2),
        [System.Drawing.PointF]::new($cx, $cy + $R1),
        [System.Drawing.PointF]::new($cx - $R2 * 0.951, $cy + $R2 * 0.309),
        [System.Drawing.PointF]::new($cx - $R1 * 0.951, $cy + $R1 * 0.309),
        [System.Drawing.PointF]::new($cx - $R2 * 0.588, $cy - $R2 * 0.809),
        [System.Drawing.PointF]::new($cx - $R1 * 0.588, $cy - $R1 * 0.809)
    )
    $starPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(220, 255, 224, 102)), 4
    $g.DrawPolygon($starPen, $pts)
}

New-Preview -OutPath (Join-Path $root 'PlaygroundMusic\preview.png') `
    -Title '外高音乐扩展' -Subtitle '操场广播曲库扩展 · 自定义 MP3 · 全员曲库协商' `
    -Tag 'Worst Class Ever Mod' -ColorTop '#3A1C71' -ColorBottom '#D76D77' -Decorate $musicDeco

New-Preview -OutPath (Join-Path $root 'TrickMole\preview.png') `
    -Title '课堂角色' -Subtitle '每局随机分配身份 · 角色与数值均可配置扩展' `
    -Tag 'Worst Class Ever Mod' -ColorTop '#2E5339' -ColorBottom '#17281D' -Decorate $chalkDeco
