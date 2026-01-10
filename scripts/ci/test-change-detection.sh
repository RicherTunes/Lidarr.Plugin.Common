#!/usr/bin/env bash
# test-change-detection.sh - Self-test for the docs-only change detection classifier
#
# Runs test cases against is-code-changed.sh to prevent regression.
# Exit 0 if all pass, non-zero if any fail.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CLASSIFIER="$SCRIPT_DIR/is-code-changed.sh"

PASSED=0
FAILED=0

assert_case() {
    local name="$1"
    local expected="$2"
    local input="$3"  # newline-separated file list

    local actual
    actual="$(printf '%s' "$input" | bash "$CLASSIFIER")"

    if [[ "$actual" != "$expected" ]]; then
        echo "FAIL: $name"
        echo "  Expected: $expected"
        echo "  Actual:   $actual"
        echo "  Input files:"
        printf '%s\n' "$input" | sed 's/^/    /'
        ((FAILED++))
        return 1
    fi

    echo "PASS: $name"
    ((PASSED++))
    return 0
}

echo "========================================"
echo "Change Detection Self-Test"
echo "========================================"
echo ""

# ============================================
# A. Docs-only cases (should return false)
# ============================================
echo "--- Docs-only cases (expected: false) ---"

assert_case "A1: docs/README.md" "false" "docs/README.md" || true

assert_case "A2: .github/ISSUE_TEMPLATE/bug_report.md" "false" \
    ".github/ISSUE_TEMPLATE/bug_report.md" || true

assert_case "A3: .github/ISSUE_TEMPLATE/config.yml" "false" \
    ".github/ISSUE_TEMPLATE/config.yml" || true

assert_case "A4: README.md (root)" "false" "README.md" || true

assert_case "A5: CHANGELOG.md (root)" "false" "CHANGELOG.md" || true

assert_case "A6: docs/dev-guide/E2E_HARDENING_ROADMAP.md" "false" \
    "docs/dev-guide/E2E_HARDENING_ROADMAP.md" || true

assert_case "A7: Mixed docs-only list" "false" \
    "README.md
docs/a.md
.github/ISSUE_TEMPLATE/x.md
CONTRIBUTING.md" || true

# ============================================
# B. Code cases (should return true)
# ============================================
echo ""
echo "--- Code cases (expected: true) ---"

assert_case "B1: scripts/e2e-runner.ps1" "true" "scripts/e2e-runner.ps1" || true

assert_case "B2: scripts/lib/e2e-gates.psm1" "true" "scripts/lib/e2e-gates.psm1" || true

assert_case "B3: .github/workflows/ci.yml" "true" ".github/workflows/ci.yml" || true

assert_case "B4: src/Utilities/FileSystemUtilities.cs" "true" \
    "src/Utilities/FileSystemUtilities.cs" || true

assert_case "B5: tests/Foo.Tests/FooTests.cs" "true" "tests/Foo.Tests/FooTests.cs" || true

assert_case "B6: build/PluginPackaging.targets" "true" "build/PluginPackaging.targets" || true

assert_case "B7: tools/PluginPack.psm1" "true" "tools/PluginPack.psm1" || true

assert_case "B8: global.json" "true" "global.json" || true

assert_case "B9: Directory.Packages.props" "true" "Directory.Packages.props" || true

assert_case "B10: NuGet.config" "true" "NuGet.config" || true

assert_case "B11: Mixed list (docs + code)" "true" \
    "README.md
docs/a.md
scripts/x.ps1" || true

assert_case "B12: .github/workflows/release.yml" "true" ".github/workflows/release.yml" || true

assert_case "B13: examples/SamplePlugin/Plugin.cs" "true" \
    "examples/SamplePlugin/Plugin.cs" || true

# ============================================
# D. Contract files in docs/ (treated as code - tripwire tests)
# ============================================
echo ""
echo "--- Contract files in docs/ (expected: true) ---"

assert_case "D1: docs/E2E_ERROR_CODES.md (tripwire test)" "true" \
    "docs/E2E_ERROR_CODES.md" || true

assert_case "D2: docs/reference/e2e-run-manifest.schema.json (schema contract)" "true" \
    "docs/reference/e2e-run-manifest.schema.json" || true

assert_case "D3: Contract file + other docs = code" "true" \
    "docs/README.md
docs/E2E_ERROR_CODES.md" || true

# ============================================
# C. Edge cases
# ============================================
echo ""
echo "--- Edge cases ---"

assert_case "C1: Empty list (conservative: run tests)" "true" "" || true

assert_case "C2: List with blank lines" "false" \
    "docs/a.md

README.md" || true

assert_case "C3: Case mismatch .github/issue_template (conservative)" "true" \
    ".github/issue_template/a.md" || true

assert_case "C4: Markdown in src/ (code, not docs-only)" "true" "src/NOTES.md" || true

assert_case "C5: Markdown in scripts/ (code, not docs-only)" "true" "scripts/README.md" || true

assert_case "C6: Root .json file (code)" "true" "cspell.json" || true

assert_case "C7: Root .yaml file (code)" "true" ".markdownlint.yaml" || true

assert_case "C8: Whitespace-only line ignored" "false" \
    "
docs/a.md
	" || true

assert_case "C9: ./ prefix stripped (docs)" "false" "./docs/README.md" || true

assert_case "C10: ./ prefix stripped (code)" "true" "./scripts/e2e-runner.ps1" || true

assert_case "C11: ./ prefix stripped (root md)" "false" "./README.md" || true

# ============================================
# Summary
# ============================================
echo ""
echo "========================================"
echo "Results: $PASSED passed, $FAILED failed"
echo "========================================"

if [[ $FAILED -gt 0 ]]; then
    echo ""
    echo "FAILURE: $FAILED test(s) failed"
    exit 1
fi

echo "SUCCESS: All tests passed"
exit 0
