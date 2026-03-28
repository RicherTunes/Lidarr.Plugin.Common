# Architecture Status — Bridge Runtime Migration

**Status:** COMPLETE (frozen 2026-03-28)
**Baseline:** Common v1.7.1, all plugins on SHA `aae92da`
**Promotion matrix:** 107/107 green

## What Was Done

### Bridge Contracts (v1.7.0-v1.7.1)
- 4 bridge contracts shipped: IAuthFailureHandler, IIndexerStatusReporter, IRateLimitReporter, IDownloadStatusReporter
- 4 default implementations registered via `AddBridgeDefaults()`
- Thread-safe singletons with volatile/lock patterns
- Fixture-backed compliance tests (68 bridge/compliance tests)

### Plugin Integration
| Plugin | Bridge Slice | Indexer Reporting | Download Reporting | Runtime Tests |
|--------|-------------|-------------------|-------------------|---------------|
| Tidalarr | Slice 1 | IIndexerStatusReporter (4 methods) | Deferred | 10/10 |
| AppleMusicarr | Slice 2 | IIndexerStatusReporter + lifecycle | IDownloadStatusReporter | 10/10 |
| Qobuzarr | Slice 3 | BridgeQobuzApiClient + adapter | Deferred | 11/11 |
| Brainarr | Exempt | N/A (no indexer) | N/A (no download) | 8/8 |

### PluginSandbox
- Strict/permissive loader modes (strict is default)
- Single IPlugin enforcement (fail-fast on >1)
- PluginType explicit selection option
- DefaultHostVersion aligned to 3.1.2.4913
- ReflectionTypeLoadException handling for ILRepack'd assemblies

### Hardening
- ~500 tests added across ecosystem
- Zero bare catch blocks remaining
- 105 net6.0 references eliminated
- Thread safety fixes (provider swap, bridge singletons)
- 5 production bugs fixed (crypto-cache, null-ref, SampleRate, DI bypass, GUID collision)

### Operational Governance
- Release policy: `docs/RELEASE_POLICY.md`
- Promotion checklist: `docs/ECOSYSTEM_PROMOTION_CHECKLIST.md`
- Debt governance: quarterly review, exemption policy with review dates
- Shadow-mode PR enforcement in billing-blocked repos
- Bridge exemption: `.bridge-exempt` marker with owner/review date

## What Is NOT Done

- CI enforcement (blocked on billing for 3 repos)
- NuGet publishing (needs NUGET_API_KEY secret)
- Docker smoke in CI (needs host-bridge build pipeline)
- CLI decision (deadline: 2026-06-19)

## Standing Rules

1. **No new abstractions** without a real consumer + compliance test + fixture
2. **No bridge surface expansion** until all existing contracts have full consumer coverage
3. **Promotion matrix must pass** (107/107) before any Common release is promoted
4. **Quarterly parity audit** required (next: 2026-06-28)

## Next Phase: Enforcement, Not Migration

The architecture is done. The next risk is operational drift, not design debt.
Focus areas: CI activation, release distribution, product-surface decisions.
