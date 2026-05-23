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

.EXAMPLE
  PS> scripts\migrate.ps1
  Applies pending migrations using the default install paths.

.NOTES
  Exit codes:
    0  success — migrations applied or none pending
    1  migrate verb itself failed (Npgsql / EF error — see logs)
    2  bad invocation (missing binary, missing/placeholder connection string)
#>
[CmdletBinding()]
param(
    [string]$InstallPrefix = "$env:ProgramFiles\ExpertiseApi",
    [string]$DataPrefix    = "$env:ProgramData\ExpertiseApi",
    [string]$SecretsFile
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

# ---- Invoke the migrate verb -------------------------------------------
$exe = Join-Path $BinDir 'ExpertiseApi.exe'
$dll = Join-Path $BinDir 'ExpertiseApi.dll'

if (Test-Path $exe) {
    Write-Log "invoking native binary: $exe migrate"
    & $exe migrate
    exit $LASTEXITCODE
} elseif (Test-Path $dll) {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Write-Err "dotnet CLI not found in PATH (required for fdd publish layout at $BinDir)"
    }
    Write-Log "invoking framework-dependent: dotnet $dll migrate"
    & dotnet $dll migrate
    exit $LASTEXITCODE
} else {
    Write-Err "no ExpertiseApi binary at $BinDir — run scripts/install.ps1 first"
}
