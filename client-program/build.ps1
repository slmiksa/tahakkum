# Builds dist\Tahakkum.exe using the C# compiler that ships with Windows.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fx = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null

# App icon: the brand symbol on a charcoal tile, at the standard sizes. Same shape as LogoView.
$iconPath = Join-Path $dist 'app.ico'
Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $k = $s / 200.0
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 88 * $k; $e = $s - 1
    $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($e - $d, 0, $d, $d, 270, 90)
    $path.AddArc($e - $d, $e - $d, $d, $d, 0, 90); $path.AddArc(0, $e - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $tile = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(14, 21, 23))
    $g.FillPath($tile, $path)
    $g.ScaleTransform($k, $k)
    $ring = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 20
    $ring.StartCap = 'Round'; $ring.EndCap = 'Round'
    $g.DrawArc($ring, 45, 45, 110, 110, 0, 270)
    $teal = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(31, 174, 180))
    $g.FillEllipse($teal, 127, 50, 20, 20)
    $points = @(@(79, 68), @(79, 124), @(92, 112), @(100, 132), @(115, 125), @(106, 105), @(122, 105)) | ForEach-Object { New-Object System.Drawing.PointF $_[0], $_[1] }
    $g.FillPolygon($teal, [System.Drawing.PointF[]]$points)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}
$out = New-Object System.IO.BinaryWriter ([System.IO.File]::Create($iconPath))
$out.Write([uint16]0); $out.Write([uint16]1); $out.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; if ($sz -ge 256) { $sz = 0 }
    $out.Write([byte]$sz); $out.Write([byte]$sz); $out.Write([byte]0); $out.Write([byte]0)
    $out.Write([uint16]1); $out.Write([uint16]32); $out.Write([uint32]$images[$i].Length); $out.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $out.Write($img) }
$out.Close()

$sources = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { '"' + $_.FullName + '"' }
$refs = 'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Security.dll', 'System.Xml.dll', 'System.Runtime.Serialization.dll' | ForEach-Object { '/r:"' + (Join-Path $fx $_) + '"' }
$rsp = Join-Path $dist 'build.rsp'
$lines = @(
    '/nologo', '/noconfig', '/nostdlib-', '/target:winexe', '/platform:anycpu', '/optimize+', '/unsafe', '/codepage:65001',
    '/nowarn:1701,1702',
    ('/out:"' + (Join-Path $dist 'Tahakkum.exe') + '"'),
    ('/win32icon:"' + $iconPath + '"'),
    ('/resource:"' + (Join-Path $root 'assets\wordmark.png') + '",wordmark.png'),
    ('/win32manifest:"' + (Join-Path $root 'app.manifest') + '"')
) + $refs + $sources
Set-Content -Path $rsp -Value $lines -Encoding UTF8

& (Join-Path $fx 'csc.exe') "@$rsp"
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Remove-Item $rsp, $iconPath
Get-Item (Join-Path $dist 'Tahakkum.exe') | Select-Object Name, Length
