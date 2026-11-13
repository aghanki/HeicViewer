# Generates app.ico (rounded gradient square with "HEIC" text) without any external tooling.
param([string]$OutPath = (Join-Path $PSScriptRoot "..\src\HeicViewer\app.ico"))

Add-Type -AssemblyName System.Drawing

$sizes = 16, 24, 32, 48, 64, 128, 256
$entries = @()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

    # Rounded-rectangle path
    $r = [Math]::Max(2, [int]($s * 0.22))
    $d = $r * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($s - $d, 0, $d, $d, 270, 90)
    $path.AddArc($s - $d, $s - $d, $d, $d, 0, 90)
    $path.AddArc(0, $s - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $rect = New-Object System.Drawing.Rectangle 0, 0, $s, $s
    $c1 = [System.Drawing.Color]::FromArgb(255, 96, 125, 245)
    $c2 = [System.Drawing.Color]::FromArgb(255, 170, 80, 235)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush -ArgumentList $rect, $c1, $c2, 45.0
    $g.FillPath($brush, $path)

    $fontSize = [Math]::Max(5, [double]$s * 0.30)
    $font = New-Object System.Drawing.Font ('Segoe UI'), $fontSize, `
        ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    $textRect = New-Object System.Drawing.RectangleF 0, ($s * -0.03), $s, $s
    $g.DrawString('HEIC', $font, [System.Drawing.Brushes]::White, $textRect, $sf)

    $g.Dispose()
    $font.Dispose(); $brush.Dispose(); $path.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $entries += , @($s, $ms.ToArray())
}

# Assemble ICO container with PNG-compressed entries
$msOut = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $msOut
$bw.Write([UInt16]0)                 # reserved
$bw.Write([UInt16]1)                 # type: icon
$bw.Write([UInt16]$entries.Count)    # count

$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $s = [int]$e[0]
    $data = [byte[]]$e[1]
    $bw.Write([byte]($s -band 0xFF))
    $bw.Write([byte]($s -band 0xFF))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($e in $entries) { $bw.Write([byte[]]$e[1]) }
$bw.Flush()

[System.IO.File]::WriteAllBytes($OutPath, $msOut.ToArray())
Write-Host "Icon written: $OutPath ($($msOut.Length) bytes)"
