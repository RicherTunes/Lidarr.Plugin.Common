# Shared OpenAI Chat Provider Audit

The Common provider base owns the OpenAI Chat Completions wire mechanics while adapters retain provider endpoint, model normalization, headers, error mapping, parser extensions, and host transport behavior.

Wire compatibility is byte-exact EXCEPT float temperatures: legacy Newtonsoft widened request float temperatures to double before serialization (0.2f emitted as 0.20000000298023224); the shared base keeps the float and emits the shortest-round-trip form (0.2). This is a deliberate, semantically equivalent normalization. The serialized body keeps the Brainarr field order: model, messages, temperature when enabled, max_tokens, stream, and response_format when enabled. The serializer uses relaxed escaping to preserve ordinary Unicode and HTML-sensitive prompt text on the wire.

The base deliberately changes empty, whitespace-only, choice-less, and recognizably structured responses with missing content into `InvalidRequest` provider errors. Malformed or type-invalid non-empty responses remain raw content so existing salvage parsers can handle truncated payloads; `usage: null` remains valid while an incompatible usage shape falls back.

Evidence is stored under `artifacts/shared-openai-chat`. The initial reflection red used the Abstractions assembly and is a harness error, not behavioral evidence. The subsequent behavioral red and candidate contract receipts are the relevant records. This audit does not claim live-auth, soak, full-suite, CLI-suite, or five-plugin coexistence proof.

The first unpromoted stream fix was committed before its regression test and was reverted in `2b06d48`. The corrected TDD sequence is test-only `f925fd9`, genuine red `red-stream-contract.trx`, then implementation `fd858ec` and green `green-stream-contract.trx`.

Provider error mapping now removes the configured API key from surfaced mapped exception text. This is an intentional credential-hygiene improvement; caller cancellation remains unwrapped.

Already-mapped provider exceptions keep subtype, error code, retryability, retry-after, and exact identity when their full exception graph is safe. Secret-bearing mapped errors are rebuilt with matching semantic metadata and without their unsafe inner exception. Auth-circuit probe and failure-record callback errors cannot leak the configured key or replace the primary provider error. The original Brainarr policy that a failing success callback invalidates completion is preserved and its surfaced error is sanitized. An initial independent-review suggestion to make success bookkeeping best-effort was implemented in `d0b3af1`, then withdrawn after comparison with the original source and transparently reverted in `8373447`.

The same boundary applies to health and stream transport exceptions. Secret-bearing causes are replaced by a redacted normalized provider exception; non-sensitive mapped errors retain their original identity.

Streaming uses one linked `ResilienceTimeout` lease for connection and all decoder reads. A valid shorter request timeout tightens the provider limit; caller cancellation remains an unwrapped cancellation and internal expiry maps to a recoverable network timeout. Ten consecutive provider-contract runs are retained in the timing-repeat artifacts.

Streaming response and decoder cleanup is explicit: disposal-only failures are mapped and sanitized, while a primary read/provider error or caller cancellation wins over a secondary disposal failure. Early consumer abandonment still disposes the response. Whitespace chunks may flow to consumers, but a stream containing no non-whitespace content or reasoning delta fails its final completion check.

The shared SSE reader was corrected in `ed32e80` to use cancellation-aware asynchronous `ReadLineAsync` calls instead of the synchronous `EndOfStream` probe. The final regression contracts are `18d260e` (async-only stream, pre-cancel, pending-read cancellation without emitting an unterminated partial frame, and source hygiene) and `2636d83` (blocked decoder read: internal deadline maps to `NetworkException`/`Timeout`; caller cancellation remains an `OperationCanceledException`; both dispose the response). The isolated `gitea/main` worktree at `9480258` failed 5 of 19 focused contracts in `red-old-sse-reader-expanded.trx`, including the synchronous-read and hygiene defects. The later focused candidate receipt passed 108 OpenAI/SSE contracts. Final-source repetitions are recorded as `green-final-stream-timing-01.trx` through `-10.trx`, each covering both blocked-read contracts with no skips.

The existing SSE limit still reads a complete physical line before enforcing its maximum and counts UTF-16 characters rather than encoded UTF-8 bytes. This extraction makes no strict byte-bound or arbitrary-line allocation claim; that pre-existing hardening remains separate audit debt.
# Follow-up: invocation-owned completion timeout

Common originally stored one constructor-level completion timeout and privately clamped every request against it. Brainarr's host invocation scope can supply a longer or shorter timeout dynamically, so its 120-second scope was incorrectly reduced to the Common shell's 30-second constructor fallback. Brainarr commit `7e5f249c` records the downstream red evidence: buffered dispatch captured 30 seconds instead of 120, while a one-second streaming owner timeout failed to stop the blocked stream within three seconds.

The additive `ResolveCompletionTimeout()` hook lets a derived shell resolve its owner timeout once per operation. Common keeps `ResolveRequestTimeout` private and continues to apply the positive-shorter request rule, rejects non-positive owner values before transport dispatch, and reuses one resolved streaming value for both `ResilienceTimeout` and `OpenAiChatRequest.Timeout`. `CheckHealthAsync` continues to use `HealthTimeout` and does not call the completion hook.

Common red evidence is retained in `artifacts/timeout-owner/red-timeout-owner-surface.trx` and `red-timeout-owner-derived-build.log`. The surface failure showed the hook absent; the derived build failed with CS0115 before the source change. Focused exact-head receipts and downstream Brainarr verification are recorded with the candidate review evidence.
