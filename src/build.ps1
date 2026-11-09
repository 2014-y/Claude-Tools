# 生成图标并编译 exe（只用 Windows 自带的 .NET Framework，无需安装任何东西）
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$outDir = Split-Path $src -Parent
Add-Type -AssemblyName System.Drawing

# 主图标（按设计稿：橙红渐变底、戴皇冠的白色像素小怪物、两条环绕切换箭头、感叹号与像素点）
function New-MasterBitmap {
    $N = 1024
    $bmp = New-Object Drawing.Bitmap $N, $N
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.Clear([Drawing.Color]::Transparent)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.CompositingQuality = 'HighQuality'
    $g.ScaleTransform($N / 1254.0, $N / 1254.0)
    function C($a, $r, $gg, $bb) { [Drawing.Color]::FromArgb($a, $r, $gg, $bb) }
    function Pt($x, $y) { New-Object Drawing.PointF ([single]$x), ([single]$y) }
    # 背景
    $bg = New-Object Drawing.Drawing2D.GraphicsPath
    $rr = 250
    $bg.AddArc(0, 0, 2*$rr, 2*$rr, 180, 90); $bg.AddArc(1254 - 2*$rr, 0, 2*$rr, 2*$rr, 270, 90)
    $bg.AddArc(1254 - 2*$rr, 1254 - 2*$rr, 2*$rr, 2*$rr, 0, 90); $bg.AddArc(0, 1254 - 2*$rr, 2*$rr, 2*$rr, 90, 90); $bg.CloseFigure()
    $g.FillPath((New-Object Drawing.Drawing2D.LinearGradientBrush (Pt 0 0), (Pt 1254 1254), (C 255 255 120 45), (C 255 236 52 10)), $bg)
    $g.SetClip($bg)
    $glow = New-Object Drawing.Drawing2D.GraphicsPath; $glow.AddEllipse(180, 200, 900, 860)
    $pg = New-Object Drawing.Drawing2D.PathGradientBrush $glow
    $pg.CenterColor = C 90 255 60 0; $pg.SurroundColors = @((C 0 255 60 0))
    $g.FillPath($pg, $glow)
    # 弧线尾迹（金黄）
    function Arc($p0, $p1, $p2, $p3, $w, $col) {
        $pen = New-Object Drawing.Pen $col, $w; $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
        $g.DrawBezier($pen, $p0, $p1, $p2, $p3)
    }
    Arc (Pt 175 640) (Pt 60 470) (Pt 260 250) (Pt 470 225) 40 (C 170 255 176 40)
    Arc (Pt 430 900) (Pt 150 900) (Pt 90 760) (Pt 300 650) 52 (C 190 255 214 110)
    Arc (Pt 960 410) (Pt 1170 400) (Pt 1190 560) (Pt 960 690) 46 (C 170 255 190 60)
    Arc (Pt 1090 700) (Pt 1180 860) (Pt 1000 990) (Pt 600 1080) 30 (C 120 255 150 30)
    # 箭头（奶黄渐变）
    function ArrowBody($p0, $p1, $p2, $p3, $w) {
        $br = New-Object Drawing.Drawing2D.LinearGradientBrush (Pt 100 100), (Pt 1150 1150), (C 255 255 246 214), (C 255 255 210 100)
        $pen = New-Object Drawing.Pen $br, $w; $pen.StartCap = 'Round'; $pen.EndCap = 'Flat'
        $g.DrawBezier($pen, $p0, $p1, $p2, $p3)
    }
    $headBr = New-Object Drawing.Drawing2D.LinearGradientBrush (Pt 400 150), (Pt 900 1100), (C 255 255 244 205), (C 255 255 205 90)
    ArrowBody (Pt 165 610) (Pt 150 390) (Pt 330 255) (Pt 500 255) 100
    $g.FillPolygon($headBr, [Drawing.PointF[]]@((Pt 478 150), (Pt 650 255), (Pt 490 360)))
    ArrowBody (Pt 1095 660) (Pt 1120 870) (Pt 950 975) (Pt 790 975) 100
    $g.FillPolygon($headBr, [Drawing.PointF[]]@((Pt 800 870), (Pt 615 968), (Pt 808 1075)))
    # 像素小方块
    $sq = New-Object Drawing.SolidBrush (C 235 255 222 130)
    foreach ($q in @(@(122,580,40), @(178,622,36), @(472,950,44), @(538,1000,38), @(1110,650,40), @(1063,708,18))) { $g.FillRectangle($sq, $q[0], $q[1], $q[2], $q[2]) }
    # 感叹号
    $ex = New-Object Drawing.SolidBrush (C 255 255 214 90)
    foreach ($e in @(@(905, 228, 50, 105, 18), @(985, 278, 44, 100, 45))) {
        $st = $g.Save(); $g.TranslateTransform($e[0], $e[1]); $g.RotateTransform($e[4])
        $g.FillRectangle($ex, -$e[2]/2, -$e[3]/2, $e[2], $e[3]); $g.Restore($st)
    }
    # 小怪物（整体倾斜）+ 皇冠
    $st = $g.Save()
    $g.TranslateTransform(632, 640); $g.RotateTransform(8); $g.ScaleTransform(0.9, 0.9); $g.TranslateTransform(-627, -615)
    function Body($brush, $dx, $dy) {
        foreach ($r in @(@(340,348,915,758), @(183,494,1072,617), @(340,758,415,883), @(495,758,570,883), @(685,758,760,883), @(840,758,915,883))) {
            $g.FillRectangle($brush, $r[0] + $dx, $r[1] + $dy, $r[2] - $r[0], $r[3] - $r[1])
        }
    }
    Body (New-Object Drawing.SolidBrush (C 110 150 30 0)) 18 26          # 投影
    Body (New-Object Drawing.SolidBrush (C 255 246 205 196)) 0 16        # 立体侧边
    Body (New-Object Drawing.SolidBrush (C 255 255 255 255)) 0 0
    $eye = New-Object Drawing.SolidBrush (C 255 255 70 18)
    $g.FillRectangle($eye, 415, 410, 85, 83); $g.FillRectangle($eye, 755, 410, 83, 83)
    $crown = [Drawing.PointF[]]@((Pt 590 352), (Pt 572 215), (Pt 660 282), (Pt 742 168), (Pt 822 278), (Pt 905 212), (Pt 895 352))
    $g.FillPolygon((New-Object Drawing.SolidBrush (C 255 230 150 20)), [Drawing.PointF[]]($crown | % { Pt $_.X ($_.Y + 14) }))
    $g.FillPolygon((New-Object Drawing.Drawing2D.LinearGradientBrush (Pt 600 200), (Pt 870 350), (C 255 255 232 120), (C 255 255 186 40)), $crown)
    $g.FillEllipse((New-Object Drawing.SolidBrush (C 255 255 226 110)), 710, 118, 64, 64)
    $g.Restore($st)
    $g.Dispose()
    return $bmp
}
$script:Master = $null
$UserIcon = Join-Path $src 'icon.png'
function New-IconPng([int]$size) {
    if (Test-Path $UserIcon) {
        $img = [Drawing.Image]::FromFile($UserIcon)
        $bmp = New-Object Drawing.Bitmap $size, $size
        $g = [Drawing.Graphics]::FromImage($bmp)
        $g.Clear([Drawing.Color]::Transparent)
        $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
        $s = [double]$size; $r = $s * 0.2
        $path = New-Object Drawing.Drawing2D.GraphicsPath
        $path.AddArc(0, 0, 2*$r, 2*$r, 180, 90); $path.AddArc($s - 2*$r - 0.01, 0, 2*$r, 2*$r, 270, 90)
        $path.AddArc($s - 2*$r - 0.01, $s - 2*$r - 0.01, 2*$r, 2*$r, 0, 90); $path.AddArc(0, $s - 2*$r - 0.01, 2*$r, 2*$r, 90, 90)
        $path.CloseFigure(); $g.SetClip($path)
        $g.DrawImage($img, 0, 0, $size, $size)
        $g.Dispose(); $img.Dispose()
        $ms = New-Object IO.MemoryStream
        $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
        return ,$ms.ToArray()
    }
    if ($size -ge 48) {
        if (-not $script:Master) { $script:Master = New-MasterBitmap }
        $bmp = New-Object Drawing.Bitmap $size, $size
        $g = [Drawing.Graphics]::FromImage($bmp)
        $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'; $g.SmoothingMode = 'AntiAlias'
        $g.DrawImage($script:Master, 0, 0, $size, $size); $g.Dispose()
        $ms = New-Object IO.MemoryStream
        $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
        return ,$ms.ToArray()
    }
    # 小尺寸：简化成橙底白色像素小怪物，保证清晰
    $bmp = New-Object Drawing.Bitmap $size, $size
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.Clear([Drawing.Color]::Transparent)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $s = [double]$size; $r = $s * 0.2
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, 2*$r, 2*$r, 180, 90); $path.AddArc($s - 2*$r - 0.01, 0, 2*$r, 2*$r, 270, 90)
    $path.AddArc($s - 2*$r - 0.01, $s - 2*$r - 0.01, 2*$r, 2*$r, 0, 90); $path.AddArc(0, $s - 2*$r - 0.01, 2*$r, 2*$r, 90, 90)
    $path.CloseFigure()
    $orange = [Drawing.Color]::FromArgb(244, 99, 58)
    $grad = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.RectangleF 0,0,$s,$s), ([Drawing.Color]::FromArgb(255,122,51)), ([Drawing.Color]::FromArgb(242,61,15)), 45.0
    $g.FillPath($grad, $path)
    $g.SmoothingMode = 'None'; $g.PixelOffsetMode = 'Half'
    $k = $s / 1254.0
    $white = New-Object Drawing.SolidBrush ([Drawing.Color]::White)
    $hole = New-Object Drawing.SolidBrush $orange
    function Add-Px($x1, $y1, $x2, $y2, $br) {
        $x = [Math]::Round($x1 * $k); $y = [Math]::Round($y1 * $k)
        $w = [Math]::Max(1, [Math]::Round($x2 * $k) - $x); $h = [Math]::Max(1, [Math]::Round($y2 * $k) - $y)
        $g.FillRectangle($br, [single]$x, [single]$y, [single]$w, [single]$h)
    }
    Add-Px 340 348 915 758 $white          # 身体
    Add-Px 183 494 1072 617 $white         # 两侧手臂
    foreach ($leg in @(340, 495, 685, 840)) { Add-Px $leg 758 ($leg + 75) 883 $white }   # 四条腿
    if ($size -ge 24) { Add-Px 415 410 500 493 $hole; Add-Px 755 410 838 493 $hole }          # 眼睛
    else { Add-Px 400 400 500 500 $hole; Add-Px 755 400 855 500 $hole }
    $g.Dispose()
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    return ,$ms.ToArray()
}

# 写多尺寸 ICO（PNG 编码）
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($sz in $sizes) { ,(New-IconPng $sz) }
$ico = New-Object IO.MemoryStream
$bw = New-Object IO.BinaryWriter $ico
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $len = $pngs[$i].Length
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz }))); $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset); $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Flush()
[IO.File]::WriteAllBytes((Join-Path $src 'app.ico'), $ico.ToArray())
[IO.File]::WriteAllBytes((Join-Path $src 'icon-256.png'), $pngs[-1])

$exeName = (Get-ChildItem $outDir -Filter *.exe | Select-Object -First 1)
$out = if ($exeName) { $exeName.FullName } else { Join-Path $outDir 'ClaudeSwitcher.exe' }
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /codepage:65001 /target:winexe /optimize+ "/out:$out" "/win32icon:$(Join-Path $src 'app.ico')" `
    "/resource:$(Join-Path $src 'ui.html'),ui.html" "/resource:$(Join-Path $src 'app.ico'),app.ico" "/resource:$(Join-Path $src 'icon-256.png'),logo.png" "/resource:$(Join-Path $src 'fonts\zcool-kuaile.woff2'),font.woff2" `
    "/resource:$(Join-Path $src 'lib\webview2\Microsoft.Web.WebView2.Core.dll'),Microsoft.Web.WebView2.Core.dll" `
    "/resource:$(Join-Path $src 'lib\webview2\Microsoft.Web.WebView2.WinForms.dll'),Microsoft.Web.WebView2.WinForms.dll" `
    "/resource:$(Join-Path $src 'lib\webview2\x64\WebView2Loader.dll'),WebView2Loader.x64.dll" `
    "/resource:$(Join-Path $src 'lib\webview2\x86\WebView2Loader.dll'),WebView2Loader.x86.dll" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.Web.Extensions.dll /r:System.Management.dll /r:System.Security.dll /r:"$(Join-Path $src 'lib\webview2\Microsoft.Web.WebView2.Core.dll')" /r:"$(Join-Path $src 'lib\webview2\Microsoft.Web.WebView2.WinForms.dll')" `
    (Join-Path $src 'ClaudeSwitcher.cs') (Join-Path $src 'Desktop.cs') (Join-Path $src 'Server.cs') (Join-Path $src 'Extras.cs') (Join-Path $src 'Usage.cs') (Join-Path $src 'Live.cs') (Join-Path $src 'AppWindow.cs')
if ($LASTEXITCODE -ne 0) { throw "编译失败" }
Write-Host "OK -> $out"

# ---- 安装程序（内嵌上面编译好的 exe）----
$setupOut = Join-Path $outDir 'Claude-Tools-Setup.exe'
& $csc /nologo /codepage:65001 /target:winexe /optimize+ "/out:$setupOut" "/win32icon:$(Join-Path $src 'app.ico')" `
    "/resource:$out,app.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll (Join-Path $src 'Setup.cs')
if ($LASTEXITCODE -ne 0) { throw "安装程序编译失败" }
Write-Host "OK -> $setupOut"