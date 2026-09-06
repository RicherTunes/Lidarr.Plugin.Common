# Typed Retry-After consolidation — 2026-09-06

## Integration boundary

Candidate branch: `refactor/retry-after-contracts-20260906`.
Base: Gitea main `2ca87d49f2d3fe77d88730a5339595775c8274a5`, which includes the gate-lifecycle fix from PR #129.
Worktree: `C:/Users/Alexandre/.devspace/worktrees/common-5d2f6381`.

The user is integrating earlier work. This candidate does not merge into main/master, repin consumers, alter the separate Common helper-cleanup branch/PR #130, or change the local Qobuz CLI timeout candidate. No historical branches or original dirty checkouts were reset or removed. The source changes below are independent of the unmerged HTTP cloning/JSON ownership changes in PR #130; final integration with a later main still requires validation.

## Shared implementation and preserved policies

The existing `RateLimitHeaderUtilities` is now the single owner of typed `RetryConditionHeaderValue` duration arithmetic in the inspected Common production paths. Two internal overloads allow a supplied clock or an already-captured observation instant; the existing public signatures remain unchanged. UTC is consulted only for HTTP-date resolution, once. Delta values and absent headers need no wall-clock read.

| Caller | Missing or malformed header | Expired HTTP date | Explicit zero delta | Other retained behavior |
| --- | --- | --- | --- | --- |
| Canonical public resolver | Zero | Zero | Zero | Delta/date values normalized to nonnegative duration |
| Typed HTTP execution, both paths | Existing policy backoff | Existing policy backoff | Immediate retry | Existing retry eligibility, elapsed budget, cancellation and response disposal |
| Shared download base | Existing policy backoff plus jitter | Existing policy backoff plus jitter | Existing additive jitter | Jitter remains 50–249 ms; exponential cap and attempt normalization unchanged |
| LLM error mapper | Null, allowing response-body fallback | Zero, overriding body hint | Zero, overriding body hint | Caller retains response ownership; error/status mapping unchanged |
| Rate-limit telemetry | No observation | Zero-delay observation | Zero-delay observation | Existing 429/503 selection; one captured timestamp used for calculation and observer |

Canonical resolution itself takes a parsed header; malformed wire values are interpreted by the framework's existing header parser. Tests distinguish malformed negative wire text from a negative header constructed directly by an adapter. RFC 9110 section 10.2.3 defines wire delay-seconds as a nonnegative decimal integer. This patch does not broaden accepted wire formats.

The deliberate corrections are negative constructed deltas: typed HTTP retry telemetry now reports zero rather than a negative duration, and the download base normalizes before adding jitter instead of returning a negative delay. Existing typed-HTTP scheduling already treated nonpositive delays as immediate; the telemetry correction does not add an extra wait. An explicit zero must not accidentally become fallback, and an absent/expired hint must not accidentally become an immediate retry where the caller historically backs off.

Production changes:

- `src/Services/Http/RateLimitHeaderUtilities.cs`: one arithmetic implementation with system-clock, injected-clock and captured-instant entry paths.
- `src/Utilities/HttpClientExtensions.cs`: two duplicate parsers replaced by one small policy wrapper delegating to the canonical resolver.
- `src/Base/BaseStreamingDownloadClient.cs`: reuse canonical resolution while retaining download-specific fallback and jitter.
- `src/Errors/LlmErrorMapper.cs`: reuse canonical resolution, keeping absence distinct from an explicit zero hint.
- `src/Services/Http/RateLimitTelemetryHandler.cs`: reuse the observation timestamp in canonical resolution.

The five production files total 35 added and 51 deleted lines: 16 fewer lines. No public API, dependency, timeout, concurrency limit, plugin setting, status policy or package requirement was added or changed. This is code removal and centralized ownership, not a measured throughput improvement.

Raw dictionary/header parsing in `HttpResponseHelpers` and the LLM JSON-body parser remain separate contracts and were not rewritten. The patch does not claim to eliminate all parsing logic in all transports.

## TDD and completed verification

The first accepted pre-fix run (`red/retry-after-verified-red.trx`) had 18 passing behavior characterizations and three failures: two real negative-delay telemetry failures (actual -1500 ms versus expected zero) and one new source-adoption guard. Earlier new-test analyzer errors and a case-sensitive host-filter setup error were corrected before this accepted run and are not counted as production defects.

The expanded consumer sweep added tests before changing the LLM mapper, telemetry handler and download base. That run (`red/retry-after-sweep-red.trx`) had 65 passing selected checks and four failures: one real negative download delay and three source-adoption guards. The guards establish the consolidation requirement, not additional runtime defects.

Final receipts under `artifacts/retry-after-contracts/`:

| Validation | Passed | Failed | Existing skips | Receipt |
| --- | ---: | ---: | ---: | --- |
| Final full Common default suite | 7,593 | 0 | 7 | `full/retry-after-final-full.trx` |
| Separate optional CLI lane | 196 | 0 | 0 | `cli/retry-after-cli.trx` |
| Affected HTTP/cache/LLM/telemetry/download suite | 242 | 0 | 0 | `green/retry-after-final-focused.trx` |
| New regression, characterization and adoption checks | 60 per run | 0 | 0 | `repeated/retry-after-repeat-1.trx` through `5.trx` |

The new set comprises 50 header/clock/typed-retry/download/telemetry cases, six LLM body-hint/ownership cases, and four source-adoption cases. Five repeated runs yielded 300 passing executions. They use synthetic handlers and controlled clocks; no live music service, account or credentials are involved.

The full suite's seven skips remain existing sample/package/platform checks, not newly disabled tests. The optional CLI build generated a 26-line lockfile delta, which was removed because it is not a dependency change in this candidate. Targeted production analyzer verification and diff checks passed.

An earlier intermediate full run (`full/retry-after-full.trx`) covered a smaller version of this change. It is retained as history only; the final 7,593-pass result comes from the separately completed final receipt above, not arithmetic added to the intermediate count.

## Independent review and limitations

A separate read-only Codex source review returned **APPROVE** with no concrete introduced defects. Its captured verdict is `artifacts/retry-after-contracts/independent-review.md`, and the bounded request is `review-request.md`. The reviewer did not execute tests, change files or establish consumer/remote acceptance. Production and tests were not modified after this approval; documentation was added afterward.

Two nonblocking coverage limitations remain explicit: the four source-adoption guards use file-wide text matching rather than semantic call-graph checks, and the download-specific matrix does not directly exercise future-date-plus-jitter. Date arithmetic, supplied-clock timestamps, positive delta jitter and expired-date fallback are independently covered; this does not turn the missing combination into a directly tested case. No production-only hook was introduced to simulate a clock in the protected download method.

The new internal fixture method in `TestDownloadClient` is one pass-through to the real protected retry-delay implementation. Ownership assertions check disposal before test cleanup, and the fake-clock tests verify sends and pending waits rather than accepting watchdog timeout as success.

## Five-consumer survey

Freshly fetched Gitea main snapshots were searched in the relevant production roots without editing them:

- AppleMusicarr `6391977b2f4605864b6f418280a6c23241d36c5a`: no direct typed-parser match in the bounded symbol survey; Common HTTP remains the adoption path.
- Brainarr `98e82fdde62a132d06ed05ef90cbbaf784a332c5`: raw host headers go through its thin adapter to Common `HttpResponseHelpers`; typed paths in `BrainarrZaiCodingProvider` and `StreamingHttpExecutor` already use `LlmErrorMapper.ParseRetryAfterHeader` and inherit this delegation after adoption.
- Qobuzarr `d0d130fd1caae92c29375c1782556d99bb883424`: its host transport uses Common's raw-header parser; that contract remains unchanged.
- Tidalarr `2bc45d5172f17ed6bf15a2b0f05de086e964647c`: `TidalApiClient` already uses `RateLimitHeaderUtilities`.
- AmazonMusicarr `9a17bb6e7fea2694e8e5224b78710cee1b3392ed`: the inspected license/decryptor retry-hint call sites already use `RateLimitHeaderUtilities`. No DRM implementation or public external seam was changed.

This is a scoped symbol/mechanism survey, not proof of exhaustive behavior for every plugin. Consumer pins remain untouched. Fresh consumer parity/build/package checks and real five-plugin coexistence are still required when this Common candidate is adopted. Earlier runtime receipts do not establish acceptance for these new source changes.

## Remaining boundaries

This closes the identified typed-header arithmetic duplication and negative-delay propagation in this slice, not all technical debt. The raw/body parser contracts, lifecycle coverage limitations recorded in PR #129, pending helper-cleanup adoption, Qobuz CLI timeout integration, atomic download-state snapshots, and historical branch reconciliation remain separately tracked work. The configured native operation-timeout range and retry-wait implementation are unchanged.

Primary protocol reference: RFC 9110, section 10.2.3, `https://www.rfc-editor.org/rfc/rfc9110.html#section-10.2.3`.
