#!/bin/bash
#
# Re-pins ext/Lidarr.Plugin.Common (or ext/lidarr.plugin.common) submodule to a specific SHA.
#
# Usage (CI — recommended for bump-common workflows):
#   ./repin-common-submodule.sh --sha-from-submodule --stage --path <path>
#   ./repin-common-submodule.sh --verify-only --path <path>
#
# Usage (maintainer — manual from local checkout):
#   ./repin-common-submodule.sh <SHA> [--stage] [--verify] [--update-pins]
#
# Flags:
#   --sha-from-submodule   Read SHA from submodule HEAD (no manual SHA needed)
#   --stage                Stage submodule + ext-common-sha.txt for commit
#   --verify               Check submodule is clean before/after
#   --verify-only          CI mode: fail if gitlink != ext-common-sha.txt; warn on stale workflow pins
#   --update-pins          Rewrite workflow SHA pins in .github/workflows/*.yml
#                          MANUAL ONLY — requires a PAT with 'workflows' scope to push.
#                          CI bump workflows should NOT use this flag (GITHUB_TOKEN cannot
#                          push .github/workflows/ changes).
#   --path <path>          Explicit submodule path (default: auto-detect)
#
# Examples:
#   ./repin-common-submodule.sh 08f04e0c938669cb1d8890e179bc3b91f9c71725 --stage --verify
#
#   # After Common PR merges, get merge commit SHA and re-pin:
#   SHA=$(gh pr view 316 --repo RicherTunes/Lidarr.Plugin.Common --json mergeCommit --jq .mergeCommit.oid)
#   ./repin-common-submodule.sh "$SHA" --stage --verify
#
#   # Maintainer: also update workflow SHA pins (requires PAT):
#   ./repin-common-submodule.sh "$SHA" --stage --verify --update-pins
#
#   # CI verification (fail fast if mismatch):
#   ./repin-common-submodule.sh --verify-only

set -e

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

# Parse arguments
SHA=""
STAGE=false
VERIFY=false
VERIFY_ONLY=false
SUBMODULE_PATH=""
SHA_FROM_SUBMODULE=false
UPDATE_PINS=false

while [[ $# -gt 0 ]]; do
    case $1 in
        --stage)
            STAGE=true
            shift
            ;;
        --verify)
            VERIFY=true
            shift
            ;;
        --verify-only)
            VERIFY_ONLY=true
            shift
            ;;
        --path)
            SUBMODULE_PATH="$2"
            shift 2
            ;;
        --sha-from-submodule)
            SHA_FROM_SUBMODULE=true
            shift
            ;;
        --update-pins)
            UPDATE_PINS=true
            shift
            ;;
        *)
            if [[ -z "$SHA" ]]; then
                SHA="$1"
            fi
            shift
            ;;
    esac
done

# Auto-detect submodule path (needed for both modes)
if [[ -z "$SUBMODULE_PATH" ]]; then
    if [[ -d "ext/Lidarr.Plugin.Common" ]]; then
        SUBMODULE_PATH="ext/Lidarr.Plugin.Common"
    elif [[ -d "ext/lidarr.plugin.common" ]]; then
        SUBMODULE_PATH="ext/lidarr.plugin.common"
    else
        echo -e "${RED}Error: Could not find Common submodule. Specify --path explicitly.${NC}"
        exit 1
    fi
fi

# --sha-from-submodule mode: read SHA from submodule gitlink
if [[ "$SHA_FROM_SUBMODULE" == true ]]; then
    SHA=$(git -C "$SUBMODULE_PATH" rev-parse HEAD)
    echo -e "${CYAN}Read SHA from submodule HEAD: $SHA${NC}"
fi

# --verify-only mode: CI check that gitlink matches ext-common-sha.txt
if [[ "$VERIFY_ONLY" == true ]]; then
    SHA_FILE="ext-common-sha.txt"

    echo -e "${CYAN}Verifying submodule gitlink matches $SHA_FILE...${NC}"

    if [[ ! -f "$SHA_FILE" ]]; then
        echo -e "${RED}ERROR: $SHA_FILE not found${NC}"
        exit 1
    fi

    EXPECTED_SHA=$(cat "$SHA_FILE" | tr -d '[:space:]')
    ACTUAL_SHA=$(git -C "$SUBMODULE_PATH" rev-parse HEAD)

    echo -e "Expected (from $SHA_FILE): ${CYAN}$EXPECTED_SHA${NC}"
    echo -e "Actual (submodule HEAD):   ${CYAN}$ACTUAL_SHA${NC}"

    if [[ "$EXPECTED_SHA" != "$ACTUAL_SHA" ]]; then
        echo -e "${RED}ERROR: Submodule SHA mismatch!${NC}"
        echo -e "${RED}The submodule gitlink does not match $SHA_FILE.${NC}"
        echo -e "${YELLOW}Fix: Run ./scripts/repin-common-submodule.sh $EXPECTED_SHA --stage${NC}"
        exit 1
    fi

    # Also verify submodule is clean
    STATUS=$(git -C "$SUBMODULE_PATH" status --porcelain)
    if [[ -n "$STATUS" ]]; then
        echo -e "${RED}ERROR: Submodule has uncommitted changes:${NC}"
        echo "$STATUS"
        exit 1
    fi

    echo -e "${GREEN}Submodule verification passed.${NC}"

    # Advisory: check for stale workflow SHA pins (warning only, not a failure).
    # Suppressed when the repo has no Common reusable-workflow references at all.
    WORKFLOW_DIR=".github/workflows"
    if [[ -d "$WORKFLOW_DIR" ]]; then
        STALE=0
        TOTAL_PINS=0
        shopt -s nullglob
        for f in "$WORKFLOW_DIR"/*.yml "$WORKFLOW_DIR"/*.yaml; do
            LINENO_CTR=0
            while IFS= read -r line; do
                ((LINENO_CTR++)) || true
                if echo "$line" | grep -qE 'uses:\s+RicherTunes/Lidarr\.Plugin\.Common/.+@[0-9a-f]{40}'; then
                    ((TOTAL_PINS++)) || true
                    PIN_SHA=$(echo "$line" | grep -oE '@[0-9a-f]{40}' | tr -d '@')
                    if [[ -n "$PIN_SHA" && "$PIN_SHA" != "$EXPECTED_SHA" ]]; then
                        ((STALE++)) || true
                        echo -e "${YELLOW}WARNING: Stale pin in $(basename "$f"):$LINENO_CTR${NC}"
                        echo -e "  ${YELLOW}$(echo "$line" | sed 's/^[[:space:]]*//')${NC}"
                        echo -e "  ${YELLOW}pinned: ${PIN_SHA:0:12}...  expected: ${EXPECTED_SHA:0:12}...${NC}"
                    fi
                fi
            done < "$f"
        done
        shopt -u nullglob
        if [[ "$STALE" -gt 0 ]]; then
            echo -e "${YELLOW}${STALE}/${TOTAL_PINS} workflow pin(s) are stale.${NC}"
            echo -e "${CYAN}Fix: ./scripts/repin-common-submodule.sh $EXPECTED_SHA --update-pins --stage${NC}"
        fi
    fi

    exit 0
fi

# Normal re-pin mode requires SHA
if [[ -z "$SHA" ]]; then
    echo -e "${RED}Error: SHA is required${NC}"
    echo "Usage: $0 <SHA> [--stage] [--verify] [--path <submodule-path>]"
    echo "       $0 --verify-only  # CI mode: check gitlink matches ext-common-sha.txt"
    exit 1
fi

# Validate 40-hex (case-insensitive), then normalize to lowercase
if ! echo "$SHA" | grep -qiE '^[0-9a-f]{40}$'; then
    echo -e "${RED}Error: SHA must be a 40-character hex string, got: $SHA${NC}"
    exit 1
fi
SHA=$(echo "$SHA" | tr '[:upper:]' '[:lower:]')

echo -e "${CYAN}Re-pinning Common submodule at: $SUBMODULE_PATH${NC}"
echo -e "${CYAN}Target SHA: $SHA${NC}"

# Verify submodule is clean before operation
if [[ "$VERIFY" == true ]]; then
    echo -e "\n${YELLOW}Verifying submodule is clean...${NC}"
    STATUS=$(git -C "$SUBMODULE_PATH" status --porcelain)
    if [[ -n "$STATUS" ]]; then
        echo -e "${RED}ERROR: Submodule has uncommitted changes:${NC}"
        echo "$STATUS"
        exit 1
    fi
    echo -e "${GREEN}Submodule is clean.${NC}"
fi

# Fetch and checkout the target SHA
echo -e "\n${YELLOW}Fetching origin...${NC}"
git -C "$SUBMODULE_PATH" fetch origin

echo -e "${YELLOW}Checking out $SHA...${NC}"
if ! git -C "$SUBMODULE_PATH" checkout "$SHA"; then
    echo -e "${RED}Failed to checkout SHA: $SHA${NC}"
    exit 1
fi

# Update ext-common-sha.txt
SHA_FILE="ext-common-sha.txt"
echo -e "\n${YELLOW}Updating $SHA_FILE...${NC}"
printf '%s\n' "$SHA" > "$SHA_FILE"

# Verify the checkout
echo -e "\n${YELLOW}Verifying checkout...${NC}"
CURRENT_SHA=$(git -C "$SUBMODULE_PATH" rev-parse HEAD)
if [[ "$CURRENT_SHA" != "$SHA" ]]; then
    echo -e "${RED}SHA mismatch after checkout. Expected: $SHA, Got: $CURRENT_SHA${NC}"
    exit 1
fi
echo -e "${GREEN}Checkout verified: $CURRENT_SHA${NC}"

if [[ "$UPDATE_PINS" == true ]]; then
    WORKFLOW_DIR=".github/workflows"
    if [[ -d "$WORKFLOW_DIR" ]]; then
        echo -e "\n${YELLOW}Updating workflow SHA pins...${NC}"
        UPDATED=0
        # Guard glob: only iterate if files exist. Include .yml and .yaml.
        shopt -s nullglob
        for f in "$WORKFLOW_DIR"/*.yml "$WORKFLOW_DIR"/*.yaml; do
            if grep -q "RicherTunes/Lidarr\.Plugin\.Common/.*@[0-9a-f]\{40\}" "$f" 2>/dev/null; then
                # Anchored to non-comment uses: lines; portable (no sed -i)
                perl -pi -e "s|^(\s*uses:\s+RicherTunes/Lidarr\.Plugin\.Common/.+\@)[0-9a-f]{40}|\${1}${SHA}|g" "$f"
                ((UPDATED++)) || true
                echo -e "  ${GREEN}Updated: $(basename "$f")${NC}"
            fi
        done
        shopt -u nullglob
        echo -e "${CYAN}Updated $UPDATED workflow file(s).${NC}"
    fi
fi

# Verify submodule is clean after operation
if [[ "$VERIFY" == true ]]; then
    echo -e "\n${YELLOW}Verifying submodule is still clean...${NC}"
    STATUS=$(git -C "$SUBMODULE_PATH" status --porcelain)
    if [[ -n "$STATUS" ]]; then
        echo -e "${YELLOW}WARNING: Submodule has changes after checkout:${NC}"
        echo "$STATUS"
    else
        echo -e "${GREEN}Submodule is clean.${NC}"
    fi
fi

# Stage changes if requested
if [[ "$STAGE" == true ]]; then
    echo -e "\n${YELLOW}Staging changes...${NC}"
    git add "$SUBMODULE_PATH" "$SHA_FILE"
    if [[ "$UPDATE_PINS" == true && -d ".github/workflows" ]]; then
        git add ".github/workflows"
    fi
    echo -e "\n${GREEN}Staged changes:${NC}"
    git status --short "$SUBMODULE_PATH" "$SHA_FILE"
fi

echo -e "\n${GREEN}Done! Submodule pinned to: $SHA${NC}"
echo -e "${GREEN}ext-common-sha.txt updated.${NC}"

if [[ "$STAGE" != true ]]; then
    echo -e "\n${CYAN}To stage: git add $SUBMODULE_PATH $SHA_FILE${NC}"
fi
