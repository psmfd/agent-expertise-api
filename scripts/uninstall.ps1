#requires -Version 7
<#
.SYNOPSIS
  Uninstall agent-expertise-api Windows Service.

.DESCRIPTION
  Dry-run by default — pass -Confirm:$false plus -WhatIf:$false to apply.
  Use -Purge to also remove user data (models, secrets, logs). Postgres data
  is NEVER touched.

.EXAMPLE
  .\uninstall.ps1               # dry-run
  .\uninstall.ps1 -WhatIf:$false  # apply (PowerShell SupportsShouldProcess)
  .\uninstall.ps1 -WhatIf:$false -Purge   # apply + remove data
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [string]$InstallPrefix = "$env:ProgramFiles\ExpertiseApi",
    [string]$DataPrefix    = "$env:ProgramData\ExpertiseApi",
    [string]$ServiceName   = 'expertise-api',
    [switch]$Purge
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Log { param([string]$Msg) Write-Host "[uninstall.ps1] $Msg" }

# Admin check
$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [System.Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error '[uninstall.ps1] must be run from an elevated PowerShell prompt'
    exit 1
}

# Stop + remove service
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($PSCmdlet.ShouldProcess($ServiceName, 'Stop service')) {
        if ($svc.Status -eq 'Running') { Stop-Service $ServiceName -Force }
    }
    if ($PSCmdlet.ShouldProcess($ServiceName, 'sc.exe delete')) {
        & sc.exe delete $ServiceName | Out-Null
        Write-Log "service $ServiceName removed"
    }
} else {
    Write-Log "service $ServiceName not present — skipping"
}

# Remove install tree
$BinDir = Join-Path $InstallPrefix 'bin'
if (Test-Path $BinDir) {
    if ($PSCmdlet.ShouldProcess($BinDir, 'Remove install dir')) {
        Remove-Item -Recurse -Force $BinDir
    }
}

if (Test-Path $InstallPrefix) {
    $remaining = Get-ChildItem $InstallPrefix -ErrorAction SilentlyContinue
    if (-not $remaining) {
        if ($PSCmdlet.ShouldProcess($InstallPrefix, 'Remove empty install root')) {
            Remove-Item -Force $InstallPrefix
        }
    }
}

if ($Purge) {
    Write-Log 'purge: removing user data'
    if (Test-Path $DataPrefix) {
        if ($PSCmdlet.ShouldProcess($DataPrefix, 'Remove data dir (purge)')) {
            Remove-Item -Recurse -Force $DataPrefix
        }
    }
} else {
    Write-Log "preserved: $DataPrefix (use -Purge to remove)"
}

Write-Log 'uninstall complete (Postgres database NOT touched)'
