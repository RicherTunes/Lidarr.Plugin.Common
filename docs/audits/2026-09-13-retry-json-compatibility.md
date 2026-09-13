# Retry body JSON compatibility audit

Date: 2026-09-13  
Candidate: test/retry-json-compatibility-20260913

## Scope

Common now resolves an LLM 429 retry hint from a strict root JSON object.
The accepted property names are retry_after and retry-after; the value
must be one JSON number. Equal aliases are rejected as duplicates, as are
unequal aliases and duplicate properties. Parsing is bounded by the shared
10 MiB JSON size and depth limits, and durations must be finite, non-negative,
and representable by TimeSpan. A body that is not JSON retains the legacy
retry[-_]?after token grammar with invariant decimal parsing. JSON-shaped
but invalid input fails closed.

OpenAI completion, health transport errors, and streaming errors now pass the
complete buffered body to the protected mapper hook. The mapper performs
semantic classification on that body and uses the existing truncation only
for exception display text. Header-derived Retry-After remains authoritative.
Public hook signatures, status mapping, inner exceptions, and provider request
schemas are unchanged.

## Validation

The test-first semantic red was captured at
.omc/state/tech-debt-retirement/retry-json-compatibility-20260913/semantic-red-1606cbe.trx
(94 selected tests: 77 passed, 17 intended failures, 0 skipped). The focused
green receipt is recorded alongside this audit after the candidate implementation
is exercised. No provider authentication, publishing, or remote-runtime claim
is made by this audit.
