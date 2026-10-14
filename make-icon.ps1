# Generates HydraBond.ico (+ logo.png): three streams (one per adapter) merging into a hub, one stream out.
Add-Type -AssemblyName System.Drawing
Set-Location $PSScriptRoot

function Render([int]$S) {
  $bmp = New-Object Drawing.Bitmap $S, $S, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([Drawing.Color]::Transparent)
  $u = $S / 40.0   # design grid is 40x40

  # rounded-square tile
  $r = 9 * $u; $d = 2 * $r; $m = 0.5 * $u; $w = $S - 2 * $m
  $path = New-Object Drawing.Drawing2D.GraphicsPath
  $path.AddArc($m, $m, $d, $d, 180, 90); $path.AddArc($m + $w - $d, $m, $d, $d, 270, 90)
  $path.AddArc($m + $w - $d, $m + $w - $d, $d, $d, 0, 90); $path.AddArc($m, $m + $w - $d, $d, $d, 90, 90); $path.CloseFigure()
  $bg = New-Object Drawing.Drawing2D.LinearGradientBrush ([Drawing.PointF]::new(0, 0)), ([Drawing.PointF]::new($S, $S)), ([Drawing.Color]::FromArgb(39, 39, 48)), ([Drawing.Color]::FromArgb(9, 9, 11))
  $g.FillPath($bg, $path)
  $g.DrawPath((New-Object Drawing.Pen ([Drawing.Color]::FromArgb(70, 70, 80)), ([Math]::Max(1, 0.6 * $u))), $path)

  $pw = [Math]::Max(1.6, 2.6 * $u)
  function P($x, $y) { [Drawing.PointF]::new($x * $u, $y * $u) }
  $cols = @([Drawing.Color]::FromArgb(59, 130, 246), [Drawing.Color]::FromArgb(168, 85, 247), [Drawing.Color]::FromArgb(16, 185, 129))
  $ys = @(10, 20, 30)
  for ($i = 0; $i -lt 3; $i++) {
    $pen = New-Object Drawing.Pen $cols[$i], $pw; $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $y = $ys[$i]
    if ($y -eq 20) { $g.DrawLine($pen, (P 6 20), (P 16 20)) }
    else { $g.DrawBezier($pen, (P 6 $y), (P 13 $y), (P 12 20), (P 16 20)) }
    $dot = New-Object Drawing.SolidBrush $cols[$i]; $dr = $pw * 0.95
    $g.FillEllipse($dot, 6 * $u - $dr, $y * $u - $dr, 2 * $dr, 2 * $dr)
  }

  # hub ring
  $hub = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(250, 250, 250)), ([Math]::Max(1.6, 2.2 * $u))
  $g.DrawEllipse($hub, (21 * $u - 5.2 * $u), (20 * $u - 5.2 * $u), 10.4 * $u, 10.4 * $u)
  $g.FillEllipse((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(250, 250, 250))), (21 * $u - 2.2 * $u), (20 * $u - 2.2 * $u), 4.4 * $u, 4.4 * $u)

  # single pooled stream out
  $out = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(250, 250, 250)), $pw; $out.StartCap = 'Round'; $out.EndCap = 'Round'
  $g.DrawLine($out, (P 27.2 20), (P 34 20))
  $g.Dispose()
  return $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) { $b = Render $s; $ms = New-Object IO.MemoryStream; $b.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $pngs += , $ms.ToArray(); if ($s -eq 256) { $b.Save("$PSScriptRoot\logo.png", [Drawing.Imaging.ImageFormat]::Png) } }

$fs = [IO.File]::Create("$PSScriptRoot\HydraBond.ico"); $bw = New-Object IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]; $wh = if ($s -ge 256) { 0 } else { $s }
  $bw.Write([byte]$wh); $bw.Write([byte]$wh); $bw.Write([byte]0); $bw.Write([byte]0)
  $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
  $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close(); $fs.Close()
Write-Host "Wrote HydraBond.ico and logo.png"
