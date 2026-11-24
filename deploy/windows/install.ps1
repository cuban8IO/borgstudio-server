#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs or updates BorgStudio Server as a Windows service.

.DESCRIPTION
    Run from the unpacked archive in an elevated PowerShell:  .\install.ps1
    The program goes to "C:\Program Files\BorgStudio Server" and runs as the virtual service account
    "NT SERVICE\BorgStudioServer", which may only change the data folder. An update replaces the
    program files and keeps data\ and borgstudio-server.json.

.PARAMETER InstallDir
    Where to install; default "C:\Program Files\BorgStudio Server".
#>
param(
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'BorgStudio Server')
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'BorgStudioServer'
$Account = "NT SERVICE\$ServiceName"
$Config = 'borgstudio-server.json'
$Source = $PSScriptRoot
$Keep = @('data', $Config)

if (-not (Test-Path (Join-Path $Source 'BorgStudio.Server.exe'))) {
    throw 'Run this script from the unpacked BorgStudio Server archive.'
}
if ($Source.TrimEnd('\') -ieq $InstallDir.TrimEnd('\')) {
    throw "Run install.ps1 from the new archive, not from $InstallDir."
}

$versionFile = Join-Path $Source 'VERSION'
$version = if (Test-Path $versionFile) { (Get-Content $versionFile -Raw).Trim() } else { 'unknown' }
Write-Host "Installing BorgStudio Server $version to $InstallDir"

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service -and $service.Status -ne 'Stopped') {
    Write-Host 'Stopping the running service ...'
    Stop-Service -Name $ServiceName
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}

# Replace the program, keep the data and the configuration.
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Get-ChildItem -LiteralPath $InstallDir -Force | Where-Object { $Keep -notcontains $_.Name } | Remove-Item -Recurse -Force
Get-ChildItem -LiteralPath $Source -Force | Where-Object { $Keep -notcontains $_.Name } |
    Copy-Item -Destination $InstallDir -Recurse -Force

$data = Join-Path $InstallDir 'data'
New-Item -ItemType Directory -Force -Path $data | Out-Null
$configPath = Join-Path $InstallDir $Config
if (-not (Test-Path $configPath)) {
    Copy-Item (Join-Path $Source 'borgstudio-server.example.json') $configPath
}

$exe = Join-Path $InstallDir 'BorgStudio.Server.exe'
if (-not $service) {
    New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName 'BorgStudio Server' `
        -Description 'Central overview and backup targets for BorgStudio.' -StartupType Automatic | Out-Null
}
else {
    & sc.exe config $ServiceName binPath= "`"$exe`"" | Out-Null
}
& sc.exe config $ServiceName obj= $Account | Out-Null
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/60000 | Out-Null

# The service account reads the program, changes only data\, and reads the configuration
# (which may hold a certificate password, so other users cannot read it).
& icacls.exe $InstallDir /grant "${Account}:(OI)(CI)RX" | Out-Null
& icacls.exe $data /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' "${Account}:(OI)(CI)M" | Out-Null
& icacls.exe $configPath /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' "${Account}:R" | Out-Null

# Inbound HTTPS from private and domain networks.
$port = 8443
$httpsSection = Select-String -LiteralPath $configPath -Pattern '"Https"' -Context 0, 3 | Select-Object -First 1
if ($httpsSection) {
    $match = [regex]::Match(($httpsSection.Context.PostContext -join "`n"), '"Port"\s*:\s*(\d+)')
    if ($match.Success) { $port = [int]$match.Groups[1].Value }
}
$rule = Get-NetFirewallRule -Name $ServiceName -ErrorAction SilentlyContinue
if ($rule) {
    $rule | Set-NetFirewallRule -LocalPort $port
}
else {
    New-NetFirewallRule -Name $ServiceName -DisplayName 'BorgStudio Server (HTTPS)' -Direction Inbound `
        -Protocol TCP -LocalPort $port -Action Allow -Profile Domain, Private | Out-Null
}

# Returns once the service reports "running"; by then a fresh installation has written the setup code
# for the first administrator.
Start-Service -Name $ServiceName
$setupCode = Join-Path $data 'setup-code.txt'

Write-Host ''
Write-Host "BorgStudio Server $version is running: https://$($env:COMPUTERNAME):$port/"
if (Test-Path $setupCode) {
    Write-Host "Setup code for the first administrator: $((Get-Content $setupCode -Raw).Trim())"
}
Write-Host ''
Write-Host "Configuration:  $configPath (then: Restart-Service $ServiceName)"
Write-Host "Log:            Event Viewer > Windows Logs > Application (warnings and errors)"
Write-Host "Locked out:     & '$exe' reset-admin   (in an elevated PowerShell)"
Write-Host "Uninstall:      & '$(Join-Path $InstallDir 'uninstall.ps1')'"
