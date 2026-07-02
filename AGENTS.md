# Repository Guidelines

## Project Structure & Module Organization
- Core libraries live under `src/`: `Lidarr.Plugin.Common` (resilience, caching, downloads) and `Abstractions` (manifest/contracts). Multi-targeting is managed via `Directory.Build.props`.
- `tests/` hosts xUnit suites and isolation fixtures; run them with or without the optional CLI feature flag. `testkit/` exposes reusable HTTP gremlins and plugin harnesses for downstreams.
- CLI assets reside in `src/CLI`, while sample integrations are under `examples/`. Supporting docs and playbooks live in `docs/`; automation lives in `tools/` and `scripts/`.

## Build, Test, and Development Commands
- `dotnet build -c Release` — compiles all targets and enforces analyzers.
- `dotnet test -c Release --no-build` — runs the full suite; append `-p:IncludeCLIFramework=true` when validating CLI scenarios.
- `dotnet format src/Lidarr.Plugin.Common.csproj analyzers --verify-no-changes` — ensures analyzers stay clean.

## Coding Style & Naming Conventions
- Follow the repo `.editorconfig`: 4-space indentation, UTF-8, `var` for obvious types, and PascalCase for public members. Private fields use `_camelCase`; locals remain `camelCase`.
- Prefer expression-bodied members for short delegates; avoid region blocks.
- Run `dotnet format` before pushing; markdown is linted with `.markdownlint.yaml` and Vale (`.vale/` rules).

## Testing Guidelines
- Tests are xUnit; name classes `*Tests` and methods `Should_*`. Put provider fixtures in `tests/Common.SampleTests` and isolation scenarios in `tests/Isolation`.
- Include negative-path coverage (timeouts, rate limits, cache misses) whenever adding resilience features.
- Record new golden payloads in `testkit/Fixtures` and document provenance in README comments.

## Commit & Pull Request Guidelines
- Use Conventional Commit prefixes (`feat:`, `fix:`, `chore:`) as reflected in `git log`.
- PRs must: describe behavioral changes, link issues, note config migrations, and attach `dotnet test` output (or GitHub Actions run). Include screenshots for CLI UX changes.
- Before opening a PR: run build, tests, analyzer verification, and regenerate API baselines if public surfaces changed. Ensure `ReportGenerator` artifacts upload cleanly.

## Tooling & Automation Tips
- GitHub Actions expect both .NET 6.0 and 8.0 SDKs; mirror that locally when debugging.
- Packaging is handled via `PluginPackaging.targets`; use `dotnet pack -c Release` to produce plugins and verify `dist/` outputs.
- Secrets and tokens must flow through the provided masking helpers (`IsSensitiveParameter`) before logging.

## Reusable Workflow Pin Policy
Same-org reusable workflow references (`RicherTunes/Lidarr.Plugin.Common/.github/workflows/*.yml`) use a tiered pinning model — enforced by `scripts/lint-workflow-sha-pins.ps1`:

| Tier | Pattern | Used by | Reason |
|------|---------|---------|--------|
| Versioned | `@workflows/v1` | General reusable workflows (packaging-gates, multi-plugin-smoke-test, single-plugin-e2e) | Stable API. Move tag with `git tag -f workflows/v1 origin/main && git push --force` after Common changes. Bump `v1` → `v2` only on breaking workflow input/output changes. |
| Bootstrap | `@main` | `verify-common-pins`, `bump-common-plugin`, `release-plugin` | These manage pin/version state — must self-update by tracking HEAD. |
| Third-party | `@<40-char SHA>` | Non-org actions (`actions/checkout`, etc.) | Security: prevents tag-mutation supply chain attacks. |

The lint script rejects any other pattern. To add a new bootstrap workflow, update `$script:BootstrapWorkflows` in `scripts/lint-workflow-sha-pins.ps1`.

## Ecosystem Consolidation & Parity Discipline (canonical)

This library is the shared core of a **five-plugin ecosystem** — `amazonmusicarr` (Widevine), `applemusicarr` (FairPlay), `tidalarr`, `qobuzarr`, `brainarr` (ImportList-only) — each vendoring Common as a submodule. The plugins are **copy-paste-adjacent**, so the highest-leverage work is cross-plugin: **every bug is a bug class, every duplicated smell exists 2–5×.** Work this way (and expect other agents to):

1. **Find once → sweep five.** When you find or fix a bug/smell in one plugin, immediately grep the other four **and Common** for the same pattern and fix every instance in the same effort. Never ship a one-plugin fix for a shared-surface class (auth/retry, rate-limit/Retry-After, catalog→ReleaseInfo field mapping, path/SSRF guards, token store, pagination, date/number parsing).
2. **Shared logic lives in Common; plugins ADOPT it.** If the same logic exists in ≥2 places, push it into Common and have all consumers call the one implementation. A thin plugin subclass purely for DI is fine (e.g. `TidalRateLimitingHandler : AdaptiveRateLimitingHandler` — "all logic lives in Common"). **De-duplicate inside Common too** — e.g. Retry-After parsing: `RateLimitHeaderUtilities.ResolveRetryAfter` is canonical (guards negative delta on both Delta and Date branches); `HttpResponseHelpers.ParseRetryAfter` is a *legitimately different* helper (raw string headers); a third near-duplicate that drops the negative-delta guard is a real divergence to fix, not to tolerate.
3. **EXCEPTION — the out-of-tree DRM seam stays plugin-owned and PUBLIC** (`IExternalDownloadHandler`, plus per-plugin CDM seams like amazon's `IWidevineCdm`/`IProcessLineChannel`). ILRepack `Internalize=true` makes Common types internal in the merged plugin DLL, so a Common-resident seam cannot be resolved across the out-of-process boundary. **Do NOT consolidate these into Common.**
4. **The parity matrix is a CONTRACT.** Each plugin's `*.Parity.Tests` pin `Check_*` facts (file/class-name parity, "CLAUDE.md documents Common helpers", etc.). A Common change is **not done** until you re-pin every consuming plugin's `ext-common-sha.txt` + gitlink **and** run that plugin's parity tests green. Don't break the matrix to land a fix.
5. **Architecture differences are real — verify before assuming a class sweeps.** Plugins that parse **raw JSON via alias-list probing** (`GetString(obj, "a", "b")`) are vulnerable to an id-field-leaking-into-a-name-alias bug; plugins using **typed DTOs** with `.Name` properties are immune to that exact class. Confirm the actual mechanism in each plugin before "fixing" — a bug in one architecture may not exist in another. Survey agents over-report (~1 in 6 real); benign/intentional-per-plugin patterns are pinned by tests — document + skip, never churn.

**Mechanics:** Common changes land via an **isolated-worktree PR from `origin/main`** (`git worktree add /tmp/wt -b fix/x origin/main`; never edit a shared checkout another agent is using); TDD red→green; run `tests/Lidarr.Plugin.Common.Tests.csproj`; push + `gh pr create`; remove the worktree. Plugin changes commit on each repo's active branch with **explicit paths** (never `git add -A`). Retry-with-wait on `CS2012`/`MSB3026` file locks (a lock is not a failure; never kill/​revert a parallel agent's work). Crypto/DRM/signer tests use **independent published vectors** (NIST, pywidevine, Bento4/shaka), never the implementation against itself.

> Each plugin's `CLAUDE.md` should point here rather than restate this (consolidation applies to docs too).
