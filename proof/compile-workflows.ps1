# Proof: rendered agentic workflows compile under gh-aw --strict.
# `plugins:` pins resolve against the published marketplace repo at compile time, so a local proof
# (marketplace not published) strips those lines in a temporary copy and compiles everything else.
param([string]$Rendered = (Join-Path $PSScriptRoot "..\.platform"), [switch]$KeepPlugins)
$ErrorActionPreference = 'Stop'
# gh-aw checks grader scripts with `bash -n`; on Windows prefer Git's bash over the WSL launcher.
$gitBash = Join-Path $env:ProgramFiles 'Git\bin'
if (Test-Path (Join-Path $gitBash 'bash.exe')) { $env:PATH = "$gitBash;$env:PATH" }
$d = Join-Path $env:TEMP "aw-proof"
if (Test-Path $d) { Remove-Item -Recurse -Force $d }
New-Item -ItemType Directory $d | Out-Null
Copy-Item -Recurse (Join-Path $Rendered "agentic-workflows\.github") (Join-Path $d ".github")
if (-not $KeepPlugins) {
  Get-ChildItem (Join-Path $d ".github\workflows\*.md") | ForEach-Object {
    $lines = Get-Content $_.FullName; $out = @(); $skip = $false
    foreach ($l in $lines) { if ($l -eq 'plugins:') { $skip = $true; continue }; if ($skip -and $l -match '^\s+- ') { continue }; $skip = $false; $out += $l }
    Set-Content -Encoding utf8NoBOM $_.FullName $out
  }
}
Push-Location $d
git init -q; git config user.email proof@local; git config user.name proof; git add -A 2>$null; git commit -qm init 2>$null
gh aw compile --strict 2>&1 | Select-String -Pattern '✓|✗|•' | ForEach-Object { $_.Line }
$code = $LASTEXITCODE
Get-ChildItem .github\workflows\*.lock.yml -ErrorAction SilentlyContinue | ForEach-Object { "lock: $($_.Name) $($_.Length) bytes" }
Pop-Location
exit $code
