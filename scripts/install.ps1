#requires -Version 7
<#
.SYNOPSIS
  Install agent-expertise-api as a Windows Service.

.DESCRIPTION
  Archetype A2 installer for Windows. Publishes the API per RID, copies it to
  Program Files, creates a Virtual Account-backed Windows Service via sc.exe,
  and configures auto-restart on failure. See docs section "Archetype A2".

.PARAMETER InstallPrefix
  Install root. Default: C:\Program Files\ExpertiseApi

.PARAMETER DataPrefix
  Data root for models, logs, secrets. Default: C:\ProgramData\ExpertiseApi

.PARAMETER ServiceName
  Windows Service name. Default: expertise-api

.PARAMETER PublishMode
  fdd | scd. Default: fdd. SCD adds ~80 MB but skips the .NET runtime check.

.PARAMETER Bind
  ASPNETCORE_URLS value. Default: http://127.0.0.1:8080

.PARAMETER SkipPreflight
  Skip pre-flight checks (.NET runtime, port, postgres, disk).

.EXAMPLE
  .\install.ps1
  .\install.ps1 -PublishMode scd -Bind 'http://127.0.0.1:9090'
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$InstallPrefix = "$env:ProgramFiles\ExpertiseApi",
    [string]$DataPrefix    = "$env:ProgramData\ExpertiseApi",
    [string]$ServiceName   = 'expertise-api',
    [ValidateSet('fdd', 'scd')]
    [string]$PublishMode   = 'fdd',
    [string]$Bind          = 'http://127.0.0.1:8080',
    [switch]$SkipPreflight
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$BinDir   = Join-Path $InstallPrefix 'bin'
$ModelDir = Join-Path $DataPrefix    'models'
$ConfigDir = Join-Path $DataPrefix   'config'
$LogDir   = Join-Path $DataPrefix    'logs'
$SecretsFile = Join-Path $ConfigDir  'secrets.env'

function Write-Log  { param([string]$Msg) Write-Host "[install.ps1] $Msg" }
function Write-Err  { param([string]$Msg) Write-Error "[install.ps1] $Msg"; exit 1 }
function Write-Warn { param([string]$Msg) Write-Warning "[install.ps1] $Msg" }

# ---- Admin check (sc.exe + Program Files writes need elevation) -----------
$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [System.Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Err 'must be run from an elevated PowerShell prompt (Run as Administrator)'
}

# ---- Pre-flight ----------------------------------------------------------
function Invoke-Preflight {
    Write-Log "pre-flight: PublishMode=$PublishMode Bind=$Bind"

    if ($PublishMode -eq 'fdd') {
        $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
        if (-not $dotnet) { Write-Err 'dotnet CLI not found in PATH' }
        $runtimes = & dotnet --list-runtimes 2>$null
        if (-not ($runtimes | Where-Object { $_ -match '^Microsoft\.AspNetCore\.App 10\.' })) {
            Write-Err 'ASP.NET Core 10 runtime not installed (https://dot.net)'
        }
        Write-Log 'dotnet runtime: OK'
    }

    $portStr = ($Bind -split ':')[-1] -replace '/', ''
    $port = [int]$portStr
    $listening = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
    if ($listening) { Write-Err "port $port already in use" }
    Write-Log "port ${port}: free"

    $pg = Test-NetConnection -ComputerName 127.0.0.1 -Port 5432 -InformationLevel Quiet -WarningAction SilentlyContinue
    if ($pg) { Write-Log 'postgres 127.0.0.1:5432: reachable' }
    else { Write-Warn 'postgres 127.0.0.1:5432 NOT reachable — service will start but health checks will fail' }

    $drive = Split-Path -Qualifier $InstallPrefix
    $disk = Get-CimInstance -ClassName Win32_LogicalDisk -Filter "DeviceID = '$drive'"
    $availMib = [math]::Round($disk.FreeSpace / 1MB, 0)
    if ($availMib -lt 200) { Write-Err "insufficient disk space on ${drive}: $availMib MiB" }
    Write-Log "disk space: $availMib MiB"
}

# ---- Publish -------------------------------------------------------------
function Invoke-Publish {
    Write-Log "publishing to $BinDir (rid=win-x64, mode=$PublishMode)"
    New-Item -ItemType Directory -Force -Path $BinDir | Out-Null
    $self = if ($PublishMode -eq 'scd') { 'true' } else { 'false' }
    Push-Location $RepoRoot
    try {
        & dotnet publish src/ExpertiseApi/ExpertiseApi.csproj `
            --configuration Release `
            --runtime win-x64 `
            --self-contained $self `
            -p:UseAppHost=true `
            --output $BinDir
        if ($LASTEXITCODE -ne 0) { Write-Err 'dotnet publish failed' }
    } finally { Pop-Location }
    Write-Log 'publish: complete'
}

# ---- Models --------------------------------------------------------------
function Confirm-Models {
    if ((Test-Path (Join-Path $ModelDir 'model.onnx')) -and (Test-Path (Join-Path $ModelDir 'vocab.txt'))) {
        Write-Log "ONNX models present at $ModelDir"; return
    }
    Write-Log "downloading ONNX models to $ModelDir"
    New-Item -ItemType Directory -Force -Path $ModelDir | Out-Null
    # download-models.sh requires bash (Git Bash, WSL, or skip and instruct user)
    $bash = Get-Command bash -ErrorAction SilentlyContinue
    if ($bash) {
        $env:DEST_DIR = $ModelDir
        & bash (Join-Path $RepoRoot 'scripts/download-models.sh')
    } else {
        Write-Warn "bash not found — download model files manually to $ModelDir"
        Write-Warn 'See scripts/download-models.sh for the source URLs'
    }
}

# ---- Config / secrets stub ----------------------------------------------
function Confirm-ConfigStubs {
    foreach ($d in $ConfigDir, $LogDir) {
        New-Item -ItemType Directory -Force -Path $d | Out-Null
    }

    if (-not (Test-Path $SecretsFile)) {
        Write-Log "creating secrets stub at $SecretsFile"
        $stub = @'
# secrets.env — read by the service at start. Do NOT commit. Do NOT log.
#
# Set the connection string after install. Example:
#   ConnectionStrings__DefaultConnection=Host=127.0.0.1;Port=5432;Database=expertise;Username=expertise;Password=CHANGE_ME
'@
        Set-Content -Path $SecretsFile -Value $stub -Encoding UTF8
        # Restrict ACL: only the service identity + Administrators
        icacls $SecretsFile /inheritance:r | Out-Null
        icacls $SecretsFile /grant:r 'Administrators:R' "NT SERVICE\$ServiceName`:R" "SYSTEM:R" | Out-Null
    } else {
        Write-Log "secrets file present at $SecretsFile (preserved)"
    }
}

# ---- Service create/update ----------------------------------------------
function Install-WindowsService {
    $exe = Join-Path $BinDir 'ExpertiseApi.exe'
    if (-not (Test-Path $exe)) { Write-Err "expected $exe not found after publish" }

    $binPath = "`"$exe`""
    $account = "NT SERVICE\$ServiceName"

    $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Log "service exists — stopping for update"
        if ($existing.Status -eq 'Running') { Stop-Service $ServiceName -Force }
        & sc.exe config $ServiceName binPath= $binPath start= auto obj= $account | Out-Null
    } else {
        Write-Log "creating service $ServiceName (Virtual Account: $account)"
        & sc.exe create $ServiceName binPath= $binPath start= auto obj= $account `
            DisplayName= 'Agent Expertise API' depend= '' | Out-Null
    }

    & sc.exe description $ServiceName 'Self-hosted REST API for AI-agent expertise (embedding + retrieval)' | Out-Null

    # Failure recovery: 5s, 5s, 30s; reset after 1 day
    & sc.exe failure $ServiceName reset= 86400 actions= 'restart/5000/restart/5000/restart/30000' | Out-Null
    & sc.exe failureflag $ServiceName 1 | Out-Null

    # Service-SID hardening
    & sc.exe sidtype $ServiceName unrestricted | Out-Null
    & sc.exe privs $ServiceName SeChangeNotifyPrivilege | Out-Null

    # Environment via REG_MULTI_SZ on the service key
    $envKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
    $envValues = @(
        "ASPNETCORE_URLS=$Bind"
        'ASPNETCORE_ENVIRONMENT=Production'
        'DOTNET_NOLOGO=true'
        'DOTNET_PRINT_TELEMETRY_MESSAGE=false'
        "Onnx__ModelPath=$(Join-Path $ModelDir 'model.onnx')"
        "Onnx__VocabPath=$(Join-Path $ModelDir 'vocab.txt')"
    )
    New-ItemProperty -Path $envKey -Name 'Environment' `
        -PropertyType MultiString -Value $envValues -Force | Out-Null

    # Grant Virtual Account write to ProgramData
    icacls $DataPrefix /grant:r "${account}:(OI)(CI)M" | Out-Null

    Start-Service $ServiceName
    Write-Log "service started"
}

# ---- Main ----------------------------------------------------------------
if (-not $SkipPreflight) { Invoke-Preflight }
Invoke-Publish
Confirm-Models
Confirm-ConfigStubs
Install-WindowsService

Write-Log 'install complete'
Write-Log "  binary:  $BinDir"
Write-Log "  models:  $ModelDir"
Write-Log "  config:  $ConfigDir"
Write-Log "  logs:    $LogDir (Serilog file sink + EventLog)"
Write-Log "  bind:    $Bind"
Write-Log ''
Write-Log "Edit $SecretsFile to set the database connection string,"
Write-Log "then check the service with: Get-Service $ServiceName"
