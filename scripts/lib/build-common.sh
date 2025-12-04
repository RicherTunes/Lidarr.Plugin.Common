#!/bin/bash
# =============================================================================
# Lidarr Plugin Common Build Library
# =============================================================================
# Shared functions for Lidarr plugin build scripts.
# Source this file in your plugin's build.sh:
#   source "ext/Lidarr.Plugin.Common/scripts/lib/build-common.sh"
# =============================================================================

# Colors for output
export RED='\033[0;31m'
export GREEN='\033[0;32m'
export BLUE='\033[0;34m'
export CYAN='\033[0;36m'
export YELLOW='\033[1;33m'
export WHITE='\033[1;37m'
export GRAY='\033[0;37m'
export NC='\033[0m' # No Color

# =============================================================================
# Logging Functions
# =============================================================================

log_info() {
    echo -e "${BLUE}ℹ${NC} $1"
}

log_success() {
    echo -e "${GREEN}✓${NC} $1"
}

log_warning() {
    echo -e "${YELLOW}⚠${NC} $1"
}

log_error() {
    echo -e "${RED}✗${NC} $1" >&2
}

log_step() {
    echo -e "${CYAN}▶${NC} $1"
}

log_header() {
    echo ""
    echo -e "${WHITE}═══════════════════════════════════════════════════════════════${NC}"
    echo -e "${WHITE}  $1${NC}"
    echo -e "${WHITE}═══════════════════════════════════════════════════════════════${NC}"
    echo ""
}

# =============================================================================
# Build Functions
# =============================================================================

# Get common build flags for dotnet
get_build_flags() {
    local flags="-p:RunAnalyzersDuringBuild=false"
    flags="$flags -p:EnableNETAnalyzers=false"
    flags="$flags -p:TreatWarningsAsErrors=false"
    echo "$flags"
}

# Clean build artifacts
do_clean() {
    local project="$1"
    log_step "Cleaning build artifacts..."
    dotnet clean "$project" --configuration Debug -v q 2>/dev/null || true
    dotnet clean "$project" --configuration Release -v q 2>/dev/null || true

    # Remove bin and obj directories
    find . -type d \( -name 'bin' -o -name 'obj' \) -not -path './ext/*' -exec rm -rf {} + 2>/dev/null || true
    log_success "Clean complete"
}

# Restore packages
do_restore() {
    local project="$1"
    log_step "Restoring packages..."
    dotnet restore "$project" -v minimal
    log_success "Restore complete"
}

# Build project
do_build() {
    local project="$1"
    local configuration="${2:-Debug}"
    local extra_flags="${3:-}"

    log_step "Building ${configuration}..."

    local flags
    flags=$(get_build_flags)

    if dotnet build "$project" \
        --configuration "$configuration" \
        --no-restore \
        $flags \
        $extra_flags; then
        log_success "Build complete: $configuration"
        return 0
    else
        log_error "Build failed"
        return 1
    fi
}

# Deploy plugin to Lidarr
do_deploy() {
    local source_dir="$1"
    local deploy_path="$2"
    local plugin_name="$3"

    if [[ -z "$deploy_path" ]]; then
        log_warning "No deploy path specified, skipping deployment"
        return 0
    fi

    log_step "Deploying to: $deploy_path"

    if [[ ! -d "$deploy_path" ]]; then
        mkdir -p "$deploy_path"
    fi

    # Copy main DLL
    local dll_path
    dll_path=$(find "$source_dir" -name "Lidarr.Plugin.${plugin_name}.dll" -path '*/bin/*' | head -1)

    if [[ -z "$dll_path" ]]; then
        log_error "Could not find built plugin DLL"
        return 1
    fi

    cp "$dll_path" "$deploy_path/"

    # Copy PDB if exists
    local pdb_path="${dll_path%.dll}.pdb"
    if [[ -f "$pdb_path" ]]; then
        cp "$pdb_path" "$deploy_path/"
    fi

    # Copy plugin.json if exists in root
    if [[ -f "plugin.json" ]]; then
        cp "plugin.json" "$deploy_path/"
    fi

    log_success "Deployed to: $deploy_path"
}

# =============================================================================
# Version Functions
# =============================================================================

# Read version from VERSION file
get_version() {
    if [[ -f "VERSION" ]]; then
        cat VERSION | tr -d '[:space:]'
    else
        echo "0.1.0-dev"
    fi
}

# Get version prefix (e.g., 1.2.3 from 1.2.3-beta)
get_version_prefix() {
    local version
    version=$(get_version)
    echo "$version" | grep -oE '^[0-9]+\.[0-9]+\.[0-9]+' || echo "0.1.0"
}

# =============================================================================
# Lidarr Assembly Functions
# =============================================================================

# Check if Lidarr assemblies exist
check_lidarr_assemblies() {
    local path="${1:-ext/Lidarr/_output/net8.0}"

    if [[ -d "$path" ]] && [[ -f "$path/Lidarr.Core.dll" ]]; then
        log_success "Lidarr assemblies found at: $path"
        return 0
    else
        log_warning "Lidarr assemblies not found at: $path"
        return 1
    fi
}

# Extract Lidarr assemblies from Docker
extract_lidarr_from_docker() {
    local output_dir="${1:-ext/Lidarr/_output/net8.0}"
    local docker_version="${2:-pr-plugins-2.14.2.4786}"

    log_step "Extracting Lidarr assemblies from Docker..."

    local image="ghcr.io/hotio/lidarr:${docker_version}"

    docker pull "$image" || { log_error "Failed to pull Docker image"; return 1; }

    local container_id
    container_id=$(docker create "$image")

    mkdir -p "$output_dir"
    docker cp "${container_id}:/app/bin/." "$output_dir/"
    docker rm "$container_id"

    log_success "Extracted Lidarr assemblies to: $output_dir"
}

# =============================================================================
# Test Functions
# =============================================================================

# Run tests
do_test() {
    local project="${1:-}"
    local configuration="${2:-Debug}"

    log_step "Running tests..."

    local test_args="--configuration $configuration --no-build"

    if [[ -n "$project" ]]; then
        dotnet test "$project" $test_args
    else
        dotnet test $test_args
    fi

    log_success "Tests complete"
}

# =============================================================================
# Utility Functions
# =============================================================================

# Check if command exists
command_exists() {
    command -v "$1" &> /dev/null
}

# Ensure dotnet is available
ensure_dotnet() {
    if ! command_exists dotnet; then
        log_error "dotnet CLI not found. Please install .NET SDK."
        exit 1
    fi
}

# Parse common build arguments
# Usage: parse_build_args "$@"
# Sets: BUILD_CONFIG, BUILD_DEPLOY, BUILD_CLEAN, BUILD_RESTORE, BUILD_VERBOSE
parse_build_args() {
    export BUILD_CONFIG="Debug"
    export BUILD_DEPLOY=false
    export BUILD_DEPLOY_PATH=""
    export BUILD_CLEAN=false
    export BUILD_RESTORE=false
    export BUILD_VERBOSE=false
    export BUILD_HELP=false
    export BUILD_NO_BUILD=false

    while [[ $# -gt 0 ]]; do
        case $1 in
            Debug|Release)
                BUILD_CONFIG="$1"
                shift
                ;;
            --deploy)
                BUILD_DEPLOY=true
                shift
                ;;
            --deploy-path)
                BUILD_DEPLOY_PATH="$2"
                shift 2
                ;;
            --clean)
                BUILD_CLEAN=true
                shift
                ;;
            --restore)
                BUILD_RESTORE=true
                shift
                ;;
            --no-build)
                BUILD_NO_BUILD=true
                shift
                ;;
            --verbose)
                BUILD_VERBOSE=true
                shift
                ;;
            --help|-h)
                BUILD_HELP=true
                shift
                ;;
            *)
                log_error "Unknown option: $1"
                return 1
                ;;
        esac
    done
}

# Show standard help message
show_build_help() {
    local plugin_name="$1"
    local default_deploy_path="${2:-}"

    echo -e "${GREEN}🔨 ${plugin_name} Build Script${NC}"
    echo ""
    echo -e "${CYAN}USAGE:${NC}"
    echo -e "  ${WHITE}./build.sh [Configuration] [Options]${NC}"
    echo ""
    echo -e "${CYAN}CONFIGURATIONS:${NC}"
    echo -e "  ${WHITE}Debug                 Debug build with symbols (default)${NC}"
    echo -e "  ${WHITE}Release               Optimized release build${NC}"
    echo ""
    echo -e "${CYAN}OPTIONS:${NC}"
    echo -e "  ${WHITE}--deploy              Auto-deploy to test Lidarr instance${NC}"
    echo -e "  ${WHITE}--deploy-path <path>  Custom deployment path${NC}"
    echo -e "  ${WHITE}--clean               Clean before building${NC}"
    echo -e "  ${WHITE}--restore             Force restore packages${NC}"
    echo -e "  ${WHITE}--no-build            Skip build (for clean/restore only)${NC}"
    echo -e "  ${WHITE}--verbose             Show detailed build output${NC}"
    echo -e "  ${WHITE}--help                Show this help${NC}"
    echo ""
    echo -e "${CYAN}EXAMPLES:${NC}"
    echo -e "  ${GRAY}./build.sh                          # Debug build${NC}"
    echo -e "  ${GRAY}./build.sh Release                  # Release build${NC}"
    echo -e "  ${GRAY}./build.sh --deploy                 # Debug build + auto-deploy${NC}"
    echo -e "  ${GRAY}./build.sh Release --deploy         # Release build + deploy${NC}"
    echo -e "  ${GRAY}./build.sh --clean --restore        # Clean, restore, and build${NC}"
    echo ""
    if [[ -n "$default_deploy_path" ]]; then
        echo -e "${CYAN}DEFAULT DEPLOY PATH:${NC}"
        echo -e "  ${GRAY}$default_deploy_path${NC}"
        echo ""
    fi
}

log_info "Loaded Lidarr.Plugin.Common build library"
