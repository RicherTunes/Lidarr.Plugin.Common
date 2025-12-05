// Centralized Playwright screenshotter for Lidarr streaming plugins
// Usage: node snap.mjs --plugin=Tidalarr --type=indexer,download-client
// Requires: `npx playwright install --with-deps chromium`

import { chromium } from 'playwright';
import { parseArgs } from 'node:util';
import { mkdirSync, existsSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

// Parse command line arguments
const { values: args } = parseArgs({
  options: {
    plugin: { type: 'string', default: process.env.PLUGIN_NAME || 'Plugin' },
    type: { type: 'string', default: 'indexer,download-client,import-list' },
    output: { type: 'string', default: process.env.OUTPUT_DIR || 'docs/assets/screenshots' },
    url: { type: 'string', default: process.env.LIDARR_BASE_URL || 'http://localhost:8686' },
    help: { type: 'boolean', short: 'h', default: false }
  }
});

if (args.help) {
  console.log(`
Lidarr Plugin Screenshot Utility

Usage: node snap.mjs [options]

Options:
  --plugin=NAME       Plugin name to search for (default: $PLUGIN_NAME or 'Plugin')
  --type=TYPES        Comma-separated plugin types: indexer,download-client,import-list
  --output=DIR        Output directory for screenshots (default: docs/assets/screenshots)
  --url=URL           Lidarr base URL (default: $LIDARR_BASE_URL or http://localhost:8686)
  -h, --help          Show this help message

Examples:
  node snap.mjs --plugin=Tidalarr --type=indexer,download-client
  node snap.mjs --plugin=Brainarr --type=import-list
  PLUGIN_NAME=Qobuzarr node snap.mjs
`);
  process.exit(0);
}

const PLUGIN_NAME = args.plugin;
const PLUGIN_TYPES = args.type.split(',').map(t => t.trim().toLowerCase());
const OUTDIR = args.output;
const BASE = args.url;

console.log(`Screenshot config:
  Plugin: ${PLUGIN_NAME}
  Types: ${PLUGIN_TYPES.join(', ')}
  Output: ${OUTDIR}
  URL: ${BASE}
`);

// Ensure output directory exists
if (!existsSync(OUTDIR)) {
  mkdirSync(OUTDIR, { recursive: true });
}

async function screenshotOrSkip(page, name, fn) {
  try {
    await fn();
    const path = `${OUTDIR}/${name}.png`;
    await page.screenshot({ path, fullPage: true });
    console.log(`saved: ${path}`);
  } catch (err) {
    console.warn(`skip ${name}: ${err?.message || err}`);
  }
}


// Helper to open add modal and find plugin card
async function openAddModalAndFindPlugin(page, pluginName) {
  await page.waitForTimeout(500);

  // Try multiple selectors for the add card/button (Lidarr uses various patterns)
  const addSelectors = [
    // Lidarr import list specific patterns
    '[class*="ImportList"] [class*="add" i]',
    '[class*="importList"] [class*="add" i]',
    // Card-style add buttons (div/a with + icon) - case insensitive
    '[class*="AddNew" i]',
    '[class*="addListItem" i]',
    '[class*="ListItemAdd" i]',
    // Generic card with + icon
    '[class*="Card"]:has-text("+")',
    '[class*="card"]:has-text("+")',
    '[class*="Poster"]:has-text("+")',
    // The + icon itself or its container
    'div:has(> [class*="icon" i]:has-text("+"))',
    // Button-style add
    'button:has-text("Add")',
    'button:has-text("+")',
  ];

  console.log('Searching for add button/card...');

  // Debug: Log elements with "+" text
  const plusElements = await page.locator('*:has-text("+")').evaluateAll(els =>
    els.slice(0, 10).map(el => ({
      tag: el.tagName,
      className: el.className,
      text: el.textContent?.substring(0, 50)
    }))
  ).catch(() => []);
  console.log('Elements with "+":', JSON.stringify(plusElements, null, 2));

  let clicked = false;
  for (const selector of addSelectors) {
    const addBtn = page.locator(selector).first();
    const count = await addBtn.count().catch(() => 0);
    if (count > 0) {
      console.log(`Found add element with selector: ${selector}`);
      try {
        await addBtn.click({ timeout: 3000 });
        await page.waitForTimeout(800);
        clicked = true;
        break;
      } catch (e) {
        console.log(`Click failed for ${selector}: ${e?.message || e}`);
      }
    }
  }

  if (!clicked) {
    console.log('Could not find add button/card with any selector');
  }

  // Wait for modal to appear
  const modal = page.locator('[class*="ModalContent"], [class*="modalContent"], [class*="Modal-content"]').first();
  const modalVisible = await modal.isVisible({ timeout: 5000 }).catch(() => false);

  if (!modalVisible) {
    console.log('No modal appeared after clicking add button');
    return { modal: null, found: false };
  }

  console.log('Modal opened, looking for plugin card...');

  // Try to use modal-specific filter/search if available (NOT global search bar)
  const modalSearch = modal.locator('input[type="text"], input[placeholder*="filter" i], input[placeholder*="search" i]').first();
  if (await modalSearch.count().catch(() => 0)) {
    console.log('Found modal-specific search/filter, using it...');
    await modalSearch.fill(pluginName).catch(() => {});
    await page.waitForTimeout(500);
  }

  return { modal, found: true };
}

// Helper to click plugin card in add modal and wait for config dialog
async function clickPluginCard(page, pluginName, modal = null) {
  await page.waitForTimeout(500);

  // If no modal provided, try to find one
  if (!modal) {
    modal = page.locator('[class*="ModalContent"], [class*="modalContent"], [class*="Modal-content"]').first();
  }

  const modalVisible = await modal.isVisible().catch(() => false);

  if (!modalVisible) {
    console.log('No modal detected, cannot click plugin card');
    return false;
  }

  console.log('Modal detected, searching for plugin card...');

  // Selectors scoped to modal content only
  const selectors = [
    // Standard Lidarr plugin card selectors
    `div[class*="AddNewItem"]:has-text("${pluginName}")`,
    `div[class*="selectableCard"]:has-text("${pluginName}")`,
    `div[class*="card"]:has-text("${pluginName}")`,
    `div[class*="Card"]:has-text("${pluginName}")`,
    // Link-style cards
    `a[class*="card"]:has-text("${pluginName}")`,
    `a[class*="Card"]:has-text("${pluginName}")`,
  ];

  for (const selector of selectors) {
    // Scope to modal only
    const card = modal.locator(selector).first();
    const count = await card.count().catch(() => 0);
    console.log(`Checking modal selector: ${selector} - found: ${count}`);
    if (count > 0) {
      try {
        // Scroll card into view first
        await card.scrollIntoViewIfNeeded({ timeout: 2000 }).catch(() => {});
        await page.waitForTimeout(300);
        await card.click({ timeout: 3000 });
        await page.waitForTimeout(1500);
        // Check if config dialog opened (should have form inputs)
        const hasForm = await page.locator('[class*="Modal"] input[name], [class*="Modal"] select, [class*="Modal"] textarea').count().catch(() => 0);
        if (hasForm > 0) {
          console.log(`Clicked plugin card using: ${selector}, form fields found: ${hasForm}`);
          return true;
        }
      } catch (e) {
        console.log(`Click failed for ${selector}: ${e?.message || e}`);
      }
    }
  }

  // Fallback: look for exact text match within modal and scroll to it
  const exactMatch = modal.locator('div, span, h3, h4').filter({ hasText: new RegExp(`^${pluginName}$`, 'i') }).first();
  if (await exactMatch.count().catch(() => 0)) {
    console.log('Found exact text match within modal, scrolling and clicking...');
    await exactMatch.scrollIntoViewIfNeeded({ timeout: 2000 }).catch(() => {});
    await page.waitForTimeout(300);
    // Click the parent card element, not just the text
    const parentCard = exactMatch.locator('xpath=ancestor::div[contains(@class, "card") or contains(@class, "Card") or contains(@class, "AddNewItem")]').first();
    if (await parentCard.count().catch(() => 0)) {
      await parentCard.click({ timeout: 3000 }).catch(() => {});
    } else {
      await exactMatch.click({ timeout: 3000 }).catch(() => {});
    }
    await page.waitForTimeout(1500);
    return true;
  }

  console.log('No plugin card found in modal');
  return false;
}

async function captureIndexerScreenshots(page) {
  // Navigate to Indexers section
  await screenshotOrSkip(page, 'indexers-list', async () => {
    const indexersLink = page.getByRole('link', { name: /indexers/i }).first();
    if (await indexersLink.count()) {
      await indexersLink.click({ timeout: 3000 }).catch(() => {});
      await page.waitForLoadState('networkidle', { timeout: 10_000 }).catch(() => {});
    } else {
      await page.goto(`${BASE}/settings/indexers`, { waitUntil: 'domcontentloaded' });
    }
    await page.waitForTimeout(500);
  });

  // Open Add Indexer modal and find plugin card (for add-modal screenshot)
  let modalRef = null;
  await screenshotOrSkip(page, 'indexer-add-modal', async () => {
    const result = await openAddModalAndFindPlugin(page, PLUGIN_NAME);
    modalRef = result.modal;
    // Screenshot shows the modal with plugin card visible (scrolled into view if needed)
    if (modalRef) {
      await clickPluginCard(page, PLUGIN_NAME, modalRef).catch(() => {});
      // After clicking, we're now in config - go back for add-modal screenshot
      await page.keyboard.press('Escape');
      await page.waitForTimeout(300);
      // Re-open modal for screenshot
      const addBtn = page.locator('button').filter({ hasText: /add/i }).first();
      if (await addBtn.count().catch(() => 0)) {
        await addBtn.click({ timeout: 2000 }).catch(() => {});
        await page.waitForTimeout(500);
      }
    }
  });

  // Plugin configuration - click the plugin card to open config modal
  await screenshotOrSkip(page, 'indexer-config', async () => {
    const clicked = await clickPluginCard(page, PLUGIN_NAME);
    if (!clicked) {
      // Try opening modal fresh
      const result = await openAddModalAndFindPlugin(page, PLUGIN_NAME);
      if (result.modal) {
        await clickPluginCard(page, PLUGIN_NAME, result.modal);
      }
    }
  });

  await page.keyboard.press('Escape');
  await page.waitForTimeout(300);
}

async function captureDownloadClientScreenshots(page) {
  // Navigate to Download Clients section
  await screenshotOrSkip(page, 'download-clients-list', async () => {
    const downloadLink = page.getByRole('link', { name: /download client/i }).first();
    if (await downloadLink.count()) {
      await downloadLink.click({ timeout: 3000 }).catch(() => {});
      await page.waitForLoadState('networkidle', { timeout: 10_000 }).catch(() => {});
    } else {
      await page.goto(`${BASE}/settings/downloadclients`, { waitUntil: 'domcontentloaded' });
    }
    await page.waitForTimeout(500);
  });

  // Open Add Download Client modal and find plugin card
  let modalRef = null;
  await screenshotOrSkip(page, 'download-client-add-modal', async () => {
    const result = await openAddModalAndFindPlugin(page, PLUGIN_NAME);
    modalRef = result.modal;
    if (modalRef) {
      await clickPluginCard(page, PLUGIN_NAME, modalRef).catch(() => {});
      await page.keyboard.press('Escape');
      await page.waitForTimeout(300);
      const addBtn = page.locator('button').filter({ hasText: /add/i }).first();
      if (await addBtn.count().catch(() => 0)) {
        await addBtn.click({ timeout: 2000 }).catch(() => {});
        await page.waitForTimeout(500);
      }
    }
  });

  // Plugin configuration - click the plugin card to open config modal
  await screenshotOrSkip(page, 'download-client-config', async () => {
    const clicked = await clickPluginCard(page, PLUGIN_NAME);
    if (!clicked) {
      const result = await openAddModalAndFindPlugin(page, PLUGIN_NAME);
      if (result.modal) {
        await clickPluginCard(page, PLUGIN_NAME, result.modal);
      }
    }
  });

  await page.keyboard.press('Escape');
  await page.waitForTimeout(300);
}

async function captureImportListScreenshots(page) {
  // Navigate to Import Lists section
  await screenshotOrSkip(page, 'import-lists', async () => {
    const importLists = page.getByText(/import lists/i).first();
    if (await importLists.count()) {
      await importLists.click({ timeout: 2000 }).catch(() => {});
    } else {
      await page.goto(`${BASE}/settings/importlists`, { waitUntil: 'domcontentloaded' });
    }
    await page.waitForLoadState('networkidle', { timeout: 10_000 }).catch(() => {});
    await page.waitForTimeout(500);
  });

  // Open Add Import List modal and find plugin card
  let modalRef = null;
  await screenshotOrSkip(page, 'import-list-add-modal', async () => {
    const result = await openAddModalAndFindPlugin(page, PLUGIN_NAME);
    modalRef = result.modal;
    if (modalRef) {
      await clickPluginCard(page, PLUGIN_NAME, modalRef).catch(() => {});
      await page.keyboard.press('Escape');
      await page.waitForTimeout(300);
      const addBtn = page.locator('button').filter({ hasText: /add/i }).first();
      if (await addBtn.count().catch(() => 0)) {
        await addBtn.click({ timeout: 2000 }).catch(() => {});
        await page.waitForTimeout(500);
      }
    }
  });

  // Plugin configuration - click the plugin card to open config modal
  await screenshotOrSkip(page, 'import-list-config', async () => {
    const clicked = await clickPluginCard(page, PLUGIN_NAME);
    if (!clicked) {
      const result = await openAddModalAndFindPlugin(page, PLUGIN_NAME);
      if (result.modal) {
        await clickPluginCard(page, PLUGIN_NAME, result.modal);
      }
    }
  });

  await page.keyboard.press('Escape');
  await page.waitForTimeout(300);
}

// Helper to enable "Show Advanced" settings toggle
async function enableShowAdvanced(page) {
  try {
    // Look for the "Show Advanced" button in the toolbar
    const showAdvancedBtn = page.locator('button').filter({ hasText: /show\s*advanced/i }).first();
    if (await showAdvancedBtn.count()) {
      // Check if it's already enabled (button text might change or have different state)
      const btnText = await showAdvancedBtn.textContent().catch(() => '');
      if (btnText && !btnText.toLowerCase().includes('hide')) {
        await showAdvancedBtn.click({ timeout: 2000 }).catch(() => {});
        await page.waitForTimeout(300);
        console.log('Enabled "Show Advanced" settings');
      }
    }
  } catch (err) {
    console.warn(`Could not enable Show Advanced: ${err?.message || err}`);
  }
}

async function run() {
  const browser = await chromium.launch({ headless: true });
  try {
    const context = await browser.newContext({
      viewport: { width: 1440, height: 900 },
      userAgent: `${PLUGIN_NAME.toLowerCase()}-ci-screenshot`,
      colorScheme: 'dark'
    });
    const page = await context.newPage();

    // Basic navigation + wizard-friendly waits
    await page.goto(BASE, { waitUntil: 'domcontentloaded', timeout: 60_000 });
    await page.waitForLoadState('networkidle', { timeout: 60_000 }).catch(() => {});

    // Try to breeze through wizard if present
    const tryClick = async (text) => {
      const el = page.getByRole('button', { name: text });
      if (await el.count().catch(() => 0)) {
        await el.first().click({ timeout: 2000 }).catch(() => {});
      }
    };
    await tryClick('Next');
    await tryClick('Continue');
    await tryClick('Skip');
    await tryClick('Finish');

    // Landing page
    await screenshotOrSkip(page, 'landing', async () => {
      await page.waitForTimeout(800);
    });

    // Navigate to Settings
    const goSettings = async () => {
      const settings = page.getByRole('link', { name: /settings/i });
      if (await settings.count()) {
        await settings.first().click();
        await page.waitForLoadState('networkidle', { timeout: 10_000 }).catch(() => {});
      } else {
        await page.goto(`${BASE}/settings`, { waitUntil: 'domcontentloaded' });
        await page.waitForLoadState('networkidle', { timeout: 10_000 }).catch(() => {});
      }
      // Enable Show Advanced after navigating to settings
      await enableShowAdvanced(page);
    };

    await goSettings();

    // Settings overview
    await screenshotOrSkip(page, 'settings', async () => {
      await page.waitForTimeout(500);
    });

    // Capture type-specific screenshots
    if (PLUGIN_TYPES.includes('indexer')) {
      await captureIndexerScreenshots(page);
      await goSettings();
    }

    if (PLUGIN_TYPES.includes('download-client')) {
      await captureDownloadClientScreenshots(page);
      await goSettings();
    }

    if (PLUGIN_TYPES.includes('import-list')) {
      await captureImportListScreenshots(page);
    }

    console.log('\nScreenshot capture complete!');
  } finally {
    await browser.close();
  }
}

run().catch((e) => {
  console.error(e);
  process.exitCode = 1;
});
