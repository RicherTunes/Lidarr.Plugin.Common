#!/bin/bash
#
# Bulk-update workflow SHA pins across all plugin repos.
#
# MAINTAINER ONLY — requires:
#   1. Local checkouts of all plugin repos as siblings of this repo
#   2. A PAT with 'workflows' scope to push .github/workflows/ changes
#
# Usage:
#   ./bulk-update-workflow-pins.sh [--commit] [--push]
#
# Without flags: updates files locally, prints what changed.
# With --commit: also creates a commit in each repo.
# With --push:   also pushes the branch (implies --commit).
#
# The script reads the SHA from this repo's own main branch HEAD,
# or from ext-common-sha.txt if run from inside a plugin repo.

set -e

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m'

COMMIT=false
PUSH=false

while [[ $# -gt 0 ]]; do
    case $1 in
        --commit) COMMIT=true; shift ;;
        --push)   PUSH=true; COMMIT=true; shift ;;
        *)        shift ;;
    esac
done

# Determine Common SHA: use this repo's HEAD
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
COMMON_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
SHA=$(git -C "$COMMON_ROOT" rev-parse HEAD)

if ! echo "$SHA" | grep -qE '^[0-9a-f]{40}$'; then
    echo -e "${RED}ERROR: Could not determine Common SHA from $COMMON_ROOT${NC}"
    exit 1
fi
echo -e "${CYAN}Common SHA: $SHA${NC}"
echo -e "${CYAN}Short: ${SHA:0:12}...${NC}"
echo ""

# Plugin repos expected as siblings
PARENT_DIR="$(dirname "$COMMON_ROOT")"
PLUGINS=(brainarr tidalarr qobuzarr applemusicarr)
UPDATED_REPOS=()

for plugin in "${PLUGINS[@]}"; do
    REPO_DIR="$PARENT_DIR/$plugin"
    if [[ ! -d "$REPO_DIR/.github/workflows" ]]; then
        echo -e "${YELLOW}SKIP: $plugin (not found at $REPO_DIR or no .github/workflows/)${NC}"
        continue
    fi

    echo -e "${CYAN}--- $plugin ---${NC}"

    # Run the repin script in update-pins mode (dry: just update files)
    COMMON_SUB="$REPO_DIR/ext/Lidarr.Plugin.Common"
    if [[ ! -d "$COMMON_SUB" ]]; then
        COMMON_SUB="$REPO_DIR/ext/lidarr.plugin.common"
    fi
    if [[ ! -d "$COMMON_SUB" ]]; then
        echo -e "${YELLOW}SKIP: $plugin (no Common submodule found)${NC}"
        continue
    fi

    # Use the repin script from Common (not the submodule copy)
    REPIN_SCRIPT="$COMMON_ROOT/scripts/repin-common-submodule.sh"

    # Check if any workflow files reference Common
    HAS_PINS=false
    shopt -s nullglob
    for f in "$REPO_DIR/.github/workflows"/*.yml "$REPO_DIR/.github/workflows"/*.yaml; do
        if grep -q "RicherTunes/Lidarr\.Plugin\.Common/.*@[0-9a-f]\{40\}" "$f" 2>/dev/null; then
            HAS_PINS=true
            break
        fi
    done
    shopt -u nullglob

    if [[ "$HAS_PINS" == false ]]; then
        echo -e "  ${GREEN}No Common workflow pins found — nothing to update${NC}"
        continue
    fi

    # Update workflow SHA pins in-place
    CHANGED=0
    shopt -s nullglob
    for f in "$REPO_DIR/.github/workflows"/*.yml "$REPO_DIR/.github/workflows"/*.yaml; do
        if grep -q "RicherTunes/Lidarr\.Plugin\.Common/.*@[0-9a-f]\{40\}" "$f" 2>/dev/null; then
            perl -pi -e "s|^(\s*uses:\s+RicherTunes/Lidarr\.Plugin\.Common/.+\@)[0-9a-f]{40}|\${1}${SHA}|g" "$f"
            ((CHANGED++)) || true
            echo -e "  ${GREEN}Updated: $(basename "$f")${NC}"
        fi
    done
    shopt -u nullglob

    if [[ "$CHANGED" -eq 0 ]]; then
        echo -e "  ${GREEN}All pins already current${NC}"
        continue
    fi

    UPDATED_REPOS+=("$plugin")

    if [[ "$COMMIT" == true ]]; then
        BRANCH="chore/update-workflow-pins-${SHA:0:7}"
        cd "$REPO_DIR"
        git checkout -B "$BRANCH" 2>/dev/null
        git add .github/workflows/
        git commit -m "chore(ci): update Common workflow SHA pins to ${SHA:0:7}

Pins all RicherTunes/Lidarr.Plugin.Common/ workflow refs to $SHA

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>" 2>/dev/null || echo -e "  ${YELLOW}No changes to commit${NC}"

        if [[ "$PUSH" == true ]]; then
            git push -u origin "$BRANCH" --force 2>/dev/null
            echo -e "  ${GREEN}Pushed branch: $BRANCH${NC}"
        fi
        cd - > /dev/null
    fi
done

echo ""
echo -e "${CYAN}========================================${NC}"
if [[ ${#UPDATED_REPOS[@]} -eq 0 ]]; then
    echo -e "${GREEN}All plugin repos are already up to date.${NC}"
else
    echo -e "${GREEN}Updated ${#UPDATED_REPOS[@]} repo(s): ${UPDATED_REPOS[*]}${NC}"
    if [[ "$COMMIT" == false ]]; then
        echo -e "${YELLOW}Run with --commit to create commits, --push to also push branches.${NC}"
    fi
fi
echo -e "${CYAN}========================================${NC}"
