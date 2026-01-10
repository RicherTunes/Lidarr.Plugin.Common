#!/usr/bin/env bash
# is-code-changed.sh - Determine if a list of changed files contains code changes
#
# Input: newline-separated list of file paths on stdin (as from git diff --name-only)
# Output: "true" if any file is code (tests should run), "false" if all are docs-only
#
# Docs-only patterns (tests skip):
#   - docs/**          (anything under docs/, EXCEPT contract files below)
#   - Root-level *.md  (README.md, CHANGELOG.md, etc. - no slashes in path)
#   - .github/ISSUE_TEMPLATE/**
#
# Contract files in docs/ that ALWAYS trigger tests:
#   - docs/E2E_ERROR_CODES.md           (parsed by Pester tripwire tests)
#   - docs/reference/e2e-run-manifest.schema.json (schema contract)
#
# Everything else is code (tests run), including:
#   - scripts/**/*.md  (markdown in code directories)
#   - src/**/*.md
#   - .github/workflows/**
#   - *.cs, *.ps1, *.json (except docs/), etc.

set -euo pipefail

is_docs_only_file() {
    local file="$1"

    # Contract files in docs/ that have tripwire tests - treat as code
    # These files are parsed by Pester tests; changes MUST run tests
    [[ "$file" == "docs/E2E_ERROR_CODES.md" ]] && return 1
    [[ "$file" == "docs/reference/e2e-run-manifest.schema.json" ]] && return 1

    # docs/** - anything under docs/ directory (except contract files above)
    [[ "$file" =~ ^docs/ ]] && return 0

    # .github/ISSUE_TEMPLATE/** - issue templates (case-sensitive)
    [[ "$file" =~ ^\.github/ISSUE_TEMPLATE/ ]] && return 0

    # Root-level *.md only (no slashes in path)
    # This matches README.md, CHANGELOG.md but NOT src/NOTES.md
    if [[ "$file" =~ \.md$ ]] && [[ ! "$file" =~ / ]]; then
        return 0
    fi

    # Everything else is code
    return 1
}

main() {
    local has_files=false
    local code_changed=false
    local file

    # Read files from stdin
    while IFS= read -r file || [[ -n "$file" ]]; do
        # Skip empty/whitespace-only lines
        file="${file#"${file%%[![:space:]]*}"}"  # trim leading
        file="${file%"${file##*[![:space:]]}"}"  # trim trailing
        [[ -z "$file" ]] && continue

        # Normalize: strip leading ./ prefix
        file="${file#./}"

        has_files=true

        if ! is_docs_only_file "$file"; then
            code_changed=true
            break
        fi
    done

    # If no files were provided (empty input), treat as code changed (conservative)
    if [[ "$has_files" == "false" ]]; then
        echo "true"
        return
    fi

    echo "$code_changed"
}

main
