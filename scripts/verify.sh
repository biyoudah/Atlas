#!/usr/bin/env bash
# ==============================================================================
# Atlas — Local Contributor Verification Script (Bash / POSIX)
# ==============================================================================
# Executes build, formatting check, automated test suite, and identity audit.
# ==============================================================================

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

echo "============================================================"
echo "  ATLAS -- Contributor Verification Suite                   "
echo "============================================================"
echo "Repository root: ${REPO_ROOT}"
cd "${REPO_ROOT}"

# 1. Verify .NET SDK
echo ""
echo "[1/5] Checking .NET SDK version..."
dotnet --version

# 2. Check Code Formatting
echo ""
echo "[2/5] Verifying code formatting..."
if ! dotnet format Atlas.slnx --verify-no-changes; then
    echo "Formatting check failed. Run 'dotnet format Atlas.slnx' to fix." >&2
    exit 1
fi
echo "Formatting check passed."

# 3. Restore & Build Solution
echo ""
echo "[3/5] Building solution (Release)..."
dotnet build Atlas.slnx --configuration Release

# 4. Run Test Suite
echo ""
echo "[4/5] Running automated tests..."
dotnet test Atlas.slnx --configuration Release --no-build --verbosity normal

# 5. Clean Identity & Confidentiality Scan
echo ""
echo "[5/5] Running identity & confidentiality scan..."
FORBIDDEN_PATTERNS=(
    "$(printf '%s%s' 'biyou' 'dah')"
    "$(printf '%s%s' 'rota' 'rex')"
    "$(printf '%s%s' 'manu' 'metrics')"
    "$(printf '%s%s' 'msqua' 'red')"
    "$(printf '%s%s' 'oua' 'dah')"
    "$(printf '%s%s' 'ay' 'oub')"
    "10\.200\.14\.88"
)

VIOLATIONS=0
for pattern in "${FORBIDDEN_PATTERNS[@]}"; do
    if git grep -in -E "$pattern" -- ":!scripts/" 2>/dev/null; then
        echo "Found prohibited identifier pattern '$pattern'" >&2
        VIOLATIONS=$((VIOLATIONS + 1))
    fi
done

if [ "$VIOLATIONS" -gt 0 ]; then
    echo "Confidentiality scan failed: $VIOLATIONS violation(s) detected." >&2
    exit 1
fi

echo "Confidentiality scan passed: 0 prohibited patterns found."
echo ""
echo "============================================================"
echo "  VERIFICATION SUCCESSFUL: Ready for commit and pull request"
echo "============================================================"
