$ErrorActionPreference = "Stop"
$name = "spicetify-remote-api"
$root = Join-Path $env:LOCALAPPDATA "SpicetifyRemoteApi"
$exe = Join-Path $root "$name.exe"
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc)) { throw ".NET Framework 4 compiler not found" }
if (-not (Get-Command spicetify -ErrorAction SilentlyContinue)) { throw "Spicetify is not installed, get it from https://spicetify.app" }

$source = $PSScriptRoot
if (-not $source -or -not (Test-Path (Join-Path $source "bridge\Bridge.cs"))) {
    Write-Host "Downloading Spicetify Remote API"
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $download = Join-Path $env:TEMP "spicetify-remote-api"
    if (Test-Path $download) { Remove-Item $download -Recurse -Force }
    New-Item -ItemType Directory -Force $download | Out-Null
    $zip = Join-Path $download "source.zip"
    Invoke-WebRequest "https://github.com/Zexylz/spicetify-remote-api/archive/refs/heads/main.zip" -OutFile $zip -UseBasicParsing
    Expand-Archive $zip $download -Force
    $source = Join-Path $download "spicetify-remote-api-main"
}

Write-Host "Building the bridge"
Get-Process $name -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300
New-Item -ItemType Directory -Force $root | Out-Null
& $csc -nologo -optimize -target:winexe -r:System.Web.Extensions.dll "-out:$exe" (Join-Path $source "bridge\Bridge.cs")
if ($LASTEXITCODE -ne 0) { throw "Bridge build failed" }
Copy-Item (Join-Path $source "remote\index.html") (Join-Path $root "remote.html") -Force

Write-Host "Starting the bridge and adding it to startup"
Set-ItemProperty $runKey -Name "SpicetifyRemoteApi" -Value "`"$exe`""
Start-Process $exe
Start-Sleep -Seconds 1
if (-not (Get-Process $name -ErrorAction SilentlyContinue)) { Write-Warning "The bridge exited, port 7979 is probably used by another program" }

Write-Host "Installing the Spicetify extension"
$ErrorActionPreference = "Continue"
$extensions = Join-Path (spicetify path userdata) "Extensions"
New-Item -ItemType Directory -Force $extensions | Out-Null
Copy-Item (Join-Path $source "extension\remote-api.js") $extensions -Force
if ((spicetify config extensions) -notcontains "remote-api.js") { spicetify config extensions remote-api.js }
spicetify apply
if ($LASTEXITCODE -ne 0) { throw "spicetify apply failed, try spicetify backup apply" }

Write-Host "Done. Remote: http://127.0.0.1:7979/remote"
