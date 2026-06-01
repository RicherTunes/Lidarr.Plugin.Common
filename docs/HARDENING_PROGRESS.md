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
7. ✅ End-to-end `CencSegmentDecryptor` (box walker → trun/senc → CencDecryptor per sample). Proven against a
   NIST-CTR synthetic segment; hardened (size>Int32 guard + multi-sample/malicious tests). (5cb43f6, ae362f2)
8. ✅ `tfhd` parser + wired into segment decryptor: (a) data_offset anchor (base_data_offset vs
   default-base-is-moof), (b) default_sample_size fallback when trun omits sizes. (commits 3777f0f, 4ae57b9)
9. ✅ cbcs end-to-end coverage (whole-sample + clear-header subsample, NIST CBC). (commit b516067)
10. ✅ `Mp4BoxWalker.FindAll` for multi-DRM pssh selection. (commit c6a5fb5)

**MILESTONE:** the deterministic CENC pipeline is COMPLETE + tested (~72 tests, PR #601):
cenc + cbcs, whole-sample + subsamples + cbcs pattern, tenc/senc/trun/tfhd parse, pssh parse, box
walk (FindFirst/FindAll), end-to-end segment decrypt (anchor + default_sample_size), all hardened via
adversarial review and conformance-validated (NIST AES vectors + pywidevine PSSH). Decryption works given a content key.

**NEXT-PHASE DECISION (surfaced to user):**
CDM license layer = the content-key producer = BLOCKED on Widevine device creds (.wvd). ARCHITECTURAL.
Non-blocked alternatives: apple's security bugs (SSRF, path-traversal guard, threading torn-reads);
traf-scoping/multi-trun (low value for single-audio Amazon).

**ARCHITECTURAL BLOCKER:** all deterministic CENC primitives are BUILT; the remaining piece is the CDM
license layer. A real Widevine CDM needs provisioned device creds (.wvd / client-id blob + device RSA key),
SEPARATE from the ADP token the plugin mints. Surface to the user before building the CDM / wiring into amazon.
## Remaining roadmap

- CDM license layer: Widevine license protobuf (SignedLicenseRequest/SignedLicense) + session-key
  derivation + content-key unwrap. BLOCKED — needs provisioned Widevine device creds (`.wvd`), separate
  from the ADP token; surface to the user before building.
- Wire `CencDecryptor` into amazon (`WidevineSegmentDecryptor`/`WidevineDecryptor`); delete dead crypto
  (OptimizedHttpClient, SafeArray, unused CryptoUtilities); collapse the duplicate license HTTP path.
- Apple: adopt Common `DrmTrack`/`IExternalDownloadHandler` seam; FairPlay cbcs via `CencDecryptor`.
- Converge Common pins (amazon `f2f84cb` / apple `80a0eb5` → `origin/main`); update `ext-common-sha.txt` + gitlink.

## Landing
- Common CENC work → PR #601 (open, not auto-merged).
- Amazon round-5 + fixes → already in open PR #2 (`ci/full-workflow-parity`).

## Guardrails active
TDD always; independent crypto vectors; consolidate to Common; explicit-path staging; never `git add -A`;
never commit `artifacts/` or phantom EOL drift; build-on-top of the parallel AI (retry-with-wait on file locks).
