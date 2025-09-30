# Documentation Style Guide

Consistent docs are easier to maintain and automate. Follow these rules for every update.

## Tone

- Use active voice and imperatives ("Do X" instead of "You should").
- Avoid filler words such as *just*, *simply*, *obviously*.
- Prefer short paragraphs and bulleted steps.

## Structure

- Each topic lives in a single page. Link to another page instead of duplicating content.
- Use headings with sentence case (`## Host responsibilities`).
- Code blocks must include a language hint (` ```csharp `).
- Highlight pitfalls with block quotes:
  > **Pitfall:** `Assembly.Load` by name drops the assembly into the default ALC.

## Diagrams

- Use Mermaid for architecture diagrams so changes are versioned alongside code.
- Keep diagrams inside the owning page; reference them via links instead of duplicating images.

## References and links

- Use relative links inside the repo (`../reference/MANIFEST.md`).
- When linking to code, point to files (e.g., `../../examples/IsolationHostSample/Program.cs`).

## Snippets

- All code snippets must come from tagged sections in source files (see [Doc testing](TESTING_DOCS.md)).
- Never paste hand-maintained code into docs.

## Spelling and casing

- Product name: **Lidarr**.
- `AssemblyLoadContext`, `PluginLoadContext`, `IPlugin` use exact casing.
- Use American English spelling.

## Checklist before merging

- [ ] Updated or confirmed relevant docs.
- [ ] Ran docs CI locally (`dotnet run --project tools/DocTools/SnippetVerifier`).
- [ ] Added/updated diagrams or tables when contracts changed.
- [ ] Cross-linked new pages in [`docs/README.md`](../README.md).

