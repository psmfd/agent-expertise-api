#requires -version 5.1
<#
.SYNOPSIS
  Apply pending EF Core migrations to the configured Postgres database.

.DESCRIPTION
  Sources secrets.env (KEY=VALUE format, same shape as scripts/install.sh
  generates on Linux/macOS), then invokes ExpertiseApi.exe migrate (or the
  fdd-published `dotnet ExpertiseApi.dll migrate` equivalent). Idempotent:
  a no-op when no migrations are pending; non-zero exit when the apply fails.

  Invoked by scripts/install.ps1 between service creation and Start-Service
  on the upgrade path (issue #144). Also runnable standalone by the operator
  after editing secrets.env on a fresh install.

.PARAMETER InstallPrefix
  Install root containing bin/. Default: $env:ProgramFiles\ExpertiseApi.

.PARAMETER SecretsFile
  Path to secrets.env. Default: $env:ProgramData\ExpertiseApi\config\secrets.env.

.PARAMETER MigrateTimeout
  Wall-time limit in seconds for the migrate verb (default: 300). 0 disables
  the bound. On timeout the script stops the migrate job, kills the dotnet
  process tree, and exits non-zero with a clear message.

.EXAMPLE
  PS> scripts\migrate.ps1
  Applies pending migrations using the default install paths.

.EXAMPLE
  PS> scripts\migrate.ps1 -MigrateTimeout 0
  Applies pending migrations with no wall-time bound.

.NOTES
  Exit codes:
    0  success — migrations applied or none pending
    1  migrate verb itself failed (Npgsql / EF error — see logs)
    2  bad invocation (missing binary, missing/placeholder connection string)
#>
[CmdletBinding()]
param(
    [string]$InstallPrefix  = "$env:ProgramFiles\ExpertiseApi",
    [string]$DataPrefix     = "$env:ProgramData\ExpertiseApi",
    [string]$SecretsFile,
    [int]$MigrateTimeout    = 300
)

$ErrorActionPreference = 'Stop'

function Write-Log  { param([string]$m) Write-Host "[migrate] $m" }
function Write-Warn { param([string]$m) Write-Warning "[migrate] $m" }
# Write-Err MUST use Write-Host + exit (not Write-Error) because
# $ErrorActionPreference='Stop' turns Write-Error into a terminating error
# that ends the script BEFORE `exit 2` runs, defaulting the exit code to 1
# and silently violating the documented "2 = bad invocation" contract.
function Write-Err  { param([string]$m) Write-Host "[migrate] ERROR: $m" -ForegroundColor Red; exit 2 }

$BinDir = Join-Path $InstallPrefix 'bin'
if (-not $SecretsFile) {
    $SecretsFile = Join-Path $DataPrefix 'config\secrets.env'
}

# ---- Source secrets.env into process env --------------------------------
# Format mirrors Linux: KEY=VALUE per line, # for comments, blank lines OK.
# Values may contain `=` (connection strings do). Quoting is honored only
# for fully-wrapped "..." or '...' values — matches `set -a; . file; set +a`
# semantics closely enough for the connection-string and ASPNETCORE_* keys
# we care about. Anything more exotic must be set in the environment before
# invoking this script.
if (Test-Path $SecretsFile) {
    Write-Log "sourcing secrets from $SecretsFile"
    foreach ($line in (Get-Content -LiteralPath $SecretsFile)) {
        $trim = $line.Trim()
        if (-not $trim -or $trim.StartsWith('#')) { continue }
        $eq = $trim.IndexOf('=')
        if ($eq -lt 1) { continue }
        $name  = $trim.Substring(0, $eq).Trim()
        $value = $trim.Substring($eq + 1)
        if ($value.Length -ge 2 -and
            (($value[0] -eq '"' -and $value[-1] -eq '"') -or
             ($value[0] -eq "'" -and $value[-1] -eq "'"))) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        Set-Item -Path "env:$name" -Value $value
    }
} else {
    Write-Warn "no secrets file at $SecretsFile — relying on inherited environment"
}

# ---- Fail-fast connection-string guard ----------------------------------
$conn = [Environment]::GetEnvironmentVariable('ConnectionStrings__DefaultConnection', 'Process')
if (-not $conn) {
    Write-Err "ConnectionStrings__DefaultConnection is not set — edit $SecretsFile first"
}
if ($conn -match 'CHANGE_ME') {
    Write-Err "ConnectionStrings__DefaultConnection still contains CHANGE_ME placeholder — edit $SecretsFile first"
}

# Also propagate the ONNX paths so the migrate verb's startup (which still
# evaluates the conditional IEmbeddingGenerator registration) doesn't log
# spurious "model files not found" warnings during a real migrate run.
if (-not $env:Onnx__ModelPath) {
    $env:Onnx__ModelPath = Join-Path $DataPrefix 'models\model.onnx'
}
if (-not $env:Onnx__VocabPath) {
    $env:Onnx__VocabPath = Join-Path $DataPrefix 'models\vocab.txt'
}

# ---- Resolve binary -----------------------------------------------------
$exe = Join-Path $BinDir 'ExpertiseApi.exe'
$dll = Join-Path $BinDir 'ExpertiseApi.dll'

$migrateCmd    = $null
$migrateCmdArg = $null

if (Test-Path $exe) {
    Write-Log "invoking native binary: $exe migrate"
    $migrateCmd    = $exe
    $migrateCmdArg = 'migrate'
} elseif (Test-Path $dll) {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Write-Err "dotnet CLI not found in PATH (required for fdd publish layout at $BinDir)"
    }
    Write-Log "invoking framework-dependent: dotnet $dll migrate"
    $migrateCmd    = 'dotnet'
    $migrateCmdArg = "$dll migrate"
} else {
    Write-Err "no ExpertiseApi binary at $BinDir — run scripts/install.ps1 first"
}

# ---- Invoke with optional wall-time bound --------------------------------
# Capture current env into a hashtable so the background job can inherit it.
# PowerShell background jobs run in a child process that does NOT inherit the
# calling process's environment modifications (Set-Item env:* changes) — we
# must pass them explicitly.
$envSnapshot = @{}
foreach ($entry in [System.Environment]::GetEnvironmentVariables('Process').GetEnumerator()) {
    $envSnapshot[$entry.Key] = $entry.Value
}

if ($MigrateTimeout -gt 0) {
    Write-Log "migrate timeout: ${MigrateTimeout}s"

    # Write the exit code to a temp file from inside the job; that is the
    # only reliable way to surface $LASTEXITCODE across a job boundary.
    $rcFile = [System.IO.Path]::GetTempFileName()

    $job = Start-Job -ScriptBlock {
        param($cmd, $arg, $envMap, $rcPath)
        # Restore environment inside the job process.
        foreach ($k in $envMap.Keys) {
            [System.Environment]::SetEnvironmentVariable($k, $envMap[$k], 'Process')
        }
        if ($arg -eq 'migrate') {
            & $cmd 'migrate'
        } else {
            # Framework-dependent: cmd=dotnet, arg="path\ExpertiseApi.dll migrate"
            $parts = $arg -split ' ', 2
            & $cmd $parts[0] $parts[1]
        }
        [System.IO.File]::WriteAllText($rcPath, "$LASTEXITCODE")
    } -ArgumentList $migrateCmd, $migrateCmdArg, $envSnapshot, $rcFile

    $finished = Wait-Job -Job $job -Timeout $MigrateTimeout
    Receive-Job -Job $job | ForEach-Object { Write-Host $_ }

    if (-not $finished) {
        Stop-Job -Job $job
        # Best-effort: kill dotnet / ExpertiseApi processes spawned by the job.
        $jobStart = $job.PSBeginTime
        Get-Process -Name 'dotnet', 'ExpertiseApi' -ErrorAction SilentlyContinue |
            Where-Object { $_.StartTime -ge $jobStart } |
            ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
        Remove-Job -Job $job -Force
        Remove-Item -Path $rcFile -Force -ErrorAction SilentlyContinue
        Write-Host "[migrate] ERROR: migration exceeded ${MigrateTimeout}s; live binaries NOT swapped; service NOT touched — check for advisory-lock contention or a runaway ALTER TABLE" -ForegroundColor Red
        exit 1
    }

    Remove-Job -Job $job -Force
    $rc = 1
    if (Test-Path $rcFile) {
        $rcStr = (Get-Content -LiteralPath $rcFile -Raw).Trim()
        if ($rcStr -match '^\d+$') { $rc = [int]$rcStr }
        Remove-Item -Path $rcFile -Force -ErrorAction SilentlyContinue
    }
    exit $rc
} else {
    # No timeout bound — run directly so output streams through normally.
    if ($migrateCmdArg -eq 'migrate') {
        & $migrateCmd 'migrate'
    } else {
        $parts = $migrateCmdArg -split ' ', 2
        & $migrateCmd $parts[0] $parts[1]
    }
    exit $LASTEXITCODE
}
