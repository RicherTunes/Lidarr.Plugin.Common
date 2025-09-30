# Doc Testing & Automation

Documentation is part of the build. This page explains the tooling that keeps it accurate.

## Snippet verification
All code samples in the docs are extracted from real source files using snippet tags.

1. **Tag code** in source files:

   ```csharp

   // snippet:alc-loader
   public static async Task<PluginHandle> LoadAsync(PluginLoadRequest request)
   {
       // ...
   }
   // end-snippet

   ```

2. **Reference the snippet** in markdown:

   ```md

   ```csharp file=../examples/IsolationHostSample/Program.cs#alc-loader```

   ```

3. **Verify locally**:

   ```bash

   dotnet tool restore
   dotnet run --project tools/DocTools/SnippetVerifier

   ```

   The verifier extracts each tagged snippet, compiles it, and fails if the code or path is invalid.

## Link and style checks
Docs CI runs:

- `markdownlint` – heading nesting, fenced code formatting.
- `cspell` – spelling of identifiers and product names.
- `lychee` – link integrity (same tool used in CI).
- Optional: `vale` – tone and terminology (install separately if you need editorial checks).

Run them locally with:

```bash

pwsh ./tools/DocTools/lint-docs.ps1

```

Requires Node.js (for `npx`) and, if you want local link checking, the `lychee` CLI on your PATH.

## Public API baselines
When CI reports RS0016/RS0026 warnings, update the relevant baseline files as described in [Public API baselines](../reference/PUBLIC_API_BASELINES.md).

## CI workflow
See [`../dev-guide/CI.md`](CI.md) for the GitHub Actions workflow that orchestrates these checks. A docs-only change must pass the same gates as code changes.


