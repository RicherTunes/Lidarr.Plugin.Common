# Legacy retry body safety audit — 2026-09-13

## Scope

`LlmErrorMapper.MapHttpError` accepts legacy provider response bodies that may
contain a `retry_after` or `retry-after` token. This bounded safety fix keeps
that legacy regular-expression grammar and the explicit `Retry-After` header
override contract intact.

## Contract

- Dot-decimal body values use invariant parsing, so `1.5` means 1.5 seconds
  under every process culture.
- A matched value that is nonfinite, negative, or outside `TimeSpan`'s
  representable range is treated as an absent body hint.
- An unusable body hint never prevents the status-specific exception from
  being returned. Existing provider identity, error code, and supplied inner
  exception are preserved for non-rate-limit mappings.
- Explicit `retryAfter` input continues to take precedence over body parsing.
- The legacy regex grammar remains unchanged; this work does not introduce a
  JSON resolver.

## Evidence

- Test-only red commit: `837c6041d474d0be6926b24e5ecf6dda831adb23`.
- Independent red review approved the corrected 400-digit nonfinite fixture.
- Red execution receipt: `.omc/state/tech-debt-retirement/retry-body-safety-20260913/semantic-red-receipt-837c604.txt`.
- Production implementation uses `NumberStyles.Float` with
  `CultureInfo.InvariantCulture`, rejects nonfinite/negative values and
  values above `TimeSpan.MaxValue.TotalSeconds`, and then constructs the
  delay.
- Green focused receipt: `.omc/state/tech-debt-retirement/retry-body-safety-20260913/testonly-green-postfix-release.log`.
- Green focused TRX: `tests/TestResults/testonly-green-postfix-release.trx`.
- The focused green run passed 49/49 tests with 0 failures and 0 skips using
  serialized Release execution and disabled build servers.
