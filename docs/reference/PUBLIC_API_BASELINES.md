# Public API Baselines

The repository uses `Microsoft.CodeAnalysis.PublicApiAnalyzers` to ensure any change to the public surface of `Lidarr.Plugin.Abstractions` or `Lidarr.Plugin.Common` is intentional.

## Files

- `src/Abstractions/PublicAPI/net6.0/PublicAPI.Shipped.txt`
- `src/Abstractions/PublicAPI/net6.0/PublicAPI.Unshipped.txt`
- `src/Abstractions/PublicAPI/net8.0/PublicAPI.Shipped.txt`
- `src/Abstractions/PublicAPI/net8.0/PublicAPI.Unshipped.txt`
- `src/PublicAPI/net6.0/PublicAPI.Shipped.txt`
- `src/PublicAPI/net6.0/PublicAPI.Unshipped.txt`
- `src/PublicAPI/net8.0/PublicAPI.Shipped.txt`
- `src/PublicAPI/net8.0/PublicAPI.Unshipped.txt`

`Shipped` files describe the APIs released in the latest NuGet version. `Unshipped` files accumulate new APIs until the next release.

## Workflow

1. Modify code.
2. Run `dotnet build` or `dotnet test`.
3. If RS0016 / RS0026 warnings appear, update the baseline using the helper script:

   ```powershell
   ./tools/Update-PublicApiBaselines.ps1 -Project src/Abstractions/Lidarr.Plugin.Abstractions.csproj
   ./tools/Update-PublicApiBaselines.ps1 -Project src/Lidarr.Plugin.Common.csproj
   ```

4. Review the diff to ensure only intentional APIs are added or removed.
5. Keep the `PublicAPI/<tfm>/` files committed to source control so CI and downstream builds stay aligned.
6. Move entries from `Unshipped` to `Shipped` during release prep.

## Policy

- Adding APIs: update Unshipped and document usage in the relevant reference/how-to guide.
- Removing/changing APIs: requires a major version bump and an entry in [`migration/BREAKING_CHANGES.md`](../migration/BREAKING_CHANGES.md).
- CI fails if baselines are out of date or if undocumented breaking changes slip in.

## Related docs

- [Architecture](../concepts/ARCHITECTURE.md)
- [Release policy](../dev-guide/RELEASE_POLICY.md)
- [Docs tooling](../dev-guide/TESTING_DOCS.md)
