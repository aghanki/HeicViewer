# Builds labeled contact sheets from candidate images (id.jpg files).
param(
    [string]$Folder = "..\.tmp-picsum",
    [string]$OutPng = "..\.tmp-picsum\sheet",
    [int]$Columns = 6,
    [int]$CellW = 230,
    [int]$CellH = 160
)
Add-Type -AssemblyName System.Drawing

$files = Get-ChildItem $Folder -Filter *.jpg | Sort-Object { [int]($_.BaseName) }
$perSheet = $Columns * 4
$sheetIndex = 0

$font = New-Object System.Drawing.Font ('Consolas'), 18, ([System.Drawing.FontStyle]::Bold)
$white = [System.Drawing.Brushes]::White
$shadow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(180, 0, 0, 0))

for ($offset = 0; $offset -lt $files.Count; $offset += $perSheet) {
    $end = [Math]::Min($offset + $perSheet, $files.Count) - 1
    $chunk = @($files[$offset..$end])
    $rows = [Math]::Ceiling($chunk.Count / $Columns)

    $sheet = New-Object System.Drawing.Bitmap ($Columns * $CellW), ($rows * $CellH)
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    $g.Clear([System.Drawing.Color]::FromArgb(255, 24, 24, 28))
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBilinear

    for ($i = 0; $i -lt $chunk.Count; $i++) {
        $x = ($i % $Columns) * $CellW
        $y = [Math]::Floor($i / $Columns) * $CellH
        try {
            $img = [System.Drawing.Image]::FromFile($chunk[$i].FullName)
            $g.DrawImage($img, $x + 2, $y + 2, $CellW - 4, $CellH - 30)
            $img.Dispose()
        } catch { }
        $g.FillRectangle($shadow, $x + 4, $y + $CellH - 26, 60, 24)
        $g.DrawString($chunk[$i].BaseName, $font, $white, $x + 8, $y + $CellH - 25)
    }

    $out = "$OutPng-sheet$sheetIndex.png"
    $sheet.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    $sheet.Dispose()
    Write-Host "saved $out ($($chunk.Count) images)"
    $sheetIndex++
}
$font.Dispose()
