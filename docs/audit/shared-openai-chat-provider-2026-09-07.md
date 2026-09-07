# Shared OpenAI Chat Provider Audit

The Common provider base owns the OpenAI Chat Completions wire mechanics while adapters retain provider endpoint, model normalization, headers, error mapping, parser extensions, and host transport behavior.

The serialized body keeps the Brainarr field order: model, messages, temperature when enabled, max_tokens, stream, and response_format when enabled. The serializer uses relaxed escaping to preserve ordinary Unicode and HTML-sensitive prompt text on the wire.

The base deliberately changes empty successful responses into `InvalidRequest` provider errors. A malformed but non-empty response remains raw content so existing salvage parsers can handle truncated JSON.

Evidence is stored under `artifacts/shared-openai-chat`. The initial reflection red used the Abstractions assembly and is a harness error, not behavioral evidence. The subsequent behavioral red and candidate contract receipts are the relevant records. This audit does not claim live-auth, soak, full-suite, CLI-suite, or five-plugin coexistence proof.

The first unpromoted stream fix was committed before its regression test and was reverted in `2b06d48`. The corrected TDD sequence is test-only `f925fd9`, genuine red `red-stream-contract.trx`, then implementation `fd858ec` and green `green-stream-contract.trx`.

Provider error mapping now removes the configured API key from surfaced mapped exception text. This is an intentional credential-hygiene improvement; caller cancellation remains unwrapped.
