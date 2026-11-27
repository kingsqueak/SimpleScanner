# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 KC2RGW Chris
# Start the installed Windows build on a CI machine: no radio, no SDRplay API and no sound card, but
# it proves the app and its native libraries load on real Windows and that it stays up, and leaves
# a screenshot. Usage: smoke-windows.ps1 <folder> <screenshot>
param([string]$Dir, [string]$Shot)
$ErrorActionPreference = 'Stop'

# Settings and the database in a folder of their own, not the runner's profile.
$env:SIMPLESCANNER_DATA = "$env:RUNNER_TEMP\simplescanner-smoke"
New-Item -ItemType Directory -Force $env:SIMPLESCANNER_DATA | Out-Null

$app = Start-Process -FilePath "$Dir\SimpleScanner.exe" -WorkingDirectory $Dir -PassThru `
    -RedirectStandardOutput app.out -RedirectStandardError app.err
Start-Sleep -Seconds 25
if ($app.HasExited) {
    Get-Content app.out, app.err -ErrorAction SilentlyContinue
    throw "SimpleScanner exited with code $($app.ExitCode)"
}
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
[System.Drawing.Graphics]::FromImage($bmp).CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
$bmp.Save($Shot)
Get-Content app.out, app.err -Tail 20 -ErrorAction SilentlyContinue
Get-ChildItem $env:SIMPLESCANNER_DATA | Format-Table Name, Length
& taskkill.exe /F /T /PID $app.Id | Out-Null
Get-Process SimpleScanner -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Host 'smoke test passed'
