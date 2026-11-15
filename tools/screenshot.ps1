param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $false)][string]$Image = "",
    [Parameter(Mandatory = $true)][string]$OutPng,
    [int]$WaitSeconds = 7
)


Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public struct RECT { public int Left, Top, Right, Bottom; }
public class Win32 {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
}
"@

[Win32]::SetProcessDPIAware() | Out-Null

# Make sure no previous instance holds the single-instance mutex.
Get-Process HeicViewer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 1200

if ($Image -ne "") {
    $p = Start-Process -FilePath $ExePath -ArgumentList ('"' + $Image + '"') -PassThru
} else {
    $p = Start-Process -FilePath $ExePath -PassThru
}
Start-Sleep -Seconds $WaitSeconds

# Wait for the main window handle (startup may be slow on first run).
$h = [IntPtr]::Zero
for ($i = 0; $i -lt 6; $i++) {
    $p.Refresh()
    $h = $p.MainWindowHandle
    if ($h -ne [IntPtr]::Zero) { break }
    Start-Sleep -Seconds 2
}
if ($h -eq [IntPtr]::Zero) { Write-Error "No window handle found"; exit 1 }
Start-Sleep -Seconds 1

$r = New-Object RECT
[Win32]::GetWindowRect($h, [ref]$r) | Out-Null

# Capture with retries. Do NOT move/raise/nudge the window - interference with a
# live desktop leads to blank captures. If minimized, restore; if PrintWindow
# catches an unpainted surface (< 150 KB), try again.
for ($attempt = 1; $attempt -le 8; $attempt++) {
    if ([Win32]::IsIconic($h)) {
        [Win32]::ShowWindow($h, 9) | Out-Null    # SW_RESTORE
        Start-Sleep -Milliseconds 900
    }
    [Win32]::GetWindowRect($h, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left
    $hgt = $r.Bottom - $r.Top
    if ($w -lt 300 -or $hgt -lt 200) {
        Write-Host ("attempt {0}: window tiny ({1}x{2}) - waiting" -f $attempt, $w, $hgt)
        Start-Sleep -Seconds 2
        continue
    }

    $bmp = New-Object System.Drawing.Bitmap $w, $hgt
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [Win32]::PrintWindow($h, $hdc, 2) | Out-Null   # 2 = PW_RENDERFULLCONTENT
    $g.ReleaseHdc($hdc)
    $g.Dispose()
    $bmp.Save($OutPng, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $len = (Get-Item $OutPng).Length
    Write-Host ("capture attempt {0}: {1} bytes ({2}x{3})" -f $attempt, $len, $w, $hgt)
    if ($len -gt 150000) { break }
    Start-Sleep -Seconds 2
}

Write-Host "Saved $OutPng (${w}x${hgt})"
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Wait-Process -Id $p.Id -ErrorAction SilentlyContinue
