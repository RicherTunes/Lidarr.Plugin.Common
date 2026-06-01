# DRM/CDM Hardening Progress

Autonomous TDD effort: real Widevine/FairPlay CDM via shared CENC crypto in Common, adopted by
amazonmusicarr (Widevine) and applemusicarr (FairPlay cbcs). Priority: correctness/security > dedup > perf > polish.

## Shipped (Common, branch `feat/drm-cenc-sample-decryptor`, PR #601)
- ✅ `CencSampleDecryptor` / `CencDecryptor` — AES-128 CTR ('cenc') + CBC ('cbcs') sample decryption.
  - CENC subsample semantics (continuous CTR keystream, clear-byte skip).
  - cbcs crypt:skip striping with CBC chaining across skipped blocks.
  - Full input validation (key=16, iv 8/16 cenc / 16 cbcs, scheme, subsample bounds/negative/overflow/OOM).
  - Key-copy zeroization.
  - Stateful per-track instance (AES key schedule once) + in-place/span API; static = thin wrapper.
  - 23 tests, NIST SP 800-38A F.2/F.5 vectors + chained-CBC pattern vector. 3 adversarial reviews folded in.

## Next (ordered)
1. ⏳ Adversarial review of the perf refactor (in-place CBC correctness, instance reuse/thread-safety, perf) — fold findings as tests.
2. MP4 `tenc`/`senc`/`saio`/`saiz` parser in Common → per-sample IVs + subsample maps + default pattern. TDD with synthetic boxes.
3. Correct `WidevinePsshParser` (v1/KID handling) — promote to Common.
4. Widevine license protobuf (SignedLicenseRequest/SignedLicense) + CDM session-key derivation + content-key unwrap.
   - ⚠️ BLOCKED/SURFACE: a real CDM needs provisioned Widevine device creds (`.wvd` / client-id blob + device RSA key),
     SEPARATE from the ADP token the plugin mints. Architectural — surface to the user before wiring.
5. Wire `CencDecryptor` into amazon `WidevineSegmentDecryptor`/`WidevineDecryptor`; delete dead crypto
   (OptimizedHttpClient, SafeArray, unused CryptoUtilities); collapse duplicate license HTTP path into the API client.
6. Apple: adopt Common `DrmTrack`/`IExternalDownloadHandler` seam; FairPlay cbcs via `CencDecryptor`; fix
   path-traversal/SSRF/threading bugs (apple adversarial review).
7. Converge Common pins: amazon `f2f84cb` / apple `80a0eb5` → `origin/main`; update `ext-common-sha.txt` + gitlink.

## Landing
- Common CENC work → PR #601 (open, not auto-merged).
- Amazon round-5 + fixes → already in open PR #2 (`ci/full-workflow-parity`).

## Guardrails active
TDD always; independent crypto vectors; consolidate to Common; explicit-path staging; never `git add -A`;
never commit `artifacts/` or phantom EOL drift; build-on-top of the parallel AI (retry-with-wait on file locks).
