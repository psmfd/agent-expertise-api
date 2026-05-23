#requires -Version 7
<#
.SYNOPSIS
  Daily-use service control for agent-expertise-api on Windows.

.DESCRIPTION
  Mirrors scripts/expertise-apictl (the Unix wrapper). Wraps Get-Service /
  Start-Service / Stop-Service / Get-WinEvent for the Windows Service.

.EXAMPLE
  .\expertise-apictl.ps1 status
  .\expertise-apictl.ps1 logs -Follow
  .\expertise-apictl.ps1 health
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateSet('start', 'stop', 'restart', 'status', 'logs', 'health')]
    [string]$Command,

    [int]$Lines = 200,
    [switch]$Follow,
    [string]$ServiceName = 'expertise-api',
    [string]$HealthUrl   = 'http://127.0.0.1:8080/health'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

switch ($Command) {
    'start'   { Start-Service $ServiceName }
    'stop'    { Stop-Service $ServiceName -Force }
    'restart' { Restart-Service $ServiceName -Force }
    'status'  { Get-Service $ServiceName | Format-List Name, Status, StartType, DisplayName }
    'logs' {
        if ($Follow) {
            # Get-WinEvent has no native -Wait; poll every second.
            $last = (Get-Date).AddMinutes(-5)
            while ($true) {
                Get-WinEvent -FilterHashtable @{ ProviderName = $ServiceName; StartTime = $last } `
                    -ErrorAction SilentlyContinue |
                    Sort-Object TimeCreated |
                    ForEach-Object {
                        '{0}  {1}  {2}' -f $_.TimeCreated, $_.LevelDisplayName, $_.Message
                        $last = $_.TimeCreated.AddMilliseconds(1)
                    }
                Start-Sleep -Seconds 1
            }
        } else {
            Get-WinEvent -ProviderName $ServiceName -MaxEvents $Lines -ErrorAction SilentlyContinue |
                Format-List TimeCreated, LevelDisplayName, Message
        }
    }
    'health' {
        try {
            $r = Invoke-RestMethod -Uri $HealthUrl -TimeoutSec 5
            $r | Format-List
            Write-Host "[expertise-apictl] health: OK"
        } catch {
            Write-Error "[expertise-apictl] health check failed against $HealthUrl : $_"
            exit 1
        }
    }
}
