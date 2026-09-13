# Runtime creation reservation audit — 2026-09-13

## Scope

This audit records the H3 repair in `HostBridgeRuntimeCache`. The candidate is
based on Common `5fce62b79918c6157426f19c9f460da41addd148` and the test-first
fixture head `f07265d35f7f74866777b24f8dc5fb8d8fff05d6`.

## Contract

`GetAsync` admits one global creation reservation while holding the semaphore,
then invokes and awaits `CreateAsync` after releasing it. Followers await that
reservation and retry the normal lookup, so a successful creation is published
once and matching callers share it. The owner receives the factory's original
success, null, or exception outcome; follower cancellation only cancels that
follower's wait.

`ResetAsync` claims the in-flight reservation by reference, clears cache
publication, and starts the current/parked disposal drain before waiting for the
factory settlement. A late runtime is therefore owned and disposed by the reset
generation exactly once. A successful owner waits for the reset generation and
retries; null, failure, and cancellation preserve the owner's outcome. Cache
bookkeeping and reset signalling use uncancelled waits, and user factory and
disposal code runs outside the semaphore.

## Evidence and limits

The test-only baseline at `f07265d35f7f74866777b24f8dc5fb8d8fff05d6` records
23 tests: 20 passed, 3 intended failures, and 0 skipped. The failures expose
the async and synchronous factory-prefix races that this candidate addresses;
the ownership, cancellation, null/fault, admission, reset, and disposal
controls pass. Receipt: `.omc/state/tech-debt-retirement/h3-client-creation-20260913/red-f072/`.

This change does not add factory timeouts, leases, recursive factory support,
per-key parallel creation, or a disposal concurrency cap. Focused green,
mutation, and full-suite validation remain required before promotion.
