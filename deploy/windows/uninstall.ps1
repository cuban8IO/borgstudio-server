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

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    & sc.exe delete $ServiceName | Out-Null
}

Get-NetFirewallRule -Name $ServiceName -ErrorAction SilentlyContinue | Remove-NetFirewallRule

if (Test-Path $InstallDir) {
    # This script may live in the folder it removes; PowerShell has already read it.
    if ($Purge) {
        Remove-Item -LiteralPath $InstallDir -Recurse -Force
    }
    else {
        Get-ChildItem -LiteralPath $InstallDir -Force | Where-Object { $Keep -notcontains $_.Name } |
            Remove-Item -Recurse -Force
    }
}

if ($Purge) {
    Write-Host 'BorgStudio Server was removed, including its data.'
}
else {
    Write-Host "BorgStudio Server was removed. Data and configuration remain in $InstallDir."
}
