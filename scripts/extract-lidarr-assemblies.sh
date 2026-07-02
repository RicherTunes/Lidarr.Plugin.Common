#!/usr/bin/env bash
set -euo pipefail

# Lidarr.Plugin.Common — Extract Lidarr assemblies from the plugins Docker image.
#
# Superset implementation: supports --mode minimal|full, --no-tar-fallback,
# digest-pinned pulls, tarball fallback chain, GITHUB_OUTPUT provenance, and
# a MANIFEST.txt provenance file in the output directory.
#
# Invoke via each plugin's thin shim (scripts/extract-lidarr-assemblies.sh)
# which sets plugin-specific defaults (OUT_DIR) and forwards all arguments.
#
# Usage:
#   bash ext/Lidarr.Plugin.Common/scripts/extract-lidarr-assemblies.sh [OPTIONS]
#
# Options:
#   --output-dir <path>     Where to write assemblies.
#                           Default: ext/Lidarr-docker/_output/net8.0
#   --mode minimal|full     minimal copies the REQ+OPT assembly lists;
#                           full copies the entire /app/bin directory.
#                           Default: minimal
#   --no-tar-fallback       Fail instead of falling back to a tarball download
#                           when Docker extraction fails.
#   --plugin-name <name>    Plugin name written to MANIFEST.txt header.
#                           Default: Plugin

OUT_DIR="ext/Lidarr-docker/_output/net8.0"
NO_TAR_FALLBACK="false"
MODE="minimal"
PLUGIN_NAME="Plugin"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --output-dir)
      OUT_DIR="$2"; shift 2 ;;
    --no-tar-fallback)
      NO_TAR_FALLBACK="true"; shift ;;
    --mode)
      MODE="$2"; shift 2 ;;
    --plugin-name)
      PLUGIN_NAME="$2"; shift 2 ;;
    *)
      echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

mkdir -p "$OUT_DIR"

# Required/optional assembly lists are defined up-front so both the Docker
# and tarball fallback paths share the same set.
# Windows runners rely on the tarball fallback when the Linux-only plugins
# image can't be pulled, so these must be defined before the Docker branch.
REQ=(
  Lidarr.dll
  Lidarr.Common.dll
  Lidarr.Core.dll
  Lidarr.Http.dll
  Lidarr.Api.V1.dll
  Lidarr.Host.dll
  NLog.dll
  Equ.dll
  FluentValidation.dll
)

OPT=(
  Microsoft.Extensions.Caching.Memory.dll
  Microsoft.Extensions.Caching.Abstractions.dll
  Microsoft.Extensions.DependencyInjection.Abstractions.dll
  Microsoft.Extensions.Logging.Abstractions.dll
  Microsoft.Extensions.Options.dll
  Microsoft.Extensions.Primitives.dll
)

LIDARR_DOCKER_VERSION="${LIDARR_DOCKER_VERSION:-nightly-3.1.3.4970}"
LIDARR_DOCKER_DIGEST="${LIDARR_DOCKER_DIGEST:-}"

TAG_IMAGE="ghcr.io/hotio/lidarr:${LIDARR_DOCKER_VERSION}"
IMAGE="$TAG_IMAGE"

DOCKER_OK=true

# ---------------------------------------------------------------------------
# Retry helper for flaky docker pulls
# ---------------------------------------------------------------------------
docker_pull_retry() {
  local image="$1"
  local attempts=${2:-3}
  local delay=${3:-3}
  local n=1
  until [ $n -gt $attempts ]; do
    if docker pull "$image" >/dev/null; then
      return 0
    fi
    echo "docker pull failed for $image (attempt $n/$attempts), retrying in ${delay}s..."
    sleep "$delay"
    n=$((n+1))
  done
  return 1
}

# ---------------------------------------------------------------------------
# Pull image — prefer digest when provided, fall back to tag
# ---------------------------------------------------------------------------
if [[ -n "$LIDARR_DOCKER_DIGEST" ]]; then
  DIGEST_IMAGE="ghcr.io/hotio/lidarr@${LIDARR_DOCKER_DIGEST}"
  echo "Attempting Docker image (digest): ${DIGEST_IMAGE}"
  if docker_pull_retry "$DIGEST_IMAGE"; then
    IMAGE="$DIGEST_IMAGE"
  else
    echo "Digest pull failed; attempting tag: ${TAG_IMAGE}"
    if ! docker_pull_retry "$TAG_IMAGE"; then
      echo "Tag pull failed as well; will use tar.gz fallback."
      DOCKER_OK=false
    fi
  fi
else
  echo "Using Docker image (tag): ${TAG_IMAGE}"
  if ! docker_pull_retry "$TAG_IMAGE"; then
    echo "Tag pull failed; will use tar.gz fallback."
    DOCKER_OK=false
  fi
fi

# ---------------------------------------------------------------------------
# Create container
# ---------------------------------------------------------------------------
CONTAINER_CREATED=false
if [[ "$DOCKER_OK" == true ]]; then
  if ! docker create --name temp-lidarr "$IMAGE" >/dev/null 2>&1; then
    echo "Failed to create container from ${IMAGE}, retrying with ${TAG_IMAGE}"
    if docker create --name temp-lidarr "$TAG_IMAGE" >/dev/null 2>&1; then
      CONTAINER_CREATED=true
    fi
  else
    CONTAINER_CREATED=true
  fi
fi

copy_from_container() {
  local file="$1"
  docker cp temp-lidarr:/app/bin/${file} "$OUT_DIR/" 2>/dev/null || return 1
}

FALLBACK_USED="none"

# ---------------------------------------------------------------------------
# Copy assemblies from container
# ---------------------------------------------------------------------------
if [[ "$CONTAINER_CREATED" == true && "$MODE" == "full" ]]; then
  echo "Mode=full: copying entire /app/bin"
  docker cp temp-lidarr:/app/bin/. "$OUT_DIR/"
elif [[ "$CONTAINER_CREATED" == true ]]; then
  echo "Mode=minimal: copying required Lidarr and Microsoft.Extensions assemblies"
  for f in "${REQ[@]}"; do
    copy_from_container "$f" || echo "Optional assembly missing: $f"
  done
  for f in "${OPT[@]}"; do
    copy_from_container "$f" || true
  done
fi

if [[ "$CONTAINER_CREATED" == true ]]; then
  docker rm -f temp-lidarr >/dev/null || true
fi

# ---------------------------------------------------------------------------
# Tarball fallback if Docker extraction did not produce Lidarr.Core.dll
# ---------------------------------------------------------------------------
if [[ ! -f "$OUT_DIR/Lidarr.Core.dll" ]]; then
  if [[ "$NO_TAR_FALLBACK" == "true" ]]; then
    echo "No tar.gz fallback requested and Lidarr.Core.dll missing" >&2
    exit 1
  fi
  echo "Docker-based extraction failed; attempting pinned tar.gz fallback..."

  CANDIDATES=()
  if [[ "$LIDARR_DOCKER_VERSION" =~ ([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+) ]]; then
    CANDIDATES+=("${BASH_REMATCH[1]}")
  fi
  # Current published release fallback, then a known-good emergency fallback.
  CANDIDATES+=("3.1.3.4968")
  CANDIDATES+=("2.13.3.4711")

  TAR_OK=false
  for VER in "${CANDIDATES[@]}"; do
    echo "Trying Lidarr tarball version: $VER"
    URL="https://github.com/Lidarr/Lidarr/releases/download/v${VER}/Lidarr.master.${VER}.linux-core-x64.tar.gz"
    if curl -fsSL --retry 3 "$URL" -o lidarr.tar.gz; then
      if tar -xzf lidarr.tar.gz; then
        TAR_OK=true
        break
      fi
    else
      echo "Tarball not available for version $VER at $URL"
    fi
  done

  # Tarballs may extract to 'Lidarr' or 'lidarr' depending on tooling
  LDIR=""
  if [[ -d Lidarr ]]; then
    LDIR="Lidarr"
  elif [[ -d lidarr ]]; then
    LDIR="lidarr"
  fi

  if [[ "$TAR_OK" == true && -n "$LDIR" ]]; then
    if [[ "$MODE" == "full" ]]; then
      cp -r "$LDIR/." "$OUT_DIR/"
    else
      for f in "${REQ[@]}"; do
        [[ -f "$LDIR/$f" ]] && cp "$LDIR/$f" "$OUT_DIR/" || echo "Missing $f (optional)"
      done
    fi
    rm -f lidarr.tar.gz
    FALLBACK_USED="tarball"
  else
    echo "All tarball fallbacks failed or unexpected layout (no Lidarr/ folder)." >&2
    exit 1
  fi
fi

# ---------------------------------------------------------------------------
# Sanity check
# ---------------------------------------------------------------------------
if [[ ! -f "$OUT_DIR/Lidarr.Core.dll" ]]; then
  echo "Missing Lidarr.Core.dll after extraction" >&2
  echo "Directory listing of OUT_DIR ($OUT_DIR):" >&2
  ls -la "$OUT_DIR" >&2 || true
  exit 1
fi
echo "Final assemblies in: $OUT_DIR"
ls -la "$OUT_DIR" || true

# ---------------------------------------------------------------------------
# Guardrail: .NET 8 version check
# Skip when tarball fallback was used (Windows runners can't pull the Linux-only
# plugins image; tarball fallback yields older .NET assemblies by design).
# ---------------------------------------------------------------------------
if [[ "$FALLBACK_USED" != "tarball" ]]; then
  RC="$OUT_DIR/Lidarr.runtimeconfig.json"
  if [[ -f "$RC" ]]; then
    if grep -qE '"version":\s*"8\.' "$RC"; then
      echo "[guardrail] OK: Lidarr runtime targets .NET 8"
    else
      echo "FATAL: Lidarr runtime does not target .NET 8 — the Docker image is likely a .NET 6 build." >&2
      echo "Docker tag: ${LIDARR_DOCKER_VERSION}" >&2
      echo "Runtime config:" >&2
      cat "$RC" >&2
      exit 1
    fi
  else
    echo "[guardrail] Lidarr.runtimeconfig.json not in output (minimal mode); skipping .NET version check"
  fi
else
  echo "[guardrail] Skipped: tarball fallback used (expected on Windows runners)"
fi

# ---------------------------------------------------------------------------
# Guardrail: FluentValidation must be 9.5.4.* (host-boundary package)
# ---------------------------------------------------------------------------
if [[ "$FALLBACK_USED" != "tarball" ]]; then
  FV_DLL="$OUT_DIR/FluentValidation.dll"
  if [[ -f "$FV_DLL" ]]; then
    FV_VER=$(strings "$FV_DLL" 2>/dev/null | grep -oE '^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$' | head -1 || true)
    if [[ -z "$FV_VER" ]]; then
      echo "[guardrail] WARNING: Could not read FluentValidation version from DLL metadata"
    elif [[ "$FV_VER" == 9.5.4.* ]]; then
      echo "[guardrail] OK: FluentValidation version $FV_VER (matches host 9.5.4)"
    else
      echo "FATAL: FluentValidation version $FV_VER does not match host expectation 9.5.4.*" >&2
      echo "The Lidarr Docker image ships a different FV version than expected." >&2
      echo "Docker tag: ${LIDARR_DOCKER_VERSION}" >&2
      exit 1
    fi
  else
    echo "[guardrail] FluentValidation.dll not in output (minimal mode); skipping FV version check"
  fi
else
  echo "[guardrail] Skipped FV check: tarball fallback used"
fi

# ---------------------------------------------------------------------------
# Provenance manifest
# ---------------------------------------------------------------------------
if [[ "$CONTAINER_CREATED" == true ]]; then
  USED_IMAGE="$IMAGE"
else
  USED_IMAGE="none"
fi

RESOLVED_FROM="$USED_IMAGE"
if [[ "$RESOLVED_FROM" == "none" || -z "$RESOLVED_FROM" ]]; then
  RESOLVED_FROM="$TAG_IMAGE"
fi
RESOLVED_DIGEST=$(docker image inspect --format '{{index .RepoDigests 0}}' "$RESOLVED_FROM" 2>/dev/null || true)

{
  echo "${PLUGIN_NAME} Assemblies Manifest"
  echo "Date: $(date -u +'%Y-%m-%dT%H:%M:%SZ')"
  echo "Mode: $MODE"
  echo "OutputDir: $OUT_DIR"
  echo "UsedImage: $USED_IMAGE"
  echo "DockerTag: $TAG_IMAGE"
  echo "DockerDigestEnv: ${LIDARR_DOCKER_DIGEST:-}"
  echo "ResolvedDigest: ${RESOLVED_DIGEST}"
  echo "Fallback: ${FALLBACK_USED}"
  echo "Files:"
  ls -1 "$OUT_DIR" | sed 's/^/  - /'
} > "$OUT_DIR/MANIFEST.txt"

# ---------------------------------------------------------------------------
# GitHub Actions step output: fallback_used
# Downstream steps can skip build/test when the tarball fallback yielded
# master-branch Lidarr DLLs (no NzbDrone.Core.Plugins namespace).
# ---------------------------------------------------------------------------
if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
  echo "fallback_used=${FALLBACK_USED}" >> "$GITHUB_OUTPUT"
fi
