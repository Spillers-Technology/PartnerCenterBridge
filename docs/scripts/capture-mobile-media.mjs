#!/usr/bin/env node
// Mobile/desktop verification matrix: screenshots every current view across 5 device profiles
// with a fully mocked API (no backend needed) and asserts no page-level horizontal overflow
// before writing each screenshot -- the assertion is the actual regression-catching mechanism,
// the screenshot is secondary evidence for human review.
//
//   cd web && npm run dev
//   node docs/scripts/capture-mobile-media.mjs
//
// Filter while iterating:
//   PCBRIDGE_CAPTURE_DEVICES=galaxy,fold-closed PCBRIDGE_CAPTURE_VIEWS=dashboard,tenants node docs/scripts/capture-mobile-media.mjs
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { installApiMock, freezeAnimations, loadPlaywright, waitForServer } from "./mock-api.mjs";

const repoRoot = path.resolve(fileURLToPath(new URL("..", import.meta.url)), "..");
const outDir = process.env.PCBRIDGE_CAPTURE_OUT || path.join(repoRoot, "docs", "assets", "screenshots", "mobile");
const baseUrl = process.env.PCBRIDGE_CAPTURE_BASE_URL || "http://127.0.0.1:5173";
const debugCapture = process.env.PCBRIDGE_CAPTURE_DEBUG === "1";
const deviceFilter = process.env.PCBRIDGE_CAPTURE_DEVICES?.split(",").map((s) => s.trim());
const viewFilter = process.env.PCBRIDGE_CAPTURE_VIEWS?.split(",").map((s) => s.trim());

// ---------------------------------------------------------------------------
// Device matrix -- five profiles matching AnchorDesk's mobile verification matrix.
// ---------------------------------------------------------------------------

function buildDevices(playwrightDevices) {
  return [
    { name: "galaxy", ...playwrightDevices["Galaxy S9+"] },
    { name: "iphone", ...playwrightDevices["iPhone 15"] },
    { name: "pixel", ...playwrightDevices["Pixel 7"] },
    {
      name: "fold-closed",
      viewport: { width: 344, height: 882 },
      isMobile: true,
      hasTouch: true,
      deviceScaleFactor: 2,
      userAgent: playwrightDevices["Galaxy S9+"].userAgent,
    },
    {
      name: "fold-open",
      viewport: { width: 717, height: 512 },
      isMobile: true,
      hasTouch: true,
      deviceScaleFactor: 2,
      userAgent: playwrightDevices["Galaxy S9+"].userAgent,
    },
  ];
}

// ---------------------------------------------------------------------------
// Overflow assertion -- the actual regression check.
// ---------------------------------------------------------------------------

let overflowFailures = 0;

async function assertNoOverflow(page, label) {
  const { scrollWidth, innerWidth } = await page.evaluate(() => ({
    scrollWidth: document.documentElement.scrollWidth,
    innerWidth: window.innerWidth,
  }));
  if (scrollWidth > innerWidth) {
    overflowFailures++;
    console.error(`  OVERFLOW: ${label} -- content ${scrollWidth}px wide in a ${innerWidth}px viewport`);
  } else if (debugCapture) {
    console.log(`  ok: ${label} (${scrollWidth}px in ${innerWidth}px)`);
  }
}

async function captureView(page, device, viewName) {
  await assertNoOverflow(page, `${viewName}-${device.name}`);
  await page.screenshot({ path: path.join(outDir, `${viewName}-${device.name}.jpg`), type: "jpeg", quality: 92 });
}

// ---------------------------------------------------------------------------
// View steps -- each takes the authenticated `page` (already navigated to baseUrl,
// already mocked) and drives it to one view's resting/landing state.
// Tasks 3-4 add more entries to this object; nothing else in the file changes shape.
// ---------------------------------------------------------------------------

async function gotoTab(page, label) {
  // On mobile (xs), the nav is in a Drawer; on desktop/tablet, it's Tabs.
  // Try to find and click the nav button/tab for this label.
  
  // First check if there's a "Open navigation" button (drawer pattern).
  const navButton = page.locator('button[aria-label*="navigation"], button[aria-label*="Navigation"]').first();
  const isDrawerVisible = await navButton.isVisible({ timeout: 1000 }).catch(() => false);
  
  if (isDrawerVisible) {
    // Mobile drawer: open it and click the nav item
    await navButton.click();
    // Click the nav item by text (works with MUI ListItem which renders clickable list items)
    await page.locator("a, button, [role=button], [role=menuitem]").filter({ hasText: new RegExp(`^${label}$`, "i") }).first().click({ timeout: 10000 });
  } else {
    // Desktop Tabs: click the tab directly
    await page.getByRole("tab", { name: label, exact: true }).click({ timeout: 10000 });
  }
}

const AUTHENTICATED_VIEWS = {
  dashboard: async (page) => {
    await page.getByText("Needs attention", { exact: false }).waitFor({ timeout: 20_000 });
  },
  tenants: async (page) => {
    await gotoTab(page, "Tenants");
    await page.getByText("Wingtip Partners", { exact: false }).waitFor({ timeout: 20_000 });
  },
  finduser: async (page) => {
    await gotoTab(page, "Find User");
    await page.getByPlaceholder(/Name or UPN/).waitFor({ timeout: 20_000 });
  },
};

async function main() {
  fs.mkdirSync(outDir, { recursive: true });
  const { chromium, devices } = loadPlaywright();
  const DEVICES = buildDevices(devices).filter((d) => !deviceFilter || deviceFilter.includes(d.name));
  const views = Object.entries(AUTHENTICATED_VIEWS).filter(([name]) => !viewFilter || viewFilter.includes(name));

  console.log(`Using Partner Center Bridge SPA at ${baseUrl}...`);
  await waitForServer(baseUrl);

  let browser;
  try {
    browser = await chromium.launch({ headless: true });
    for (const device of DEVICES) {
      console.log(`Device: ${device.name} (${device.viewport.width}x${device.viewport.height})`);
      const page = await browser.newPage({ ...device });
      if (debugCapture) {
        page.on("console", (m) => console.log(`BROWSER ${m.type()}: ${m.text()}`));
        page.on("pageerror", (e) => console.log(`BROWSER pageerror: ${e.message}`));
      }
      await installApiMock(page);
      await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
      await freezeAnimations(page);

      for (const [viewName, drive] of views) {
        console.log(`  ${viewName}...`);
        await drive(page);
        await captureView(page, device, viewName);
      }
      await page.close();
    }
  } finally {
    if (browser) await browser.close();
  }

  console.log(`\nCaptured screenshots in ${path.relative(repoRoot, outDir)}`);
  if (overflowFailures > 0) {
    console.error(`\n${overflowFailures} view/device pair(s) had page-level horizontal overflow -- see OVERFLOW lines above.`);
    process.exitCode = 1;
  }
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
