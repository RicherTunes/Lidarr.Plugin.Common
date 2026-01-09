#!/usr/bin/env bash
# is-code-changed.sh - Determine if a list of changed files contains code changes
#
# Input: newline-separated list of file paths on stdin (as from git diff --name-only)
# Output: "true" if any file is code (tests should run), "false" if all are docs-only
#
# Docs-only patterns (tests skip):
#   - docs/**          (anything under docs/)
#   - Root-level *.md  (README.md, CHANGELOG.md, etc. - no slashes in path)
#   - .github/ISSUE_TEMPLATE/**
#
# Everything else is code (tests run), including:
#   - scripts/**/*.md  (markdown in code directories)
#   - src/**/*.md
#   - .github/workflows/**
#   - *.cs, *.ps1, *.json (except docs/), etc.

set -euo pipefail

is_docs_only_file() {
    local file="$1"

    # docs/** - anything under docs/ directory
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
