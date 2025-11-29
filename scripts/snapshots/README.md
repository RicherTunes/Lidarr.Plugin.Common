# Lidarr Plugin Screenshot Utility

Centralized Playwright-based screenshot automation for Lidarr streaming plugins.

## Overview

This utility captures UI screenshots of Lidarr plugin configurations for documentation. It supports all plugin types:
- **Indexers** (Tidalarr, Qobuzarr)
- **Download Clients** (Tidalarr, Qobuzarr)
- **Import Lists** (Brainarr)

## Installation

```bash
# Install Playwright
npm i -D playwright

# Install Chromium browser
npx playwright install --with-deps chromium
```

## Usage

### Command Line

```bash
# Basic usage - specify plugin name and types
node snap.mjs --plugin=Tidalarr --type=indexer,download-client

# Brainarr (import list only)
node snap.mjs --plugin=Brainarr --type=import-list

# Custom output directory
node snap.mjs --plugin=Qobuzarr --output=./screenshots

# Custom Lidarr URL
node snap.mjs --plugin=Tidalarr --url=http://192.168.1.100:8686
```

### Environment Variables

```bash
# Alternative configuration via environment
export PLUGIN_NAME=Tidalarr
export LIDARR_BASE_URL=http://localhost:8686
export OUTPUT_DIR=docs/assets/screenshots
node snap.mjs
```

### From Plugin Repositories

Plugins can reference this centralized script:

```bash
# From Tidalarr repo
node ../Lidarr.Plugin.Common/scripts/snapshots/snap.mjs \
  --plugin=Tidalarr \
  --type=indexer,download-client \
  --output=docs/assets/screenshots
```

Or create a thin wrapper:

```javascript
// scripts/snapshots/snap.mjs
import { execSync } from 'child_process';
execSync('node ../../ext/lidarr.plugin.common/scripts/snapshots/snap.mjs ' +
  '--plugin=Tidalarr --type=indexer,download-client', { stdio: 'inherit' });
```

## Generated Screenshots

| Screenshot | Plugin Types | Description |
|------------|--------------|-------------|
| `landing.png` | All | Lidarr home page |
| `settings.png` | All | Settings overview |
| `indexers-list.png` | indexer | Indexers section |
| `indexer-add-modal.png` | indexer | Add Indexer modal with search |
| `indexer-config.png` | indexer | Indexer configuration form |
| `download-clients-list.png` | download-client | Download Clients section |
| `download-client-add-modal.png` | download-client | Add Download Client modal |
| `download-client-config.png` | download-client | Download Client configuration |
| `import-lists.png` | import-list | Import Lists section |
| `import-list-add-modal.png` | import-list | Add Import List modal |
| `import-list-config.png` | import-list | Import List configuration |

## CI/CD Integration

### GitHub Actions Workflow Template

```yaml
name: UI Screenshots

on:
  workflow_dispatch:
  schedule:
    - cron: '0 7 * * 0'  # Weekly
  push:
    branches: [ main ]
    paths:
      - 'src/**'
      - 'scripts/snapshots/**'

jobs:
  screenshots:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with:
          submodules: recursive

      - name: Setup Node
        uses: actions/setup-node@v5
        with:
          node-version: '20'

      - name: Install Playwright
        run: |
          npm i -D playwright@1.48.2
          npx playwright install --with-deps chromium

      # ... (build and start Lidarr with plugin)

      - name: Capture Screenshots
        run: |
          node ext/lidarr.plugin.common/scripts/snapshots/snap.mjs \
            --plugin=${{ env.PLUGIN_NAME }} \
            --type=${{ env.PLUGIN_TYPES }} \
            --output=docs/assets/screenshots

      - name: Commit Screenshots
        run: |
          git config user.name "github-actions[bot]"
          git config user.email "github-actions[bot]@users.noreply.github.com"
          git add docs/assets/screenshots/*.png
          git diff --cached --quiet || git commit -m "docs: refresh screenshots"
          git push
```

## Plugin Configuration

| Plugin | Types | Example Command |
|--------|-------|-----------------|
| Tidalarr | indexer, download-client | `--plugin=Tidalarr --type=indexer,download-client` |
| Qobuzarr | indexer, download-client | `--plugin=Qobuzarr --type=indexer,download-client` |
| Brainarr | import-list | `--plugin=Brainarr --type=import-list` |

## Troubleshooting

### Plugin not found in modal

- Verify the plugin is installed and Lidarr has been restarted
- Check the plugin name matches exactly (case-insensitive search)
- Ensure the plugin type matches what's being searched

### Timeout errors

- Increase network timeout in Lidarr configuration
- Check if Lidarr is fully loaded (not showing setup wizard)
- Verify the URL is accessible

### Empty or blank screenshots

- Check Lidarr is running and accessible
- Verify no authentication is blocking access
- Check browser console for JavaScript errors

## Contributing

To add support for new screenshot types:

1. Add a new `capture*Screenshots` function
2. Update the `--type` parser to recognize the new type
3. Document the new screenshots in this README
