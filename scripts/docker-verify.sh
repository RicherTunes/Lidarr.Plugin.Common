#!/bin/bash
#
# docker-verify.sh - Local Docker-based verification when CI is billing-blocked
#
# Usage:
#   ./docker-verify.sh [options]
#
# Options:
#   -p, --project PATH    Project directory (default: current directory)
#   -c, --config CONFIG   Build configuration (default: Release)
#   -s, --skip-tests      Skip running tests
#   -f, --filter FILTER   Test filter expression
#   -i, --interactive     Run container interactively
#   -h, --help            Show this help
#
# This script runs build and test verification in a Docker container,
# providing CI-equivalent results when GitHub Actions is unavailable
# due to billing limits.

set -e

# Defaults
PROJECT="."
CONFIGURATION="Release"
SKIP_TESTS=false
TEST_FILTER=""
INTERACTIVE=false
IMAGE="mcr.microsoft.com/dotnet/sdk:8.0"

# Parse arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        -p|--project)
            PROJECT="$2"
            shift 2
            ;;
        -c|--config)
            CONFIGURATION="$2"
            shift 2
            ;;
        -s|--skip-tests)
            SKIP_TESTS=true
            shift
            ;;
        -f|--filter)
            TEST_FILTER="$2"
            shift 2
            ;;
        -i|--interactive)
            INTERACTIVE=true
            shift
            ;;
        -h|--help)
            head -30 "$0" | tail -n +2 | sed 's/^# //'
            exit 0
            ;;
        *)
            echo "Unknown option: $1"
            exit 1
            ;;
    esac
done

# Check Docker availability
if ! docker version &>/dev/null; then
    echo "[ERROR] Docker is not available or not running."
    echo ""
    echo "Please ensure Docker is installed and running."
    exit 1
fi

# Resolve project path
PROJECT_ROOT=$(cd "$PROJECT" && pwd)
PROJECT_NAME=$(basename "$PROJECT_ROOT")

echo ""
echo "========================================"
echo "Docker Local Verification"
echo "========================================"
echo ""
echo "Project:       $PROJECT_NAME"
echo "Path:          $PROJECT_ROOT"
echo "Configuration: $CONFIGURATION"
echo "Image:         $IMAGE"
echo ""

# Find solution or project file
BUILD_TARGET=""
if [[ -f "$PROJECT_ROOT"/*.sln ]]; then
    BUILD_TARGET=$(basename "$PROJECT_ROOT"/*.sln)
    echo "Build target:  $BUILD_TARGET (solution)"
elif [[ -f "$PROJECT_ROOT"/*.csproj ]]; then
    BUILD_TARGET=$(basename "$PROJECT_ROOT"/*.csproj)
    echo "Build target:  $BUILD_TARGET (project)"
else
    # Search for solution in subdirectories
    BUILD_TARGET=$(find "$PROJECT_ROOT" -maxdepth 2 -name "*.sln" -print -quit)
    if [[ -n "$BUILD_TARGET" ]]; then
        BUILD_TARGET="${BUILD_TARGET#$PROJECT_ROOT/}"
        echo "Build target:  $BUILD_TARGET (nested solution)"
    else
        echo "[ERROR] No solution or project file found."
        exit 1
    fi
fi

echo ""

# Build command
BUILD_CMD="dotnet restore && dotnet build \"$BUILD_TARGET\" --configuration $CONFIGURATION"

if [[ "$SKIP_TESTS" == "false" ]]; then
    # Test command
    TEST_CMD='for testproj in $(find . -name "*.Tests.csproj" -o -name "*Tests.csproj" | head -5); do echo "Testing: $testproj"; dotnet test "$testproj" --configuration '"$CONFIGURATION"' --no-build --verbosity normal'

    if [[ -n "$TEST_FILTER" ]]; then
        TEST_CMD="$TEST_CMD --filter \"$TEST_FILTER\""
    fi

    TEST_CMD="$TEST_CMD; done"
    FULL_CMD="$BUILD_CMD && $TEST_CMD"
else
    FULL_CMD="$BUILD_CMD"
fi

# Execute in container
echo "Starting Docker container..."
echo ""

if [[ "$INTERACTIVE" == "true" ]]; then
    docker run --rm -it \
        -v "$PROJECT_ROOT:/src" \
        -w /src \
        -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
        -e DOTNET_NOLOGO=1 \
        "$IMAGE" \
        /bin/bash
else
    echo "Command: $FULL_CMD"
    echo ""

    docker run --rm \
        -v "$PROJECT_ROOT:/src" \
        -w /src \
        -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
        -e DOTNET_NOLOGO=1 \
        "$IMAGE" \
        /bin/bash -c "$FULL_CMD"
fi

EXIT_CODE=$?

echo ""

if [[ $EXIT_CODE -eq 0 ]]; then
    echo "========================================"
    echo "[PASS] Docker verification successful"
    echo "========================================"
else
    echo "========================================"
    echo "[FAIL] Docker verification failed (exit code: $EXIT_CODE)"
    echo "========================================"
fi

exit $EXIT_CODE
