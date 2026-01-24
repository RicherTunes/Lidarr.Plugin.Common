# Lidarr.Plugin.Common - Templates

This directory contains standardized configuration templates for Lidarr plugins.
Copy these files to your plugin repository root to ensure consistent configuration
across all plugins (Brainarr, Qobuzarr, Tidalarr, etc.).

## Available Templates

### Code Quality

| File | Purpose |
|------|---------|
| `.editorconfig` | Unified code style and analyzer settings |
| `.markdownlint.yml` | Markdown linting rules |
| `.pre-commit-config.yaml` | Pre-commit hooks for quality checks |

### Security

| File | Purpose |
|------|---------|
| `.gitleaks.toml` | Secret detection configuration |

### Documentation

| File | Purpose |
|------|---------|
| `.lychee.toml` | Link checker configuration |

### Build

| File | Purpose |
|------|---------|
| `Directory.Packages.props` | Central Package Management with aligned versions |

## Usage

### Quick Setup

```bash
# From your plugin repository root
cp ext/Lidarr.Plugin.Common/templates/.editorconfig .
cp ext/Lidarr.Plugin.Common/templates/.lychee.toml .
cp ext/Lidarr.Plugin.Common/templates/.markdownlint.yml .
cp ext/Lidarr.Plugin.Common/templates/.gitleaks.toml .
cp ext/Lidarr.Plugin.Common/templates/.pre-commit-config.yaml .
cp ext/Lidarr.Plugin.Common/templates/Directory.Packages.props .
```

### Workflow Templates

See `.github/workflow-templates/` for reusable GitHub Actions workflows:

- `codeql.yml` - CodeQL security scanning
- `dependency-review.yml` - License and vulnerability checking
- `link-check.yml` - Documentation link validation

### Scripts

See `scripts/` directory for shared build scripts:

- `extract-lidarr-assemblies.sh` - Extract Lidarr assemblies from Docker images
- `setup-repository.sh` - Repository setup automation
- `verify-assemblies.ps1` - Assembly verification (Windows)

## Version Alignment

The `Directory.Packages.props` template defines aligned package versions:

| Package | Version | Notes |
|---------|---------|-------|
| FluentValidation | 11.11.0 | Latest stable |
| NLog | 6.0.3 | Matches Lidarr |
| Newtonsoft.Json | 13.0.4 | Latest |
| Polly | 8.5.2 | V8 resilience library |
| ILRepack | 2.0.34.2 | For plugin assembly merging |

## Customization

Templates can be customized per-plugin if needed, but prefer keeping
configuration consistent where possible. Document any deviations in
your plugin's README or CLAUDE.md.
