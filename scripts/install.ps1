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

.PARAMETER MigrateTimeout
  Wall-time limit in seconds for the migrate step (default: 300). 0 disables
  the bound. On timeout the install exits non-zero; the service is NOT started
  and the prior service state is untouched. Passed through to migrate.ps1.

.EXAMPLE
  .\install.ps1
  .\install.ps1 -PublishMode scd -Bind 'http://127.0.0.1:9090'
  .\install.ps1 -MigrateTimeout 0   # unbounded migrate
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$InstallPrefix = "$env:ProgramFiles\ExpertiseApi",
    [string]$DataPrefix    = "$env:ProgramData\ExpertiseApi",
    [string]$ServiceName   = 'expertise-api',
    [ValidateSet('fdd', 'scd')]
    [string]$PublishMode   = 'fdd',
    [string]$Bind          = 'http://127.0.0.1:8080',
    [switch]$SkipPreflight,
    [int]$MigrateTimeout   = 300
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
#   ConnectionStrings__DefaultConnection="Host=127.0.0.1;Port=5432;Database=expertise;Username=expertise;Password=CHANGE_ME"
#
# The double quotes match the Linux/macOS secrets.env convention; Windows
# reads them via scripts/migrate.ps1's parser which strips a single enclosing
# pair of quotes. Without quotes a `;` inside the value would still be safe
# on Windows (the parser splits on first `=` only) but cross-platform parity
# is easier to maintain if all platforms agree on quoting.
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
        # Lightweight local-workstation runtime tuning (native service ONLY —
        # set on the service key, never in the csproj/Docker image so the
        # container/k8s path keeps Server GC + metrics on). Workstation GC uses
        # a single heap instead of one-per-core, cutting idle working set for a
        # single-user, low-traffic service; metrics default off (no local
        # scraper). Override by editing this REG_MULTI_SZ value (e.g. set
        # DOTNET_gcServer=1 or Metrics__Enabled=true) and restarting the service.
        'DOTNET_gcServer=0'
        'DOTNET_gcConcurrent=1'
        'Metrics__Enabled=false'
        "Onnx__ModelPath=$(Join-Path $ModelDir 'model.onnx')"
        "Onnx__VocabPath=$(Join-Path $ModelDir 'vocab.txt')"
    )
    New-ItemProperty -Path $envKey -Name 'Environment' `
        -PropertyType MultiString -Value $envValues -Force | Out-Null

    # Grant Virtual Account write to ProgramData
    icacls $DataPrefix /grant:r "${account}:(OI)(CI)M" | Out-Null

    # NOTE: Start-Service intentionally deferred to a separate Start-ExpertiseService
    # step below so the install pipeline can run migrate between service-create
    # and service-start (issue #144). Starting here would race the migrate verb
    # against the API's own MigrationReversibilityTests-style startup checks.
}

# ---- Migrate -------------------------------------------------------------
function Invoke-Migrate {
    # Conditional, matching scripts/install.sh maybe_migrate(): a fresh install
    # has a placeholder secrets.env and cannot meaningfully migrate. Detect
    # that case, tell the operator, and continue (the service still gets
    # created — it just won't start cleanly until secrets are edited and
    # scripts/migrate.ps1 is run manually).
    $conn = $null
    if (Test-Path $SecretsFile) {
        foreach ($line in (Get-Content -LiteralPath $SecretsFile)) {
            $trim = $line.Trim()
            if (-not $trim -or $trim.StartsWith('#')) { continue }
            if ($trim -match '^ConnectionStrings__DefaultConnection\s*=\s*(.*)$') {
                $conn = $Matches[1].Trim('"', "'")
                break
            }
        }
    }

    if (-not $conn -or $conn -match 'CHANGE_ME') {
        Write-Warn "skipping migrate — ConnectionStrings__DefaultConnection unset or placeholder in $SecretsFile"
        Write-Warn "After editing the secrets file, run: $PSScriptRoot\migrate.ps1"
        Write-Warn "Then start the service: Start-Service $ServiceName"
        return
    }

    Write-Log 'running migrate (scripts/migrate.ps1 — idempotent; no-op when up to date)'
    & (Join-Path $PSScriptRoot 'migrate.ps1') `
        -InstallPrefix $InstallPrefix `
        -DataPrefix $DataPrefix `
        -SecretsFile $SecretsFile `
        -MigrateTimeout $MigrateTimeout
    if ($LASTEXITCODE -ne 0) {
        Write-Err "migrate failed — service NOT started; prior state intact. Inspect the output above, fix the schema/DB issue, then re-run scripts/install.ps1."
    }
}

# ---- Service start ------------------------------------------------------
function Start-ExpertiseService {
    Start-Service $ServiceName
    Write-Log 'service started'
}

# ---- Main ----------------------------------------------------------------
if (-not $SkipPreflight) { Invoke-Preflight }
Invoke-Publish
Confirm-Models
Confirm-ConfigStubs
Install-WindowsService
Invoke-Migrate
Start-ExpertiseService

Write-Log 'install complete'
Write-Log "  binary:  $BinDir"
Write-Log "  models:  $ModelDir"
Write-Log "  config:  $ConfigDir"
Write-Log "  logs:    $LogDir (Serilog file sink + EventLog)"
Write-Log "  bind:    $Bind"
Write-Log ''
Write-Log "Edit $SecretsFile to set the database connection string,"
Write-Log "then check the service with: Get-Service $ServiceName"
