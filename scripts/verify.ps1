# ==============================================================================
# Atlas — Local Contributor Verification Script (PowerShell)
# ==============================================================================
# Executes build, formatting check, automated test suite, and identity audit.
# ==============================================================================

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  ATLAS -- Contributor Verification Suite                   " -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "Repository root: $RepoRoot"

Set-Location $RepoRoot

# 1. Verify .NET SDK
Write-Host "`n[1/5] Checking .NET SDK version..." -ForegroundColor Yellow
$dotnetVersion = dotnet --version
Write-Host "Found .NET SDK: $dotnetVersion" -ForegroundColor Green

# 2. Check Code Formatting
Write-Host "`n[2/5] Verifying code formatting..." -ForegroundColor Yellow
dotnet format Atlas.slnx --verify-no-changes
if ($LASTEXITCODE -ne 0) {
    Write-Host "Formatting check failed. Run 'dotnet format Atlas.slnx' to fix." -ForegroundColor Red
    exit 1
}
Write-Host "Formatting check passed." -ForegroundColor Green

# 3. Restore and Build Solution
Write-Host "`n[3/5] Building solution (Release)..." -ForegroundColor Yellow
dotnet build Atlas.slnx --configuration Release
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed." -ForegroundColor Red
    exit 1
}
Write-Host "Build passed." -ForegroundColor Green

# 4. Run Test Suite
Write-Host "`n[4/5] Running automated tests..." -ForegroundColor Yellow
dotnet test Atlas.slnx --configuration Release --no-build --verbosity normal
if ($LASTEXITCODE -ne 0) {
    Write-Host "Automated tests failed." -ForegroundColor Red
    exit 1
}
Write-Host "All tests passed." -ForegroundColor Green

# 5. Clean Identity and Confidentiality Scan
Write-Host "`n[5/5] Running identity and confidentiality scan..." -ForegroundColor Yellow
$forbiddenPatterns = @(
    (('b','i','y','o','u','d','a','h') -join ''),
    (('r','o','t','a','r','e','x') -join ''),
    (('m','a','n','u','m','e','t','r','i','c','s') -join ''),
    (('m','s','q','u','a','r','e','d') -join ''),
    (('o','u','a','d','a','h') -join ''),
    (('a','y','o','u','b') -join ''),
    "10\.200\.14\.88"
)

$foundViolations = 0
foreach ($pattern in $forbiddenPatterns) {
    $matches = git grep -in -E "$pattern" -- ":!scripts/" 2>$null
    if ($matches) {
        Write-Host "Found prohibited identifier pattern '$pattern':" -ForegroundColor Red
        $matches | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkRed }
        $foundViolations++
    }
}

if ($foundViolations -gt 0) {
    Write-Host "`nConfidentiality scan failed: $foundViolations violation(s) detected." -ForegroundColor Red
    exit 1
}

Write-Host "Confidentiality scan passed: 0 prohibited patterns found." -ForegroundColor Green

Write-Host "`n============================================================" -ForegroundColor Green
Write-Host "  VERIFICATION SUCCESSFUL: Ready for commit and pull request" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
exit 0
