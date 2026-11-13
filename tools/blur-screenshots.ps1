# Pixelates (heavily blurs) the README screenshots so personal content is unreadable.
Add-Type -AssemblyName System.Drawing

$files = @("docs\screenshot-library.png", "docs\screenshot-viewer.png")
$factor = 28

foreach ($f in $files) {
    $path = (Resolve-Path $f).Path
    $src = [System.Drawing.Image]::FromFile($path)

    $sw = [Math]::Max(1, [int]($src.Width / $factor))
    $sh = [Math]::Max(1, [int]($src.Height / $factor))

    # Down
    $small = New-Object System.Drawing.Bitmap $sw, $sh
    $g1 = [System.Drawing.Graphics]::FromImage($small)
    $g1.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBilinear
    $g1.DrawImage($src, 0, 0, $sw, $sh)
    $g1.Dispose()

    # Up (smooth blur)
    $dst = New-Object System.Drawing.Bitmap $src.Width, $src.Height
    $g2 = [System.Drawing.Graphics]::FromImage($dst)
    $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBilinear
    $g2.DrawImage($small, 0, 0, $src.Width, $src.Height)
    $g2.Dispose()

    $src.Dispose()
    $dst.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $dst.Dispose()
    Write-Host "blurred $f ($($src.Width)x$($src.Height), factor $factor)"
}
