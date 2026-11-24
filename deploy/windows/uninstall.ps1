#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Removes the BorgStudio Server service and program.

.PARAMETER Purge
    Also delete data\ and borgstudio-server.json (database, keys, certificates).
#>
param(
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'BorgStudio Server'),
    [switch] $Purge
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'BorgStudioServer'
$Keep = @('data', 'borgstudio-server.json')

# --- Console output (same look in all BorgStudio Server scripts) -------------------------------
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
function Invoke-Step([string] $Text, [scriptblock] $Action) {
    if ($Fancy) { Write-Host " $Busy $Text" -ForegroundColor Magenta -NoNewline }
    try {
        $null = & $Action
    }
    catch {
        if ($Fancy) { Write-Host "`r$(' ' * ($Text.Length + 4))`r" -NoNewline }
        Write-Host " $Cross " -ForegroundColor Red -NoNewline
        Write-Host $Text
        Write-Host "   $($_.Exception.Message)" -ForegroundColor DarkGray
        exit 1
    }
    if ($Fancy) { Write-Host "`r" -NoNewline }
    Write-Ok $Text
}
# ------------------------------------------------------------------------------------------------

if ($Purge) { Write-Banner 'Removing the program and all data' }
else { Write-Banner 'Removing the program (data and configuration stay)' }

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    Invoke-Step "Stopping and removing the service $ServiceName" {
        if ($service.Status -ne 'Stopped') {
            Stop-Service -Name $ServiceName
            $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        }
        & sc.exe delete $ServiceName | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "sc.exe delete failed (exit code $LASTEXITCODE)." }
    }
}

Invoke-Step 'Removing the firewall rule' {
    Get-NetFirewallRule -Name $ServiceName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
}

if (Test-Path $InstallDir) {
    # This script may live in the folder it removes; PowerShell has already read it.
    if ($Purge) {
        Invoke-Step "Deleting $InstallDir with all data" { Remove-Item -LiteralPath $InstallDir -Recurse -Force }
    }
    else {
        Invoke-Step 'Deleting the program files' {
            Get-ChildItem -LiteralPath $InstallDir -Force | Where-Object { $Keep -notcontains $_.Name } |
                Remove-Item -Recurse -Force
        }
    }
}

Write-Host ''
if ($Purge) {
    Write-Ok 'BorgStudio Server was removed, including its data.'
}
else {
    Write-Ok 'BorgStudio Server was removed.'
    Write-Info "Data and configuration remain in $InstallDir; install again to use them, or run with -Purge to delete them."
}
