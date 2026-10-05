Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap(64,64)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$g.Clear([System.Drawing.Color]::Transparent)

function RoundRect([System.Drawing.Drawing2D.GraphicsPath]$p, [float]$x,[float]$y,[float]$w,[float]$h,[float]$r) {
  $p.AddArc($x,$y,$r*2,$r*2,180,90)
  $p.AddArc($x+$w-$r*2,$y,$r*2,$r*2,270,90)
  $p.AddArc($x+$w-$r*2,$y+$h-$r*2,$r*2,$r*2,0,90)
  $p.AddArc($x,$y+$h-$r*2,$r*2,$r*2,90,90)
  $p.CloseFigure()
}

# dark circle background
$bg = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255,32,32,32))
$g.FillEllipse($bg,2,2,60,60)

# battery body (rounded rect, white outline)
$body = New-Object System.Drawing.Drawing2D.GraphicsPath
RoundRect $body 14 22 32 20 4
$white = New-Object System.Drawing.Pen([System.Drawing.Color]::White,3)
$g.DrawPath($white,$body)

# positive nub
$nub = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
$g.FillRectangle($nub,47,28,5,8)

# 70% green fill
$fill = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255,76,201,76))
$g.FillRectangle($fill,18,26,19,12)

$g.Dispose()
$ico = [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
$fs = [System.IO.File]::Create("$PSScriptRoot\app.ico")
$ico.Save($fs)
$fs.Close()
Write-Host "app.ico generated"
