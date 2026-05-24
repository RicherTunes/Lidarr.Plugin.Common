# Security Hardening Overview — Lidarr Plugin Ecosystem

_Generated: 2026-05-23 | Phase 1 cross-repo security audit_
_Repos audited: lidarr.plugin.common · brainarr · qobuzarr · tidalarr · applemusicarr_

---

## Executive Summary

The five-repo Lidarr plugin ecosystem has strong foundational security infrastructure in Common
(encrypted token store, adaptive rate limiter, path sanitization, URL redaction). The critical
gaps are concentrated in **per-user secrets that remain plaintext in Lidarr's settings JSON**
and **sync-over-async debt that is partially mitigated but not yet fully closed**. No live
hardcoded secrets were found that are not already covered by an ADR. One Critical finding
(APL-001/APL-002) warrants immediate prioritisation: the Apple Music developer private key and
user bearer token are stored unencrypted.

---

## Urgent Items

> No live API secrets with immediate exploitation risk were found. The single URGENT item is a
> design-level issue that requires a fix before a public release of applemusicarr.

**APL-001/APL-002 — Apple Music private key stored in plaintext (applemusicarr)**
The MusicKit PKCS#8 private key (`PrivateKey`) and per-user `MusicUserToken` are stored as
raw strings in Lidarr's settings bag, serialised to disk as plaintext JSON. Anyone with
filesystem access to the Lidarr config directory can extract the private key and sign arbitrary
developer tokens against the operator's Apple Developer team. This is the only Critical-severity
finding in the audit and should be resolved before applemusicarr reaches a public release.
**File:** `src/AppleMusicarr.Plugin/Configuration/AppleMusicPluginSettings.cs:14,16`

---

## Top 10 Cross-Cutting Findings

Ranked by (severity × breadth). `Breadth` = number of repos affected.

| Rank | ID(s) | Finding | Severity | Breadth |
|------|-------|---------|----------|---------|
| 1 | APL-001, APL-002 | Apple Music private key + user token stored plaintext in settings JSON | Critical | 1 (applemusicarr) |
| 2 | BRN-001 | All 8 LLM provider API keys stored plaintext in settings JSON; TODO comment documents debt | High | 1 (brainarr) |
| 3 | QOB-003 | `AppSecret` field has `FieldType.Textbox` (not Password); visible in UI and stored plaintext | High | 1 (qobuzarr) |
| 4 | COM-005 | `Sanitize.IsSafePath` bypassed by un-normalised paths; affects every plugin that calls it | High | 5 (all) |
| 5 | COM-011 | Download integrity: no final byte-count verification after partial resume; affects all downloading plugins | High | 3 (qobuzarr, tidalarr, applemusicarr) |
| 6 | QOB-001 | Three sync-over-async calls inside `lock {}` in `QobuzAuthenticationService`; deadlock risk on ASP.NET context | High | 1 (qobuzarr) |
| 7 | TID-007 | No integrity check of assembled+decrypted audio stream (TidalChunkDownloader) | High | 1 (tidalarr) |
| 8 | TID-001, QOB-001, APL-003 | Sync-over-async in Category A sync contracts without thread-pool context hop | Medium | 3 |
| 9 | BRN-002, QOB-007, APL-008 | PII / sensitive values in debug logs (email, user ID, API keys in exception messages) | Medium | 4 (all except Common) |
| 10 | TID-009 | Dual rate-limiter stack in tidalarr (plugin-local + Common) — double-throttle risk | Medium | 1 (tidalarr) |

---

## Already-Addressed Items (credit)

| Item | Description | Status |
|------|-------------|--------|
| Token encryption at rest | `FileTokenStore` v2 format uses `DataProtectionTokenProtector` / DPAPI / Keychain / SecretService | **Done in Common** |
| Legacy plaintext token migration | `FileTokenStore.TryMigrateToProtectedFormat` migrates old v1 files on first load | **Done in Common** |
| URL redaction in logs | `Sanitize.RedactUrls` + `SafeErrorMessage` used in Common HTTP pipeline | **Done in Common** |
| Path traversal guard (basic) | `Sanitize.IsSafePath` + `PathSegment` in Common | **Partial — see COM-005/COM-006** |
| Retry with exponential backoff + jitter | `ExponentialBackoffRetryPolicy` with `Random.Shared` (Wave 56 fix) | **Done in Common** |
| Auth failure isolation from rate limiter | `AuthFailureGate` + `UniversalAdaptiveRateLimiter.RecordAuthFailure` | **Done in Common** |
| Hardcoded Tidal client credentials ADR | `tidalarr/docs/decisions/0001-hardcoded-tidal-client-credentials.md` | **Done in tidalarr** |
| Qobuz sync-over-async documentation | `docs/SYNC_ASYNC_DEBT.md` + `TODO(phase-1.1)` comments | **Documented; not yet fixed** |
| Windows file permissions on token file | `File.SetUnixFileMode` for Unix; no Windows equivalent (see COM-001) | **Partial** |
| Rate-limiter auth-failure isolation | Auth 401/403 no longer tightens rate-limit budget (Wave N fix) | **Done in Common** |

---

## Recommended Remediation Order

### Sprint 1 — Critical / must-fix before any public release
1. **APL-001 + APL-002** — Encrypt `PrivateKey` + `MusicUserToken` in applemusicarr via Common
   `TokenProtectorFactory`. Blocked on: nothing.
2. **COM-005** — Harden `Sanitize.IsSafePath` to normalise paths before `..` check. All plugins
   consume this method — fix in Common first, then rebuild all plugins.

### Sprint 2 — High / fix before next stable release
3. **BRN-001** — Encrypt LLM API keys via `TokenProtectorFactory`. Unblocked after Common v2
   ships `TokenProtectorFactory` stable API (already available).
4. **QOB-003** — Change `AppSecret` field type to `Password`; add `TokenProtectorFactory` storage.
5. **COM-011** — Add byte-count verification in `HttpFileDownloadService` post-download.
6. **QOB-001** — Convert `IQobuzAuthenticationService` to async; remove `lock {}` + `GetAwaiter().GetResult()` pattern.
7. **TID-007** — Add integrity check in `TidalChunkDownloader` after assembly+decryption.

### Sprint 3 — Medium / harden before broad deployment
8. **TID-001, APL-003** — Add `Task.Run` context hops for all Category A sync-over-async calls.
9. **COM-001** — Set Windows DACL on token files to owner-only after atomic rename.
10. **TID-009** — Retire plugin-local `TidalRateLimiter`; delegate entirely to Common's `UniversalAdaptiveRateLimiter`.
11. **BRN-002, QOB-007, APL-008** — Route all log statements through `Sanitize.SafeErrorMessage`; hash/truncate PII fields.
12. **COM-009** — Namespace `UniversalAdaptiveRateLimiter` keys by plugin name to prevent cross-plugin state leak.
13. **QOB-004** — Write Qobuz app-ID ADR (parallel to tidalarr's ADR-0001).

### Sprint 4 — Low / polish
14. COM-002, COM-003, COM-006, COM-007, COM-008, COM-010, COM-012
15. BRN-005, BRN-006, BRN-007, BRN-008, BRN-009, BRN-010
16. QOB-005, QOB-006, QOB-008, QOB-009, QOB-010, QOB-011
17. TID-002, TID-003, TID-004, TID-005, TID-006, TID-008, TID-010
18. APL-004, APL-005, APL-006, APL-007, APL-009, APL-010, APL-011

---

## Cross-Plugin DRY Opportunities

### 1. Encrypted settings storage for API keys (brainarr + applemusicarr)
Both brainarr (LLM API keys) and applemusicarr (Apple private key + user token) need to encrypt
string values in the Lidarr settings bag. Neither uses the Common `FileTokenStore` /
`DataProtectionTokenProtector` pipeline today. A thin **`EncryptedSettingsProperty<T>`** wrapper
built on `TokenProtectorFactory` in Common would give both plugins a single line-of-code fix.
**Proposal:** Add `Common.Security.EncryptedSettingsProperty` helper in Common Sprint 1.

### 2. Category A sync-over-async context hop (qobuzarr + tidalarr + applemusicarr)
All three downloading plugins independently implement the same pattern:
`Task.Run(() => asyncMethod()).GetAwaiter().GetResult()` (or omit the `Task.Run`, which is the bug).
**Proposal:** Add a `Common.Utilities.SyncBridge.RunSync<T>(Func<Task<T>>)` helper that
centralises the `Task.Run` hop, mirrors .NET's `AsyncHelper` pattern, and documents the
Category A rationale in one place. qobuzarr already has `SafeAsyncHelper`; promote it to Common.

### 3. Per-plugin rate-limiter scoping (all five plugins)
`UniversalAdaptiveRateLimiter` is a shared singleton. If two plugins in the same Lidarr process
both call `WaitIfNeededAsync("Default", ...)` they share budgets. tidalarr already registers as
`"Tidal"`, qobuzarr as `"Qobuz"`, applemusicarr as `"AppleMusic"`, but any plugin using the
`"Default"` key will collide.
**Proposal:** Enforce a convention: every `WaitIfNeededAsync` call must use a plugin-namespaced
key. Lint rule or assertion in `Common` to reject the raw `"Default"` key from plugin code.

### 4. Post-download integrity check (Common extension)
Both tidalarr and qobuzarr perform file downloads and neither verifies byte-count or hash after
completion. `HttpFileDownloadService` in Common (COM-011) is the right place for a shared
`DownloadAndVerifyAsync` method that optionally accepts an expected SHA-256 or expected byte count.

### 5. Qobuz app-ID ADR (qobuzarr — mirrors tidalarr's ADR-0001)
`QobuzConstants.Api.DefaultAppId = "798273057"` is a public constant extracted from the Qobuz
web player. It needs the same ADR treatment as Tidal's `CLIENT_ID_PKCE`.
**Action:** Copy `tidalarr/docs/decisions/0001-hardcoded-tidal-client-credentials.md` as a
template; create `qobuzarr/docs/decisions/0001-hardcoded-qobuz-app-id.md`.

---

## Dependencies / Sequencing Notes

- **APL-001 fix** depends on: nothing external. All required Common infrastructure
  (`TokenProtectorFactory`, `DataProtectionTokenProtector`) is already shipped.
- **BRN-001 fix** depends on: same Common infrastructure; already available.
- **QOB-001 fix** (async IQobuzAuthenticationService) is a **breaking interface change**:
  all 3+ callers must be updated simultaneously. Co-ordinate with any in-flight PRs.
- **COM-005 fix** (path normalisation) changes the return value of `Sanitize.IsSafePath` for
  some inputs — callers that currently pass un-normalised paths will start getting `false`.
  Requires audit of all call sites before merging. Recommended approach: add a new
  `IsSafePathNormalized(string, string root)` overload; deprecate the old one.
- **COM-009 fix** (rate-limiter key namespacing) is a **behavioural change** for any plugin
  using `"Default"`. Audit call sites in all five repos before merging.
- **TID-009** (retire plugin-local rate limiter) must be co-ordinated with any pending
  tidalarr rate-limiting PRs to avoid merge conflicts.

---

## Finding Count Summary

| Repo | Critical | High | Medium | Low | Total |
|------|----------|------|--------|-----|-------|
| lidarr.plugin.common | 0 | 2 | 7 | 3 | 12 |
| brainarr | 0 | 2 | 5 | 3 | 10 |
| qobuzarr | 0 | 2 | 7 | 2 | 11 |
| tidalarr | 0 | 2 | 6 | 2 | 10 |
| applemusicarr | 2 | 1 | 6 | 2 | 11 |
| **TOTAL** | **2** | **9** | **31** | **12** | **54** |

_Note: findings in `ext/` (vendored copies of Common) are intentionally excluded — they mirror
the canonical Common source and will be resolved when the Common fix is pulled._
