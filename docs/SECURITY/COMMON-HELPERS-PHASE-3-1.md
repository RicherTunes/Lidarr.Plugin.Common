# Phase 3.1 — Common Helper APIs

Three helpers added to unblock deferred applemusicarr findings APL-006, APL-009, and APL-010.

## APL-006 — `SecureMemory` (src/Security/SecureMemory.cs)

**Problem:** ECDsa / PEM key material remained on the managed heap indefinitely after use.

**Solution:** `public static class SecureMemory`

| Method | Signature | Notes |
|--------|-----------|-------|
| `ZeroBytes` | `static void ZeroBytes(Span<byte> buffer)` | Deterministic clear; delegates to `Span<T>.Clear()`. |
| `ZeroPemKey` | `static void ZeroPemKey(ref string? pem)` | Best-effort overwrite via `MemoryMarshal.CreateSpan` + `MemoryMarshal.AsBytes`, then nulls the reference. |

### String-zeroing caveats (documented in XML doc and test file)

- Works only for **runtime-allocated, non-interned** strings (PEM loaded from file/network qualifies).
- The GC may have relocated the string before `ZeroPemKey` is called — the overwrite targets the *current* location only; prior copies at old addresses are not zeroed.
- For a cryptographic-strength guarantee, prefer a pinned `byte[]` (`GCHandle.Alloc(GCHandleType.Pinned)`) and call `ZeroBytes`.
- `AllowUnsafeBlocks=false` in the project — implementation uses `MemoryMarshal` instead of `unsafe` pointer arithmetic.

---

## APL-009 — `UniversalAdaptiveRateLimiter.WithConservativeDefaults()` (src/Services/Performance/UniversalAdaptiveRateLimiter.cs)

**Problem:** The existing limiter had aggressive defaults (200 RPM+) unsuitable for Apple Music's strict quota.

**Solution:** Added `WithConservativeDefaults()` static factory that returns a limiter configured with:

| Parameter | Value |
|-----------|-------|
| Initial RPM | 60 (~1 req/s) |
| Minimum RPM | 2 (~30 s gap floor, limiter never fully stalls) |
| Maximum RPM | 60 (no auto-expansion beyond initial cap) |
| Circuit-open threshold | 3 consecutive non-auth errors (default profile: 5) |

Existing `new UniversalAdaptiveRateLimiter()` behaviour is unchanged (back-compat).
Auth-failure neutrality (APL-009-adjacent) applies to both profiles.

---

## APL-010 — `PagedResponseValidator` + `PagedResponseIntegrityException`

**Files:**
- `src/Services/Http/PagedResponseValidator.cs`
- `src/Errors/PagedResponseIntegrityException.cs`

**Problem:** Plugin paged-API consumers accumulated items without verifying `sum(page.items) == response.totalCount`, allowing silent truncation.

**Solution:**

```csharp
PagedResponseValidator.Validate(
    receivedItemCount: allItems.Count,
    declaredTotalCount: firstPage.TotalCount,   // pass null if API doesn't declare
    contextName: "apple-music-albums");
```

Throws `PagedResponseIntegrityException` (with `ReceivedItemCount`, `DeclaredTotalCount`, `ContextName` properties and a diagnostic message containing all three) on mismatch. No-ops when `declaredTotalCount` is `null`.

---

## Tests added

| File | Tests |
|------|-------|
| `tests/Security/SecureMemoryTests.cs` | 9 tests — ZeroBytes (5), ZeroPemKey (4) |
| `tests/Services/Http/PagedResponseValidatorTests.cs` | 12 tests — happy paths (3), mismatch assertions (5), exception properties (2), edge cases (2) |
| `tests/Services/Performance/UniversalAdaptiveRateLimiterConservativeTests.cs` | 10 tests — back-compat (3), conservative preset (7) |

**Total: 32 new tests. All GREEN.**

Full suite: 2920 passed, 1 skipped (PluginLoads — no sample DLL on this machine), 1 pre-existing failure (PackageClosure/brainarr — external repo issue, unrelated to this phase).
