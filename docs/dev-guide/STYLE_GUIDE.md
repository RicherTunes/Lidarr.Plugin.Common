# Documentation Style Guide

Consistent docs are easier to maintain and automate. Follow these rules for every update.

This guide applies to all Lidarr streaming plugins (Brainarr, Tidalarr, Qobuzarr) and the shared library.

## Tone

- Use active voice and imperatives ("Do X" instead of "You should").
- Avoid filler words such as *just*, *simply*, *obviously*.
- Prefer short paragraphs and bulleted steps.

## File Naming

### Conventions

| Type | Format | Example |
|------|--------|---------|
| User docs | `UPPER_CASE.md` | `GETTING_STARTED.md` |
| Reference docs | `lowercase.md` | `configuration.md` |
| Tutorials | `UPPER_CASE.md` | `BUILD_YOUR_FIRST_PLUGIN.md` |
| Archives | `UPPER_CASE.md` | `TECH_DEBT_ANALYSIS.md` |

### Standard File Names

Use these names consistently across plugins:

- `README.md` - Documentation index
- `GETTING_STARTED.md` or `USER_SETUP_GUIDE.md` - Initial setup
- `CONFIGURATION.md` - Settings reference
- `TROUBLESHOOTING.md` - Common issues
- `UPGRADE_GUIDE.md` - Version upgrades
- `IS_IT_WORKING.md` - Verification checklist

## Structure

- Each topic lives in a single page. Link to another page instead of duplicating content.
- Use headings with sentence case (`## Host responsibilities`).
- One H1 per document (the title).
- Don't skip heading levels (H2 → H4).

### Heading Hierarchy

```markdown
# Document Title (H1) - Only one per document

## Major Section (H2)

### Subsection (H3)

#### Minor Subsection (H4) - Use sparingly
```

## Code Blocks

Code blocks must include a language hint:

| Language | Tag |
|----------|-----|
| Bash/Shell | `bash` |
| PowerShell | `powershell` |
| C# | `csharp` |
| JSON | `json` |
| YAML | `yaml` |
| XML | `xml` |

### Snippets

- All code snippets must come from tagged sections in source files (see [Doc testing](TESTING_DOCS.md)).
- Never paste hand-maintained code into docs.

## Text Formatting

| Style | Use For | Example |
|-------|---------|---------|
| **Bold** | UI elements, important terms | Click **Settings** |
| *Italic* | Emphasis, first use of terms | This is *optional* |
| `Code` | Settings, values, commands | Set `enabled` to `true` |

### UI References

Always bold UI elements and use exact text:

```markdown
# Good
Go to **Settings > Indexers > Add**

# Bad
Go to settings, then indexers, then add
```

## Notes and Warnings

Highlight pitfalls with block quotes:

```markdown
> **Note:** General information.

> **Tip:** Helpful suggestion.

> **Warning:** Could cause problems.

> **Pitfall:** `Assembly.Load` by name drops the assembly into the default ALC.
```

## Tables

### Standard Format

```markdown
| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `name` | string | - | Display name |
```

- Always include header row
- Use `code` for setting names, values
- Use `-` for empty/not applicable cells

## References and Links

- Use relative links inside the repo (`../reference/MANIFEST.md`).
- When linking to code, point to files (e.g., `../../examples/IsolationHostSample/Program.cs`).
- For cross-repository links, use full GitHub URLs.

## Diagrams

- Use Mermaid for architecture diagrams so changes are versioned alongside code.
- Keep diagrams inside the owning page; reference them via links instead of duplicating images.
- Store image files in `docs/assets/` directories.

## Terminology

Use consistent terms. See [GLOSSARY.md](../GLOSSARY.md) for definitions.

### Key Terms

| Use | Don't Use |
|-----|-----------|
| Audio Quality | Format, Profile |
| Indexer | Search provider |
| Download Client | Downloader |
| Import List | Recommendation engine |
| OAuth 2.0 | OAuth, OAuth2 |

## Spelling and Casing

- Product name: **Lidarr**.
- `AssemblyLoadContext`, `PluginLoadContext`, `IPlugin` use exact casing.
- Use American English spelling.

## Version References

### Do

```markdown
Requires Lidarr 2.14.2 or newer.
As of November 2025, the nightly branch is required.
```

### Don't

```markdown
New in v0.0.12  # Version will become stale
This will take 2-3 weeks  # No timelines
```

## Archive Policy

When archiving documents:

1. Move to `docs/archive/`
2. Add entry to `docs/archive/README.md`
3. Remove from main README index
4. Add note explaining why archived

## Checklist Before Merging

- [ ] Updated or confirmed relevant docs.
- [ ] Ran docs CI locally (`dotnet run --project tools/DocTools/SnippetVerifier`).
- [ ] Added/updated diagrams or tables when contracts changed.
- [ ] Cross-linked new pages in [`docs/README.md`](../README.md).
- [ ] Used consistent terminology from [GLOSSARY.md](../GLOSSARY.md).
