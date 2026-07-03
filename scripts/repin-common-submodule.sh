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
#   --stage                Stage submodule + ext-common-sha.txt for commit;
#                          also self-verifies that the staged gitlink, sentinel file,
#                          and submodule HEAD all match the target SHA.
#   --verify               Check submodule is clean before/after
#   --verify-only          CI mode: fail if gitlink != ext-common-sha.txt or workflow pins are stale
#   --update-pins          Rewrite workflow SHA pins in .github/workflows/*.yml
#                          MANUAL ONLY -- requires a PAT with 'workflows' scope to push.
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

# Anchor all path operations to the git repo root so the script is cwd-independent.
REPO_ROOT=$(git rev-parse --show-toplevel 2>/dev/null) || {
    echo "Error: Not inside a git repository. Run this script from within the plugin repo."
    exit 1
}

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

# Auto-detect submodule path (uses REPO_ROOT, not pwd)
if [[ -z "$SUBMODULE_PATH" ]]; then
    if [[ -d "$REPO_ROOT/ext/Lidarr.Plugin.Common" ]]; then
        SUBMODULE_PATH="$REPO_ROOT/ext/Lidarr.Plugin.Common"
    elif [[ -d "$REPO_ROOT/ext/lidarr.plugin.common" ]]; then
        SUBMODULE_PATH="$REPO_ROOT/ext/lidarr.plugin.common"
    else
        echo -e "${RED}Error: Could not find Common submodule. Specify --path explicitly.${NC}"
        exit 1
    fi
fi

# Normalize SUBMODULE_PATH to absolute
if [[ "$SUBMODULE_PATH" != /* ]]; then
    SUBMODULE_PATH="$REPO_ROOT/$SUBMODULE_PATH"
fi
# Relative path for git index operations (git ls-files, git add)
SUBMODULE_REL_PATH="${SUBMODULE_PATH#$REPO_ROOT/}"

# Sentinel file is always at the repo root
SHA_FILE="$REPO_ROOT/ext-common-sha.txt"

# --sha-from-submodule mode: read SHA from submodule gitlink
if [[ "$SHA_FROM_SUBMODULE" == true ]]; then
    SHA=$(git -C "$SUBMODULE_PATH" rev-parse HEAD)
    echo -e "${CYAN}Read SHA from submodule HEAD: $SHA${NC}"
fi

# --verify-only mode: CI check that gitlink matches ext-common-sha.txt
if [[ "$VERIFY_ONLY" == true ]]; then
    echo -e "${CYAN}Verifying submodule gitlink matches $SHA_FILE...${NC}"

    if [[ ! -f "$SHA_FILE" ]]; then
        echo -e "${RED}ERROR: $SHA_FILE not found${NC}"
        exit 1
    fi

    # Validate file format: exactly 40 lowercase hex + LF (41 bytes, no BOM, no CRLF, no bare CR).
    # Byte-count alone is insufficient -- a 41-byte file ending in a BARE CR (0x0D) instead
    # of LF (0x0A) would pass `wc -c -eq 41` but fail the parallel .ps1 verifier (which
    # explicitly checks for 0x0A at byte 41). Without an explicit last-byte check, CI on
    # Linux accepts a file that a developer's local PowerShell rerun rejects. Bring the .sh
    # check into parity with .ps1 by asserting the terminator is LF.
    BYTE_LEN=$(wc -c < "$SHA_FILE")
    if [[ "$BYTE_LEN" -ne 41 ]]; then
        echo -e "${RED}ERROR: $SHA_FILE must be exactly 41 bytes (40 hex + LF), got $BYTE_LEN${NC}"
        echo -e "${YELLOW}Fix: Run ./scripts/repin-common-submodule.sh --sha-from-submodule --stage${NC}"
        exit 1
    fi
    LAST_BYTE_HEX=$(tail -c 1 "$SHA_FILE" | od -An -tx1 | tr -d ' \n')
    if [[ "$LAST_BYTE_HEX" != "0a" ]]; then
        echo -e "${RED}ERROR: $SHA_FILE must end with LF (0x0a), got 0x$LAST_BYTE_HEX (e.g. bare CR from a CRLF editor)${NC}"
        echo -e "${YELLOW}Fix: Run ./scripts/repin-common-submodule.sh --sha-from-submodule --stage${NC}"
        exit 1
    fi
    EXPECTED_SHA=$(head -c 40 "$SHA_FILE")
    if ! echo "$EXPECTED_SHA" | grep -qE '^[0-9a-f]{40}$'; then
        echo -e "${RED}ERROR: $SHA_FILE must contain exactly 40 lowercase hex chars${NC}"
        echo -e "${RED}Got: $EXPECTED_SHA${NC}"
        exit 1
    fi
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

    # Guard: fail on stale reusable Common workflow SHA pins.
    # Suppressed when the repo has no Common reusable-workflow references at all.
    WORKFLOW_DIR="$REPO_ROOT/.github/workflows"
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
                        echo -e "${RED}ERROR: Stale pin in $(basename "$f"):$LINENO_CTR${NC}"
                        echo -e "  ${RED}$(echo "$line" | sed 's/^[[:space:]]*//')${NC}"
                        echo -e "  ${RED}pinned: ${PIN_SHA:0:12}...  expected: ${EXPECTED_SHA:0:12}...${NC}"
                    fi
                fi
            done < "$f"
        done
        shopt -u nullglob
        if [[ "$STALE" -gt 0 ]]; then
            echo -e "${RED}ERROR: ${STALE}/${TOTAL_PINS} workflow pin(s) are stale.${NC}"
            echo -e "${CYAN}Fix: ./scripts/repin-common-submodule.sh $EXPECTED_SHA --update-pins --stage${NC}"
            exit 1
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

# Update ext-common-sha.txt (anchored to repo root, not pwd)
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
    WORKFLOW_DIR="$REPO_ROOT/.github/workflows"
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
    git -C "$REPO_ROOT" add "$SUBMODULE_PATH" "$SHA_FILE"
    if [[ "$UPDATE_PINS" == true && -d "$WORKFLOW_DIR" ]]; then
        git -C "$REPO_ROOT" add "$WORKFLOW_DIR"
    fi
    echo -e "\n${GREEN}Staged changes:${NC}"
    git -C "$REPO_ROOT" status --short "$SUBMODULE_REL_PATH" "ext-common-sha.txt"

    # Self-verify: sentinel file, submodule HEAD, and staged gitlink must all equal $SHA.
    # This catches silent drift (e.g. checkout failed silently, git add picked up wrong state).
    SENTINEL_SHA=$(head -c 40 "$SHA_FILE")
    SUBMODULE_HEAD=$(git -C "$SUBMODULE_PATH" rev-parse HEAD 2>/dev/null | tr -d '[:space:]')
    LS_OUTPUT=$(git -C "$REPO_ROOT" ls-files -s "$SUBMODULE_REL_PATH" 2>/dev/null)
    STAGED_SHA=$(echo "$LS_OUTPUT" | grep -oE '[0-9a-f]{40}' | head -1)
    FAILURES=()
    if [[ "$SENTINEL_SHA" != "$SHA" ]]; then
        FAILURES+=("ext-common-sha.txt contains '$SENTINEL_SHA', expected '$SHA'")
    fi
    if [[ "$SUBMODULE_HEAD" != "$SHA" ]]; then
        FAILURES+=("submodule HEAD is '$SUBMODULE_HEAD', expected '$SHA'")
    fi
    if [[ "$STAGED_SHA" != "$SHA" ]]; then
        FAILURES+=("staged gitlink is '$STAGED_SHA', expected '$SHA' (was git add run?)")
    fi
    if [[ ${#FAILURES[@]} -gt 0 ]]; then
        echo -e "${RED}ERROR: Self-verify FAILED -- silent pin drift detected:${NC}"
        for msg in "${FAILURES[@]}"; do
            echo -e "  ${RED}- $msg${NC}"
        done
        exit 1
    fi
    echo -e "${GREEN}Self-verify passed: sentinel, submodule HEAD, and staged gitlink all = $SHA${NC}"
fi

echo -e "\n${GREEN}Done! Submodule pinned to: $SHA${NC}"
echo -e "${GREEN}ext-common-sha.txt updated.${NC}"

if [[ "$STAGE" != true ]]; then
    echo -e "\n${CYAN}To stage: git -C '$REPO_ROOT' add '$SUBMODULE_PATH' '$SHA_FILE'${NC}"
fi