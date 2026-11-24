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

# --- Console output (same look in all BorgStudio Server scripts) -------------------------------
# Plain ASCII source (Windows PowerShell 5.1 reads scripts without BOM as ANSI); symbols come from code points.
$Fancy = -not [Console]::IsOutputRedirected
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { $null = $_ }
$Check = [string][char]0x2714
$Cross = [string][char]0x2716
$Busy = [string][char]0x280F
$Dot = [string][char]0x2022

function Write-Banner([string] $Subtitle) {
    @(
        '  ____                  ____  _             _ _',
        ' | __ )  ___  _ __ __ _/ ___|| |_ _   _  __| (_) ___',
        " |  _ \ / _ \| '__/ _`` \___ \| __| | | |/ _`` | |/ _ \",
        ' | |_) | (_) | | | (_| |___) | |_| |_| | (_| | | (_) |',
        ' |____/ \___/|_|  \__, |____/ \__|\__,_|\__,_|_|\___/'
    ) | ForEach-Object { Write-Host $_ -ForegroundColor Green }
    Write-Host '                  |___/ ' -ForegroundColor Green -NoNewline
    Write-Host 'S E R V E R' -ForegroundColor Magenta
    Write-Host ''
    Write-Host "  $Subtitle" -ForegroundColor DarkGray
    Write-Host ''
}
function Write-Ok([string] $Text) { Write-Host " $Check " -ForegroundColor Green -NoNewline; Write-Host $Text }
function Write-Info([string] $Text) { Write-Host " $Dot " -ForegroundColor DarkGray -NoNewline; Write-Host $Text }
function Stop-WithError([string] $Text, [string] $Detail) {
    Write-Host " $Cross " -ForegroundColor Red -NoNewline
    Write-Host $Text
    if ($Detail) { Write-Host "   $Detail" -ForegroundColor DarkGray }
    exit 1
}
# Invoke-Step <text> { ... }: runs the block quietly; a check mark when it worked, the error otherwise.
function Invoke-Step([string] $Text, [scriptblock] $Action) {
    if ($Fancy) { Write-Host " $Busy $Text" -ForegroundColor Magenta -NoNewline }
    try {
        $null = & $Action
    }
    catch {
        if ($Fancy) { Write-Host "`r$(' ' * ($Text.Length + 4))`r" -NoNewline }
        Stop-WithError $Text $_.Exception.Message
    }
    if ($Fancy) { Write-Host "`r" -NoNewline }
    Write-Ok $Text
}
# Native programs report failure by exit code, not by exception.
function Assert-ExitCode([string] $What) {
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit code $LASTEXITCODE)." }
}
function Write-Summary([string] $Title, [System.Collections.Specialized.OrderedDictionary] $Facts) {
    $width = $Title.Length
    foreach ($key in $Facts.Keys) { $width = [Math]::Max($width, 15 + "$($Facts[$key])".Length) }
    $line = [string][char]0x2500 * ($width + 4)
    $side = [string][char]0x2502
    Write-Host ''
    Write-Host " $([char]0x256D)$line$([char]0x256E)" -ForegroundColor Green
    Write-Host " $side" -ForegroundColor Green -NoNewline
    Write-Host "  $($Title.PadRight($width))  " -NoNewline
    Write-Host $side -ForegroundColor Green
    Write-Host " $side$(' ' * ($width + 4))$side" -ForegroundColor Green
    foreach ($key in $Facts.Keys) {
        $value = "$($Facts[$key])"
        $color = if ($key -eq 'Setup code') { 'Magenta' } else { 'Gray' }
        Write-Host " $side" -ForegroundColor Green -NoNewline
        Write-Host "  $($key.PadRight(15))" -ForegroundColor DarkGray -NoNewline
        Write-Host $value.PadRight($width - 15) -ForegroundColor $color -NoNewline
        Write-Host "  $side" -ForegroundColor Green
    }
    Write-Host " $([char]0x2570)$line$([char]0x256F)" -ForegroundColor Green
    Write-Host ''
}
# ------------------------------------------------------------------------------------------------

$versionFile = Join-Path $Source 'VERSION'
$version = if (Test-Path $versionFile) { (Get-Content $versionFile -Raw).Trim() } else { 'unknown' }
Write-Banner "Installing version $version"

if (-not (Test-Path (Join-Path $Source 'BorgStudio.Server.exe'))) {
    Stop-WithError 'Run this script from the unpacked BorgStudio Server archive.'
}
if ($Source.TrimEnd('\') -ieq $InstallDir.TrimEnd('\')) {
    Stop-WithError "Run install.ps1 from the new archive, not from $InstallDir."
}

$previousFile = Join-Path $InstallDir 'VERSION'
$previous = if (Test-Path $previousFile) { (Get-Content $previousFile -Raw).Trim() } else { $null }
if ($previous) { Write-Info "Installed: $previous - it is replaced, data and configuration stay" }

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service -and $service.Status -ne 'Stopped') {
    Invoke-Step 'Stopping the running service' {
        Stop-Service -Name $ServiceName
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
}

# Replace the program, keep the data and the configuration.
Invoke-Step "Copying the program to $InstallDir" {
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    Get-ChildItem -LiteralPath $InstallDir -Force | Where-Object { $Keep -notcontains $_.Name } | Remove-Item -Recurse -Force
    Get-ChildItem -LiteralPath $Source -Force | Where-Object { $Keep -notcontains $_.Name } |
        Copy-Item -Destination $InstallDir -Recurse -Force
}

$data = Join-Path $InstallDir 'data'
$configPath = Join-Path $InstallDir $Config
$exe = Join-Path $InstallDir 'BorgStudio.Server.exe'

Invoke-Step "Setting up the Windows service $ServiceName" {
    if (-not $service) {
        New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName 'BorgStudio Server' `
            -Description 'Central overview and backup targets for BorgStudio.' -StartupType Automatic | Out-Null
    }
    else {
        & sc.exe config $ServiceName binPath= "`"$exe`"" | Out-Null
        Assert-ExitCode 'sc.exe config'
    }
    & sc.exe config $ServiceName obj= $Account | Out-Null
    Assert-ExitCode 'sc.exe config'
    & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/60000 | Out-Null
    Assert-ExitCode 'sc.exe failure'
}

# The service account reads the program, changes only data\, and reads the configuration
# (which may hold a certificate password, so other users cannot read it).
Invoke-Step 'Setting up data folder and configuration' {
    New-Item -ItemType Directory -Force -Path $data | Out-Null
    if (-not (Test-Path $configPath)) {
        Copy-Item (Join-Path $Source 'borgstudio-server.example.json') $configPath
    }
    & icacls.exe $InstallDir /grant "${Account}:(OI)(CI)RX" | Out-Null
    Assert-ExitCode 'icacls'
    & icacls.exe $data /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' "${Account}:(OI)(CI)M" | Out-Null
    Assert-ExitCode 'icacls'
    & icacls.exe $configPath /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' "${Account}:R" | Out-Null
    Assert-ExitCode 'icacls'
}

# Inbound HTTPS from private and domain networks.
$port = 8443
$httpsSection = Select-String -LiteralPath $configPath -Pattern '"Https"' -Context 0, 3 | Select-Object -First 1
if ($httpsSection) {
    $match = [regex]::Match(($httpsSection.Context.PostContext -join "`n"), '"Port"\s*:\s*(\d+)')
    if ($match.Success) { $port = [int]$match.Groups[1].Value }
}
Invoke-Step "Opening port $port in the firewall (private and domain networks)" {
    $rule = Get-NetFirewallRule -Name $ServiceName -ErrorAction SilentlyContinue
    if ($rule) {
        $rule | Set-NetFirewallRule -LocalPort $port
    }
    else {
        New-NetFirewallRule -Name $ServiceName -DisplayName 'BorgStudio Server (HTTPS)' -Direction Inbound `
            -Protocol TCP -LocalPort $port -Action Allow -Profile Domain, Private | Out-Null
    }
}

# Returns once the service reports "running"; by then a fresh installation has written the setup code
# for the first administrator.
Invoke-Step 'Starting BorgStudio Server' { Start-Service -Name $ServiceName }

$facts = [ordered]@{ 'Address' = "https://$($env:COMPUTERNAME):$port/" }
$setupCode = Join-Path $data 'setup-code.txt'
if (Test-Path $setupCode) { $facts['Setup code'] = (Get-Content $setupCode -Raw).Trim() }
$facts['Configuration'] = $configPath
$facts['Log'] = 'Event Viewer > Windows Logs > Application'
$facts['Locked out'] = "& '$exe' reset-admin"
$facts['Uninstall'] = "& '$(Join-Path $InstallDir 'uninstall.ps1')'"

if ($previous -and $previous -ne $version) {
    Write-Summary "BorgStudio Server updated: $previous -> $version" $facts
}
else {
    Write-Summary "BorgStudio Server $version is running" $facts
}

if (Test-Path $setupCode) {
    Write-Info 'Open the address in a browser and enter the setup code to create the first administrator.'
    Write-Info 'The browser warns about the self-signed certificate; upload your own under Settings.'
}
