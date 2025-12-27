# Ecosystem Parity Roadmap

This document tracks progress toward full structural and behavioral parity across the plugin ecosystem (Tidalarr, Qobuzarr, Brainarr).

## Current Status

| Dimension | Tidalarr | Qobuzarr | Brainarr | Common |
|-----------|----------|----------|----------|--------|
| **Packaging** | 100% | 100% | 100% | Policy complete |
| **Naming/Path** | 90% | 95% | N/A | FileSystemUtilities |
| **Concurrency** | 95% | 100% | N/A | BaseDownloadOrchestrator |
| **E2E Gates** | Pending | Proven | Pending | Harness ready |

**Overall Ecosystem Parity: ~93-95%**

---

## Definition of Done

Full ecosystem parity is achieved when:

- [ ] All three plugins ship the 5-DLL type-identity contract
- [ ] Both streaming plugins produce identical filename format on multi-disc and edge sanitization
- [ ] Persistent single-plugin E2E gates pass for Qobuzarr and Tidalarr
- [ ] Multi-plugin schema gate passes for 2 plugins, then 3 plugins (when host supports)

**No-Drift Rule**: Any new filename/path logic must either live in Common or delegate to Common.

---

## Phase 1: Lock Contracts with Tests (High Priority)

### 1.1 Packaging Content Tests
- [x] **Common**: PluginPackageValidator with TypeIdentityAssemblies
- [x] **Tidalarr**: PackagingPolicyBaseline updated to 5-DLL contract
- [x] **Qobuzarr**: PackagingPolicyTests with RequiredTypeIdentityAssemblies
- [x] **Brainarr**: BrainarrPackagingPolicyTests with 5-DLL contract

### 1.2 Naming Contract Tests
- [ ] Multi-disc: D01Txx/D02Txx format validation
- [ ] Extension normalization: `.flac` and `flac` both produce `.flac`
- [ ] Unicode normalization: NFC form consistency

### 1.3 Common SHA Verification
- [x] Tidalarr: Uses ext-common-sha.txt
- [x] Qobuzarr: Uses ext-common-sha.txt
- [x] Brainarr: Uses ext-common-sha.txt (fixed format)

---

## Phase 2: Reduce Code Drift (Tech Debt)

### 2.1 Tidalarr Sanitization Consolidation
**Status**: In Progress

Current duplicate paths:
- `TidalDownloadClient.cs:99-100` - FileNameSanitizer for title/artist
- `TidalDownloadClient.cs:405` - FileNameSanitizer for temp file path

**Action**: Route through Common FileSystemUtilities where affecting output filenames.

### 2.2 Qobuzarr Filename Builder Consolidation
**Status**: Complete

- [x] `TrackFileNameBuilder` now delegates to `FileSystemUtilities.CreateTrackFileName`
- [x] Single source of truth for naming/sanitization

### 2.3 Common Internal Audit
**Status**: Pending

Check for direct filename building outside FileSystemUtilities:
- [ ] StreamingPluginMixins
- [ ] DataValidationService
- [ ] Any other helpers creating paths

### 2.4 TidalChunkDownloader Delay Configuration
**Status**: Pending

Current: Fixed `Task.Delay(50)` per chunk (line 49)

Options:
1. Set delay to 0 and rely on rate-limit handlers
2. Make configurable via advanced setting

---

## Phase 3: Persistent E2E Gates

### 3.1 Single-Plugin E2E Runner
**Location**: `lidarr.plugin.common/scripts/`

Gates:
1. **Schema Gate** (no credentials): Indexer/DownloadClient discovered and configured
2. **Search Gate** (credentials required): API search returns results
3. **Grab Gate** (credentials required): Download initiated and completes

### 3.2 Plugin-Specific Status
| Plugin | Schema | Search | Grab |
|--------|--------|--------|------|
| Qobuzarr | Proven | Proven | Proven |
| Tidalarr | Pending | Pending | Pending (OAuth stability) |
| Brainarr | Pending | N/A | N/A |

### 3.3 Multi-Plugin E2E
**Blocked by**: Upstream Lidarr ALC fix for multi-plugin isolation

Design:
- Start Lidarr with persisted /config, /downloads, /music
- Deploy all plugin zips
- Run gates sequentially per plugin
- On failure: diagnostics bundle + run manifest JSON

---

## Phase 4: Multi-Plugin Proof (Future)

Once upstream Lidarr ALC fix lands:
1. Enable multi-plugin schema gate as hard pass/fail
2. Enable search/grab gates with secrets
3. Prove 3-plugin concurrent operation

---

## Quick Reference: File Locations

### Packaging Tests
- `tidalarr/tests/Tidalarr.Tests/Unit/Packaging/PackagingPolicyBaseline.cs`
- `qobuzarr/tests/Qobuzarr.Tests/Compliance/PackagingPolicyTests.cs`
- `brainarr/Brainarr.Tests/Packaging/BrainarrPackagingPolicyTests.cs`
- `lidarr.plugin.common/tests/PackageValidation/PluginPackageValidator.cs`

### Filename Utilities
- `lidarr.plugin.common/src/Utilities/FileSystemUtilities.cs`
- `qobuzarr/src/Utilities/TrackFileNameBuilder.cs` (delegates to Common)
- `tidalarr/src/Tidalarr/Integration/TidalDownloadClient.cs` (needs consolidation)

### E2E Scripts
- `lidarr.plugin.common/scripts/local-e2e-single.ps1`
- `lidarr.plugin.common/scripts/local-e2e-multi.ps1` (future)

---

## Changelog

| Date | Change |
|------|--------|
| 2025-12-27 | Common PRs #167/#168 merged - packaging policy complete |
| 2025-12-27 | Tidalarr packaging baseline updated to 5-DLL contract |
| 2025-12-27 | Qobuzarr TrackFileNameBuilder delegated to Common |
| 2025-12-27 | Initial roadmap created |
