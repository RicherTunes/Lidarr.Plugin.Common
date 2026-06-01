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
1. ✅ Adversarial review of the perf refactor — folded: cbcs edge vectors (trailing partial, (1,9), 3-group chaining), single-threaded contract doc, disposed-instance test. (commit 81148d8)
2. ✅ MP4 `tenc`/`senc` parser (`CencBoxParser`) → per-sample IVs + subsample maps + default pattern/constant-IV. 5 tests, bounds-checked. (commit 7b429c1)
3. ✅ Canonical `PsshParser` (v0 + v1 KID list), supersedes amazon's buggy in-tree parser. (commit 7e7f6ab)
4. ✅ Adversarial review of box + PSSH parsers (2 lenses) → folded: senc zero-progress DoS guard, long-math
   bounds (int-wrap bypass), pssh KID_count pre-reject + DataSize>Int32 guard, edge coverage. (commit fb8d9ad)
5. ✅ MP4 box walker (`Mp4BoxWalker`) — ReadBoxes + recursive FindFirst with stsd/sample-entry descent to
   reach tenc; hardened (depth cap vs StackOverflow DoS, largesize overflow). 9 tests. (commits 0a188fc, 87595a2)
6. ✅ `trun` parser (`CencBoxParser.ParseTrun`) — per-sample sizes + data_offset to slice samples from mdat,
   DoS-guarded. (commit 635ab42)
7. ⏳ End-to-end `CencSegmentDecryptor`: box walker → ParseTrun (sample byte ranges in mdat) + ParseSenc
   (per-sample IV + subsamples) + tenc defaults (scheme/IV-size/pattern from init) → CencDecryptor per sample,
   given a content key (key is an INPUT — testable with a synthetic encrypted segment + known key, NO CDM).
   Then `FindAll` (multi-DRM pssh Widevine filter).

## All deterministic CENC primitives are now BUILT (CencDecryptor, tenc/senc/trun parser, PsshParser,
## Mp4BoxWalker). The remaining piece is the CDM license layer — ⚠️ ARCHITECTURAL BLOCKER:
## a real Widevine CDM needs provisioned device creds (.wvd / client-id blob + device RSA key), SEPARATE
## from the ADP token the plugin mints. SURFACE this to the user before building the CDM/wiring into amazon.
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
