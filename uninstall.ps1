$ErrorActionPreference = "Stop"
$name = "spicetify-remote-api"
$root = Join-Path $env:LOCALAPPDATA "SpicetifyRemoteApi"

Get-Process $name -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "SpicetifyRemoteApi" -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300
if (Test-Path $root) { Remove-Item $root -Recurse -Force }

$ErrorActionPreference = "Continue"
if (Get-Command spicetify -ErrorAction SilentlyContinue) {
    if ((spicetify config extensions) -contains "remote-api.js") { spicetify config extensions remote-api.js- }
    $extension = Join-Path (spicetify path userdata) "Extensions\remote-api.js"
    if (Test-Path $extension) { Remove-Item $extension -Force }
    spicetify apply
}

Write-Host "Spicetify Remote API removed"
