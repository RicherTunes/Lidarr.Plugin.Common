# Shared OpenAI Chat Provider Audit

The Common provider base owns the OpenAI Chat Completions wire mechanics while adapters retain provider endpoint, model normalization, headers, error mapping, parser extensions, and host transport behavior.

The serialized body keeps the Brainarr field order: model, messages, temperature when enabled, max_tokens, stream, and response_format when enabled. The serializer uses relaxed escaping to preserve ordinary Unicode and HTML-sensitive prompt text on the wire.

The base deliberately changes empty successful responses into `InvalidRequest` provider errors. A malformed but non-empty response remains raw content so existing salvage parsers can handle truncated JSON.

Evidence is stored under `artifacts/shared-openai-chat`. The initial reflection red used the Abstractions assembly and is a harness error, not behavioral evidence. The subsequent behavioral red and candidate contract receipts are the relevant records. This audit does not claim live-auth, soak, full-suite, CLI-suite, or five-plugin coexistence proof.

The first unpromoted stream fix was committed before its regression test and was reverted in `2b06d48`. The corrected TDD sequence is test-only `f925fd9`, genuine red `red-stream-contract.trx`, then implementation `fd858ec` and green `green-stream-contract.trx`.

Provider error mapping now removes the configured API key from surfaced mapped exception text. This is an intentional credential-hygiene improvement; caller cancellation remains unwrapped.

The same boundary applies to health and stream transport exceptions. Secret-bearing causes are replaced by a redacted normalized provider exception; non-sensitive mapped errors retain their original identity.

Streaming uses one linked `ResilienceTimeout` lease for connection and all decoder reads. A valid shorter request timeout tightens the provider limit; caller cancellation remains an unwrapped cancellation and internal expiry maps to a recoverable network timeout. Ten consecutive provider-contract runs are retained in the timing-repeat artifacts.

The shared SSE reader was corrected in `ed32e80` to use cancellation-aware asynchronous `ReadLineAsync` calls instead of the synchronous `EndOfStream` probe. The final regression contracts are `18d260e` (async-only stream, pre-cancel, pending-read cancellation without emitting an unterminated partial frame, and source hygiene) and `2636d83` (blocked decoder read: internal deadline maps to `NetworkException`/`Timeout`; caller cancellation remains an `OperationCanceledException`; both dispose the response). The isolated `gitea/main` worktree at `9480258` failed 5 of 19 focused contracts in `red-old-sse-reader-expanded.trx`, including the synchronous-read and hygiene defects. Candidate receipt `green-sse-provider-final-2636d83.trx` passed 85 of 85 focused SSE/decoder/provider contracts. `green-stream-timeout-cancel-2636d83-01.trx` through `-10.trx` are ten sequential no-build runs; each passed both blocked-read contracts with no skips.
