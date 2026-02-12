#!/usr/bin/env bash
# assert-net8-runtime.sh — shared guardrail for Lidarr .NET 8 runtime verification
#
# Usage:
#   bash scripts/ci/assert-net8-runtime.sh <assemblies-dir> [--allow-tarball-skip]
#
# Checks Lidarr.runtimeconfig.json in the extracted assemblies directory to
# verify the host runtime targets .NET 8. Prevents silently building plugins
# against a .NET 6 host, which causes System.Runtime assembly load failures.
#
# Exit codes:
#   0 — .NET 8 verified (or skipped with --allow-tarball-skip when runtimeconfig missing)
#   1 — .NET version mismatch or missing assemblies directory

set -euo pipefail

ASSEMBLIES_DIR="${1:-}"
ALLOW_SKIP=false

shift || true
while [[ $# -gt 0 ]]; do
  case "$1" in
    --allow-tarball-skip) ALLOW_SKIP=true; shift ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

if [[ -z "$ASSEMBLIES_DIR" ]]; then
  echo "Usage: assert-net8-runtime.sh <assemblies-dir> [--allow-tarball-skip]" >&2
  exit 2
fi

if [[ ! -d "$ASSEMBLIES_DIR" ]]; then
  echo "::error::Assemblies directory not found: $ASSEMBLIES_DIR" >&2
  exit 1
fi

RC="$ASSEMBLIES_DIR/Lidarr.runtimeconfig.json"

if [[ -f "$RC" ]]; then
  if grep -qE '"version":\s*"8\.' "$RC"; then
    echo "[guardrail] OK: Lidarr runtime targets .NET 8"
    exit 0
  else
    echo "::error::Lidarr runtime does NOT target .NET 8. The Docker image is likely a .NET 6 build." >&2
    echo "Runtime config contents:" >&2
    cat "$RC" >&2
    exit 1
  fi
else
  if [[ "$ALLOW_SKIP" == true ]]; then
    echo "[guardrail] Skipped: Lidarr.runtimeconfig.json not found (tarball fallback or minimal extraction)"
    exit 0
  else
    echo "::error::Lidarr.runtimeconfig.json not found in $ASSEMBLIES_DIR — cannot verify .NET version" >&2
    exit 1
  fi
fi
