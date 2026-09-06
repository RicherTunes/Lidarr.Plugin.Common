# HTTP helper ownership and cloning consolidation — 2026-09-06

## Integration boundary

Candidate branch: `fix/http-helper-ownership-20260906`, based on Gitea main `e09f7791d1357b5316817b9fd9c5616e28e0704f`.

This is independent of the gate-lifecycle candidate `c79db2350b83b1bceadb192d2dc2793d68842d27` (PR #129). That branch, the original user checkouts, consumer branches and consumer pins were not changed. This report is not a claim that either candidate is merged or adopted by the five plugins.

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

Accepted receipts are under `artifacts/http-helper-ownership/`: `red/json-ownership-red.trx`, `red/clone-red-json-green.trx`, `green/ownership-cloning-green.trx`, `green/http-helper-final-focused.trx`, `full/http-helper-full.trx`, and `cli/http-helper-cli.trx`. The 35-case final focused result is confirmed; do not add the five supplemental cases to the earlier full-suite count and present the sum as an observed full-run result.

One earlier broad filtered test invocation exceeded its tool timeout and yielded no accepted completion receipt. It is not counted as a pass. The owned full-suite runs are separate executions, not inferred results.

## Review and static validation

The read-only Codex review is retained at `artifacts/http-helper-ownership/independent-review.md`; verdict: APPROVE, scoped source review only. The reviewer did not run tests or establish consumer/runtime acceptance. Production code remained unchanged after that review.

Targeted production analyzer verification and the documentation-reference sentinel passed. Whitespace verification of the new test files returned success with a workspace-loading warning; no formatting changes were requested. Remaining source-reviewed boundaries include unfinished-clone disposal (no production-only test hook was introduced) and cancellation during synchronous deserialization, which is not forcibly interruptible by these helpers.

## Five-plugin survey and safe reuse

The existing Gitea-main snapshots of AppleMusicarr, Brainarr, Qobuzarr, Tidalarr and AmazonMusicarr were searched for `CloneForRetryAsync`, `CloneHttpRequestMessageAsync`, `GetJsonAsync` and `PostJsonAsync` in their production source. The first, second, fourth and fifth repositories had no direct occurrences in the searched production roots. Qobuz's matches belong to `IQobuzHttpClient` and its Newtonsoft.Json adapters, not the Common System.Text.Json APIs.

Qobuz's host adapter uses Lidarr's `IHttpClient` and a per-request host timeout. Its CLI adapter uses .NET `GetStringAsync` and Newtonsoft.Json. Replacing either by a similarly named Common JSON method would alter transport/serializer contracts; no such replacement was performed. This is a bounded symbol/mechanism survey, not proof that every HTTP implementation across every plugin has been exhaustively audited.

Common's production retry clone call sites include `OAuthDelegatingHandler` and both typed resilience cores, so existing Common consumers inherit the shared fix on adoption without per-plugin copies. Public external DRM seams are untouched.

## Remaining integration gates and debt

Consumer repinning, parity/build/package validation, five-plugin Docker coexistence and combined validation with PR #129 remain separate acceptance gates. Earlier Docker receipts are not evidence for this candidate. No live external music services or credentials were used by the new tests.

This pass does not claim zero technical debt. The two legacy typed Retry-After parsers still need contract-first consolidation; Qobuz's CLI adapter still mutates its shared client's timeout and warrants its own focused regression/repair. Mutable download-state snapshots, historical branch recovery and other recorded lifecycle work remain separate. Neither public APIs nor diagnostics were removed merely because a narrow symbol search found no callers.

## Primary API reference

The option-copy reasoning was checked against the .NET 8 implementation, not guessed from the generic method signature:

`https://raw.githubusercontent.com/dotnet/runtime/v8.0.0/src/libraries/System.Net.Http/src/System/Net/Http/HttpRequestOptions.cs`

The .NET 8 HttpClient implementation was also inspected for response buffering and request-content ownership:

`https://raw.githubusercontent.com/dotnet/runtime/v8.0.0/src/libraries/System.Net.Http/src/System/Net/Http/HttpClient.cs`
