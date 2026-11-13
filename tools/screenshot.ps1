param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $false)][string]$Image = "",
    [Parameter(Mandatory = $true)][string]$OutPng
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
Start-Sleep -Seconds 4

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
$w = $r.Right - $r.Left
$hgt = $r.Bottom - $r.Top

# If the window got minimized (user or system), restore it first.
if ([Win32]::IsIconic($h)) {
    [Win32]::ShowWindow($h, 9) | Out-Null    # SW_RESTORE
    Start-Sleep -Milliseconds 800
}
[Win32]::GetWindowRect($h, [ref]$r) | Out-Null
$w = $r.Right - $r.Left
$hgt = $r.Bottom - $r.Top
if ($w -lt 300 -or $hgt -lt 200) {
    Write-Error "Window still tiny after restore (${w}x${hgt}) - aborting"
    exit 1
}

# Bring the window above everything else, then nudge the cursor over it
# so the floating chrome is shown for the capture.
[Win32]::SetWindowPos($h, [IntPtr]::Zero, 0, 0, 0, 0, 0x0003 -bor 0x0040) | Out-Null   # HWND_TOP | NOMOVE|NOSIZE|SHOWWINDOW
Start-Sleep -Milliseconds 400
[Win32]::SetCursorPos($r.Left + [int]($w * 0.5), $r.Top + [int]($hgt * 0.85)) | Out-Null
Start-Sleep -Milliseconds 400
[Win32]::SetCursorPos($r.Left + [int]($w * 0.5) + 3, $r.Top + [int]($hgt * 0.85)) | Out-Null
Start-Sleep -Milliseconds 400

$bmp = New-Object System.Drawing.Bitmap $w, $hgt
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
[Win32]::PrintWindow($h, $hdc, 2) | Out-Null   # 2 = PW_RENDERFULLCONTENT
$g.ReleaseHdc($hdc)
$g.Dispose()
$bmp.Save($OutPng, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

Write-Host "Saved $OutPng (${w}x${hgt})"
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Wait-Process -Id $p.Id -ErrorAction SilentlyContinue
