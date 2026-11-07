<#
.SYNOPSIS
  Loads the runs of one experiment into PostgreSQL, following the experiment manifest.

.DESCRIPTION
  Only slots that are done, not warm-up, and whose successful attempt has a bundle are loaded.
  Warm-up and rejected/interrupted slots are skipped (their bundles are NOT labeled in the bundle
  itself, so never ingest data\runs\* blindly). Re-running is safe: runs already loaded are skipped.
  Needs no elevation. Reads credentials from the gitignored .env; never prints them.

.EXAMPLE
  .\scripts\ingest-experiment.ps1 -ExperimentId exp-20261107-182200-e58103 -DryRun
  .\scripts\ingest-experiment.ps1 -ExperimentId exp-20261107-182200-e58103
#>
param(
  [Parameter(Mandatory = $true)][ValidatePattern('^exp-[0-9A-Za-z-]+$')][string]$ExperimentId,
  [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $root "data\experiments\$ExperimentId\manifest.json"
if (-not (Test-Path $manifestPath)) { throw "manifest not found: $manifestPath" }
$m = Get-Content $manifestPath -Raw | ConvertFrom-Json
Write-Host "experiment $($m.experiment_id): scenario=$($m.scenario) state=$($m.state)"
if ($m.state -ne 'completed') { Write-Warning "experiment state is '$($m.state)', not 'completed'; loading only finished slots" }

$kv = @{}
Get-Content (Join-Path $root '.env') | Where-Object { $_ -match '^\s*[A-Z_]+=' } | ForEach-Object {
  $i = $_.IndexOf('='); $kv[$_.Substring(0, $i)] = $_.Substring($i + 1) }
foreach ($k in 'WTBA_DB_USER','WTBA_DB_PASSWORD','WTBA_DB_PORT','WTBA_DB_NAME','WTBA_HASH_SALT') {
  if (-not $kv.ContainsKey($k)) { throw ".env is missing $k" } }
$pw = [uri]::EscapeDataString($kv.WTBA_DB_PASSWORD)
$env:WTBA_DATABASE_URL = "postgresql+psycopg://$($kv.WTBA_DB_USER):$pw@127.0.0.1:$($kv.WTBA_DB_PORT)/$($kv.WTBA_DB_NAME)"
$env:WTBA_HASH_SALT = $kv.WTBA_HASH_SALT

$todo = @(); $skipped = @()
foreach ($s in $m.slots) {
  if ($s.warmup) { $skipped += "slot $($s.index + 1) ($($s.condition)): warm-up"; continue }
  if ($s.status -ne 'done') { $skipped += "slot $($s.index + 1) ($($s.condition)): status=$($s.status)"; continue }
  $att = @($s.attempts | Where-Object { $_.status -eq 'done' -and $_.bundle_path }) | Select-Object -Last 1
  if (-not $att) { $skipped += "slot $($s.index + 1) ($($s.condition)): no successful attempt"; continue }
  $full = [IO.Path]::GetFullPath($att.bundle_path)
  # only ever read bundles from this repo's data\runs
  $runsRoot = [IO.Path]::GetFullPath((Join-Path $root 'data\runs')) + '\'
  if (-not $full.StartsWith($runsRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "bundle outside data\runs: $full" }
  $todo += [pscustomobject]@{ Slot = $s.index + 1; Condition = $s.condition; Path = $full }
}
Write-Host ("to load: {0} runs ({1} A, {2} B); skipped: {3}" -f $todo.Count, @($todo | Where-Object Condition -eq 'A').Count, @($todo | Where-Object Condition -eq 'B').Count, $skipped.Count)
$skipped | ForEach-Object { Write-Host "  skip: $_" }

Push-Location (Join-Path $root 'pipeline')
try {
  foreach ($t in $todo) {
    $args2 = @('run', 'wtba', 'ingest', $t.Path)
    if ($DryRun) { $args2 += '--dry-run' }
    $out = & uv @args2 2>&1 | Select-Object -Last 1
    Write-Host ("  slot {0,2} {1}: {2}" -f $t.Slot, $t.Condition, $out)
  }
} finally { Pop-Location; Remove-Item Env:WTBA_DATABASE_URL, Env:WTBA_HASH_SALT -ErrorAction SilentlyContinue }
