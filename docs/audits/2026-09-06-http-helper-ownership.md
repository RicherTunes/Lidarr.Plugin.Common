# HTTP helper ownership and cloning consolidation — 2026-09-06

## Integration boundary

Candidate branch: `fix/http-helper-ownership-20260906`, Common PR #130. Initial implementation commit: `20c4ffbe88e95afa05f11aad392b757e9a06c262`, based on Gitea main `e09f7791d1357b5316817b9fd9c5616e28e0704f`.

While this candidate was being prepared, the user's integration advanced Gitea main to `2ca87d49f2d3fe77d88730a5339595775c8274a5`, merging the prior gate-lifecycle PR #129 (`c79db2350b83b1bceadb192d2dc2793d68842d27`). That new main was brought into this candidate branch only. Production edits combined automatically; the sole conflict was the two Unreleased changelog entries, resolved by retaining both. The imported gate implementation/tests match new main exactly. The incremental candidate remains five files and only one changed production file.

No main/master merge was performed by this pass. The gate-lifecycle source branch, original user checkouts, consumer branches and consumer pins were not changed. PR #130 is a review candidate, not a completed five-plugin rollout.

## Debt removed and behavior fixed

Both public request cloning methods now delegate to one private implementation in `HttpClientExtensions`. The shared code copies the method, URI, HTTP version/policy, request headers, options, and independent content wrapper. One-off cloning still reads the current body without adding the retry cache; retry cloning still populates/reuses `PluginHttpOptions.BufferedBodyKey`. Those distinct contracts are explicitly tested rather than flattened into a different API.

Option copying no longer constructs generic key types or methods through reflection for every value. It uses the existing `HttpRequestOptions.Set<object?>` API. The .NET 8 implementation stores values by string name and checks the runtime value type during typed retrieval; interface/delegate/boxed nullable/enum values and explicit null entries are covered. This removes reflective work; no measured throughput or allocation improvement is claimed.

A failed or cancelled body read now propagates once instead of being attempted a second time. Failed reads are not cached. An unfinished clone is disposed on failure without disposing the caller's source request/content. The one-off helper now rejects a missing source with `ArgumentNullException`, matching the retry helper; public method signatures remain unchanged.

JSON GET/POST helpers dispose the responses they create on success and on HTTP, JSON and media-type failures. POST disposes its generated `StringContent` even when sending throws or is cancelled. The caller's `HttpClient` and handler remain caller-owned. POST now uses the existing Common `HttpContentLightUp.ReadAsStringAsync` instead of a separate uncancellable call. Both helpers check caller cancellation after response buffering and after the string read.

Existing differences are retained: GET uses case-insensitive defaults, accepts empty/whitespace as default, checks response media type and rejects deserialized null; POST retains its original serializer defaults, rejects empty/whitespace, accepts JSON null and does not add a new response media-type restriction. Request serialization options and `application/json; charset=utf-8` are characterized. Buffering and response-size limits have not been changed.

## TDD evidence

- Existing `HttpClientExtensionsTests` baseline: 17 passed.
- JSON regression/characterization run before its implementation changes: 16 failed, 2 passed; failures were undisposed resources or missed caller cancellation, not compilation/setup errors.
- Clone regression/characterization run before its implementation changes: 3 failed, 9 passed. Both repeat-read cases and the missing-source exception contract were observed.
- Initial combined new tests after production changes: 30 passed.
- Independent source review approved the production changes. Five additional cases and extra assertions then addressed review coverage gaps without further production changes: empty JSON bodies, custom POST serialization/headers, successful cache population/reuse, and null-option retrieval equivalence.
- Final focused HTTP/OAuth/new-test run: 112 passed, 0 failed, 0 skipped. The final new-test set contains 35 cases.
- Completed full default suite (before those five supplemental cases): 7,535 passed, 0 failed, seven existing skips. The later full run with the supplemental cases was started separately, but its status-query action was blocked; no unconfirmed result from that invocation is used for acceptance.
- Separate optional CLI lane: 196 passed, 0 failed, 0 skipped. Its generated 26-line lockfile additions were removed from this candidate; no dependency change is included.
- After importing the user's new main at `2ca87d4`: the combined full default suite passed **7,568 tests, zero failures, seven existing skips**; the combined focused suite passed **140 tests**, and the rebuilt combined CLI lane passed **196 tests**. These are directly observed separate receipts, not arithmetic extrapolations from earlier runs.

Accepted receipts are under `artifacts/http-helper-ownership/`: `red/json-ownership-red.trx`, `red/clone-red-json-green.trx`, `green/ownership-cloning-green.trx`, `green/http-helper-final-focused.trx`, `full/http-helper-full.trx`, and `cli/http-helper-cli.trx`. The final combined candidate is covered by `combined-main/combined-main-focused.trx`, `combined-main/combined-main-full.trx`, and `combined-main/combined-main-cli.trx`; its full-run log explicitly records 7,568 passing tests.

One earlier broad filtered test invocation exceeded its tool timeout and yielded no accepted completion receipt. It is not counted as a pass. The owned full-suite runs are separate executions, not inferred results.

## Review and static validation

The first read-only Codex review is retained at `artifacts/http-helper-ownership/independent-review.md`; verdict: APPROVE, scoped source review only. A second independent integration review in `artifacts/http-helper-ownership/combined-main-review.md` also returned APPROVE after inspecting the final 35 cases and the diff against newly merged main. Neither reviewer ran tests or established consumer/runtime acceptance. The helper implementation hunks remained unchanged after the first review. Gate-lifecycle changes were imported verbatim from the user's new main, not rewritten by this pass.

Targeted production analyzer verification, the sync-over-async gate and the documentation-reference sentinel passed on the combined candidate. Whitespace verification of the new test files returned success with a workspace-loading warning; no formatting changes were requested. Remaining source-reviewed boundaries include unfinished-clone disposal (no production-only test hook was introduced) and cancellation during synchronous deserialization, which is not forcibly interruptible by these helpers.

## Five-plugin survey and safe reuse

The existing Gitea-main snapshots of AppleMusicarr, Brainarr, Qobuzarr, Tidalarr and AmazonMusicarr were searched for `CloneForRetryAsync`, `CloneHttpRequestMessageAsync`, `GetJsonAsync` and `PostJsonAsync` in their production source. The first, second, fourth and fifth repositories had no direct occurrences in the searched production roots. Qobuz's matches belong to `IQobuzHttpClient` and its Newtonsoft.Json adapters, not the Common System.Text.Json APIs.

Qobuz's host adapter uses Lidarr's `IHttpClient` and a per-request host timeout. Its CLI adapter uses .NET `GetStringAsync` and Newtonsoft.Json. Replacing either by a similarly named Common JSON method would alter transport/serializer contracts; no such replacement was performed. This is a bounded symbol/mechanism survey, not proof that every HTTP implementation across every plugin has been exhaustively audited.

Common's production retry clone call sites include `OAuthDelegatingHandler` and both typed resilience cores, so existing Common consumers inherit the shared fix on adoption without per-plugin copies. Public external DRM seams are untouched.

## Remaining integration gates and debt

Combined source validation with merged PR #129 is complete: 140 focused tests, 7,568 full-suite tests and 196 CLI tests passed as recorded above. Consumer repinning, parity/build/package validation and five-plugin Docker coexistence remain separate acceptance gates. Remote CI on the updated PR head has not been verified in this pass; it must not be labeled passed from local evidence. Earlier Docker receipts are not evidence for this candidate. No live external music services or credentials were used by the new tests.

This pass does not claim zero technical debt. The two legacy typed Retry-After parsers still need contract-first consolidation; Qobuz's CLI adapter still mutates its shared client's timeout and warrants its own focused regression/repair. Mutable download-state snapshots, historical branch recovery and other recorded lifecycle work remain separate. Neither public APIs nor diagnostics were removed merely because a narrow symbol search found no callers.

## Primary API reference

The option-copy reasoning was checked against the .NET 8 implementation, not guessed from the generic method signature:

`https://raw.githubusercontent.com/dotnet/runtime/v8.0.0/src/libraries/System.Net.Http/src/System/Net/Http/HttpRequestOptions.cs`

The .NET 8 HttpClient implementation was also inspected for response buffering and request-content ownership:

`https://raw.githubusercontent.com/dotnet/runtime/v8.0.0/src/libraries/System.Net.Http/src/System/Net/Http/HttpClient.cs`
