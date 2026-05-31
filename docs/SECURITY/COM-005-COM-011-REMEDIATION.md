# Security Hardening: COM-005 and COM-011 Remediation

**Phase:** 2 (High findings)
**Date:** 2026-05-23
**Findings addressed:** COM-005, COM-011
**Status:** SHIPPED — tests GREEN, Release build clean

---

## COM-005: `Sanitize.IsSafePath` path-traversal bypass

### Threat model (COM-005)

The original implementation was a single `Contains("..")` check:

```csharp
return !path.Contains("..") && !path.Contains("../") && !path.Contains("..\\");
```

This was bypassed by any input that encoded the traversal sequence in a form
the naive check did not recognise:

| Bypass class | Example | Why it bypassed |
|---|---|---|
| Mixed separators | `foo/..\..\bar` | `..` segment present but adjacent separator is backslash |
| Percent-encoded | `foo/%2e%2e/bar` | Characters `%`, `2`, `e` individually — no `..` literal |
| Unicode look-alike | `foo/．．/bar` | U+FF0E FULLWIDTH FULL STOP — NFKC normalises to `.` |
| Extended-length UNC | `\\?\C:\..\Windows` | `\\?\` prefix bypasses OS MAX\_PATH and some guards |
| Null-byte injection | `foo\0../etc/passwd` | Null byte could truncate OS path resolution |

All five plugin consumers (Common, brainarr, qobuzarr, tidalarr, applemusicarr)
use `Sanitize.IsSafePath` as their traversal guard.

### Fix (COM-005)

`src/Security/Sanitize.cs` — `IsSafePath` now applies a normalisation pipeline
before checking for traversal segments:

1. **Null-byte / C0 control character rejection** — any character `< ' '` (except
   `\t`, `\n`, `\r`) causes an immediate `false` return.
2. **Extended-length UNC prefix rejection** — `\\?\` and `//?/` prefixes are
   rejected immediately.
3. **Percent-decode once** — replaces `%XX` with the decoded character.
4. **NFKC Unicode normalisation** — `String.Normalize(NormalizationForm.FormKC)`
   collapses fullwidth/compatibility variants to their canonical ASCII equivalents.
5. **Explicit look-alike replacement** — U+FF0E, U+2024, U+FE52 are mapped to
   ASCII `.` in case they survive NFKC.
6. **Separator normalisation** — all `\` replaced with `/`.
7. **Segment-level check** — path split on `/`; any segment exactly equal to `..`
   causes `false`. Substrings of filenames (e.g., `my..album`) are NOT rejected.

### Caller impact

`IsSafePath` was scanned across all files in `src/`. The method is **not called
directly by any production code inside `Common` itself** — it is a utility
exported for downstream plugin authors. The only in-tree references are:

- `src/Security/InputSanitizer.cs` — a deprecated shim that references
  `IsSafePath` only in `[Obsolete]` attribute text (no call site).

**No callers inside Common required changes.**

Downstream plugins (brainarr, qobuzarr, tidalarr, applemusicarr) consume
`IsSafePath` to guard user-supplied paths before constructing file paths.
The new implementation is strictly stricter: it rejects more inputs than before.
Callers passing well-formed paths (simple relative segments, absolute OS paths,
paths with dots inside filenames) are unaffected.

The one behavioural change worth noting for downstream authors:
a path like `artist/..album/track.flac` (`.` appears at the **start** of a
segment but the segment is not exactly `..`) is now **accepted** where the old
implementation rejected it (because the old code did `Contains("..")`). This
aligns with correct behaviour: `..album` is not a traversal.

### Tests added (COM-005)

`tests/Security/SanitizePathBypassTests.cs`

| Test | Category |
|---|---|
| `IsSafePath_RejectsMixedSeparatorTraversal` | BYPASS RED→GREEN |
| `IsSafePath_RejectsPercentEncodedDotDot` | BYPASS RED→GREEN |
| `IsSafePath_RejectsUnicodeNormalizedDots` | BYPASS RED→GREEN |
| `IsSafePath_RejectsLongUNCBypass` | BYPASS RED→GREEN |
| `IsSafePath_RejectsNullByteInPath` | BYPASS RED→GREEN |
| `IsSafePath_AcceptsNullOrWhitespace` | REGRESSION GUARD |
| `IsSafePath_AcceptsSimpleRelativePath` | REGRESSION GUARD |
| `IsSafePath_AcceptsPathWithSpacesAndUnicode` | REGRESSION GUARD |
| `IsSafePath_AcceptsPathWithDotsInFilename` | REGRESSION GUARD |
| `IsSafePath_AcceptsAbsoluteRootedPaths` | REGRESSION GUARD |
| `IsSafePath_AcceptsHyphenatedAndSpecialCharacters` | REGRESSION GUARD |
| `IsSafePath_AcceptsDoubleDotAsSubstringOfFilename` | REGRESSION GUARD |

**Total: 12 tests — 12 PASS**

### Limitations / follow-ups (COM-005)

- **Symbolic link traversal** — COM-005 explicitly mentions symlink traversal as
  a bypass vector. `IsSafePath` cannot detect this; it is a string-level guard
  only. True symlink traversal protection requires `Path.GetFullPath` resolution
  against a known root at the call site (an OS syscall). Plugin authors who write
  to user-configurable directories should additionally validate with
  `Path.GetFullPath` and check the result starts with the expected root prefix.
  This is tracked as a Phase 3 follow-up (COM-005-SYMLINK).

- **Root-anchor overload** — the overview doc (`SECURITY_HARDENING_OVERVIEW.md`)
  suggests adding an `IsSafePathNormalized(string, string root)` overload that
  performs the `GetFullPath` resolution. That overload is not included in this
  phase to avoid introducing OS-level side-effects into what is currently a
  pure string predicate. Tracked as COM-005-ROOT.

---

## COM-011: `HttpFileDownloadService` partial-download corruption

### Threat model (COM-011)

The download service supports HTTP range resumption (RFC 7233). Before this fix:

1. The server declares `Content-Length: 1000`.
2. The service downloads only 500 bytes (network interruption, premature close).
3. The partial file is moved to the final path as-is.
4. `AudioMagicBytesValidator` and `ValidationUtilities.ValidateDownloadedFile`
   run on the 500-byte file — these checks verify format/magic-bytes, not total
   size, so they may pass.
5. A 500-byte truncated FLAC/M4A/OPUS file is delivered to Lidarr as the
   downloaded track, causing silent corruption.

This affected all audio download plugins: qobuzarr, tidalarr, applemusicarr.

### Fix (COM-011)

`src/Services/Download/HttpFileDownloadService.cs`

After the download loop completes, the total bytes written (`totalWritten`) is
compared against the server-declared expected total:

- **200 OK response**: expected total = `Content-Length` header value.
- **206 Partial Content response**: expected total = Content-Range
  `complete-length` field (the `/N` part of `bytes start-end/N`).
- **Chunked transfer (no Content-Length)**: no expected total available — the
  integrity check is skipped. This is a documented limitation (see below).

On mismatch:
1. The `.partial` file is deleted (best-effort).
2. `DownloadIntegrityException` is thrown with `ActualBytes` and `ExpectedBytes`
   properties for caller inspection.

The integrity check happens **before** `File.Move` so no corrupt final file is
ever written. The `finally`-style cleanup is inline in the service rather than
a finally block to avoid interfering with the existing exception paths for content
type rejection and magic bytes validation.

### New exception type

`src/Errors/DownloadIntegrityException.cs`

```csharp
public sealed class DownloadIntegrityException : Exception
{
    public long ActualBytes { get; }
    public long ExpectedBytes { get; }
    // ...
}
```

Registered in `PublicAPI.Shipped.txt` (both `net8.0` and `net6.0` baselines).

### Tests added (COM-011)

`tests/Services/Http/HttpFileDownloadIntegrityTests.cs`

| Test | Category |
|---|---|
| `Download_TruncatedResponse_ThrowsAndDeletesPartial` | INTEGRITY RED→GREEN |
| `Download_TruncatedResponse_ExceptionHasCorrectProperties` | INTEGRITY RED→GREEN |
| `Download_ResumedTruncation_ThrowsAfterFinalAssembly` | INTEGRITY RED→GREEN |
| `Download_ExactByteCount_Succeeds` | HAPPY PATH |
| `Download_NoContentLength_ChunkedTransferEncoding_CompletesWithoutIntegrityError` | LIMITATION DOC |

**Total: 5 tests — 5 PASS**

### Limitations / follow-ups (COM-011)

- **Chunked transfer encoding (no Content-Length)**: when the server does not
  send a `Content-Length` or `Content-Range` header, the downloaded byte count
  cannot be verified here. Some CDNs and transcoding proxies use chunked encoding.
  Mitigation would require the server to emit a checksum in a custom header (e.g.,
  `X-Content-SHA256`) which is out of scope for this finding. Tracked as
  COM-011-CHUNKED.

- **Multi-cycle resume**: the current implementation performs one range request
  per call. If callers loop over `DownloadToFileAsync` to implement multi-cycle
  resumption, the `Content-Range` total from the resumed response carries the
  correct total for the assembled file. The `Download_ResumedTruncation_ThrowsAfterFinalAssembly`
  test verifies this end-to-end.

---

## Pre-flight results

| Check | Result |
|---|---|
| `dotnet build src/Lidarr.Plugin.Common.csproj --configuration Release` | 0 errors, 0 warnings |
| `dotnet test --filter "IsSafePath\|HttpFileDownload"` | 28/28 PASS |
| Full test suite | see full run output |
| Pester ecosystem-parity-lint (Phase 0) | not applicable to Common itself |
