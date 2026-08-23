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
import { installApiMock, freezeAnimations, loadPlaywright, waitForServer, getUnmatchedRouteCount } from "./mock-api.mjs";

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

// Compares scrollWidth against the device's CONFIGURED width, never the live window.innerWidth.
// Mobile Chromium emulation can auto-widen its own reported layout viewport to fit content that
// doesn't fit the device's actual width, rather than clipping it -- so scrollWidth and
// window.innerWidth can silently converge to the same (wrong) number exactly in the overflow case
// this check exists to catch. Confirmed directly: Tenants at a 320px-wide device reports
// window.innerWidth === scrollWidth === 650 after Chromium widened its own reported viewport to
// fit the overflowing table -- comparing against the fixed, intended device width (320) correctly
// flags this; comparing against window.innerWidth does not.
// A small tolerance absorbs harmless sub-pixel rounding between Playwright's declared
// device.viewport.width and what the browser actually reports as the resting (no-overflow)
// scrollWidth at high deviceScaleFactor -- confirmed directly (not assumed): Galaxy S9+ at a
// clean, non-overflowing Dashboard render measured exactly 1px above its declared 320px width, so
// the tolerance is set to that exact observed value, not a rounder/looser guess. Real overflow in
// this matrix measures in the hundreds of pixels, far outside this tolerance.
const OVERFLOW_TOLERANCE_PX = 1;

async function assertNoOverflow(page, label, expectedWidth) {
  const scrollWidth = await page.evaluate(() => document.documentElement.scrollWidth);
  if (scrollWidth > expectedWidth + OVERFLOW_TOLERANCE_PX) {
    overflowFailures++;
    console.error(`  OVERFLOW: ${label} -- content ${scrollWidth}px wide, device is ${expectedWidth}px`);
    if (debugCapture) {
      const offenders = await page.evaluate(({ width, tolerance }) =>
        [...document.querySelectorAll("body *")]
          .map((element) => {
            const rect = element.getBoundingClientRect();
            const style = getComputedStyle(element);
            return { tag: element.tagName, className: String(element.className), left: Math.round(rect.left), right: Math.round(rect.right), width: Math.round(rect.width), position: style.position, overflowX: style.overflowX };
          })
          .filter((rect) => rect.right > width + tolerance || rect.left < -tolerance)
          .sort((a, b) => a.right - b.right)
          .slice(0, 12),
        { width: expectedWidth, tolerance: OVERFLOW_TOLERANCE_PX }
      );
      console.error(`  overflow elements: ${JSON.stringify(offenders)}`);
      const pageMetrics = await page.evaluate(() => ({
        innerWidth: window.innerWidth,
        htmlClientWidth: document.documentElement.clientWidth,
        htmlScrollWidth: document.documentElement.scrollWidth,
        bodyClientWidth: document.body.clientWidth,
        bodyScrollWidth: document.body.scrollWidth,
        bodyMargin: getComputedStyle(document.body).margin,
        rootRect: document.getElementById("root")?.getBoundingClientRect().toJSON(),
      }));
      console.error(`  page metrics: ${JSON.stringify(pageMetrics)}`);
    }
  } else if (debugCapture) {
    console.log(`  ok: ${label} (${scrollWidth}px in ${expectedWidth}px)`);
  }
}

async function captureView(page, device, viewName) {
  await assertNoOverflow(page, `${viewName}-${device.name}`, device.viewport.width);
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
    // Mobile drawer: open it and click the nav item. Selecting an item closes the drawer
    // (AppShell's selectTab calls setDrawerOpen(false)), but MUI's Drawer unmounts its
    // backdrop/panel on close rather than just hiding it -- if the NEXT gotoTab call starts
    // before that close transition finishes, its own drawer-open click can race the still-
    // exiting previous instance and grab a list-item element that gets detached mid-click
    // (reproduced directly: a fixed-timing version without this wait threw exactly that
    // "element was detached from the DOM" error on the second navigation in a run). Wait for
    // the backdrop to actually leave the DOM instead of guessing a fixed delay.
    await navButton.click({ force: true });
    await page.locator("a, button, [role=button], [role=menuitem]").filter({ hasText: new RegExp(`^${label}$`, "i") }).first().click({ timeout: 10000 });
    // Wait for the drawer's backdrop to actually leave the DOM (selecting an item closes it)
    // before returning, so the NEXT gotoTab call's own drawer-open click can't race a still-
    // exiting previous instance. Deliberately NOT swallowed: if the backdrop is still there
    // after 2s, the drawer is genuinely stuck -- that's a real bug worth failing loudly on, not
    // silently proceeding into the exact "detached from the DOM" race this wait exists to
    // prevent (three repeated full 55-capture runs never came close to this timeout).
    await page.locator(".MuiBackdrop-root").waitFor({ state: "hidden", timeout: 2000 });
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
    // UserSearch's MUI migration associates the query field via a proper <label> (no
    // placeholder) instead of the pre-migration bare <input placeholder=...>, so wait on the
    // label text instead of a placeholder that no longer exists.
    await page.getByLabel(/Name or UPN/).waitFor({ timeout: 20_000 });
  },
  approvals: async (page) => {
    // "Mutating actions requested through MCP" is Approvals' static subtitle, present on mount
    // regardless of whether the pendingActions fetch has resolved -- a slow response could pass
    // this wait while the table (and its real overflow risk) hasn't rendered yet. Wait for
    // "workflow.remediate" instead, the actionType column value that only exists once the mocked
    // pendingActions data has actually loaded into the table.
    await gotoTab(page, "Approvals");
    await page.getByText("workflow.remediate", { exact: false }).first().waitFor({ timeout: 20_000 });
  },
  contracts: async (page) => {
    await gotoTab(page, "Contracts");
    await page.getByText("Managed Workstations", { exact: false }).waitFor({ timeout: 20_000 });
  },
  deploy: async (page) => {
    // "Deploy a template" is DeployWizard's static heading, present before the templates/tenants
    // fetch resolves. Since its MUI migration, the template picker is an MUI Select, whose
    // MenuItems aren't in the DOM at rest (only mounted once the dropdown is opened, which this
    // resting-state capture deliberately never does) -- a plain <select><option> locator no longer
    // applies. Wait for a real tenant checkbox label instead: the tenant fieldset renders
    // unconditionally once the tenants fetch resolves, in the same resting view, and is exactly
    // the wide/overflow-prone content this capture needs to have actually rendered.
    await gotoTab(page, "Deploy");
    await page.getByText("Contoso Ltd", { exact: false }).first().waitFor({ timeout: 20_000 });
  },
  history: async (page) => {
    // "Deployment history" is Deployments' static heading, present before the deployments fetch
    // resolves. Wait for a real status badge value instead -- only rendered once the mocked
    // deployments data has actually loaded into the table.
    await gotoTab(page, "History");
    await page.getByText("UpdateAvailable", { exact: false }).first().waitFor({ timeout: 20_000 });
  },
  newhire: async (page) => {
    // NewHire's own wide content (SKU/group checkboxes) is gated behind manually selecting a
    // tenant, which this capture never does -- the static "New hire" heading IS the view's
    // actual resting state here, not a false-clean risk like approvals/deploy/history above.
    await gotoTab(page, "New Hire");
    await page.getByText("New hire", { exact: true }).waitFor({ timeout: 20_000 });
  },
  offboard: async (page) => {
    // Same resting-state reasoning as newhire above. Scoped to the heading role specifically
    // (not a plain getByText().first()) because the nav tab's own label text is the identical
    // string "Offboard" -- an unscoped .first() would resolve against whichever of the two DOM
    // order happens to put first, proving nothing about the page content having rendered.
    await gotoTab(page, "Offboard");
    await page.getByRole("heading", { name: "Offboard", exact: true }).waitFor({ timeout: 20_000 });
  },
  templates: async (page) => {
    await gotoTab(page, "App Templates");
    await page.getByText("7-Zip 24.08", { exact: false }).waitFor({ timeout: 20_000 });
  },
  workflows: async (page) => {
    await gotoTab(page, "Workflows");
    await page.getByRole("button", { name: "Mailbox archive repair", exact: true }).waitFor({ timeout: 20_000 });
  },
  snapshots: async (page) => {
    // Was missing from this plan's original view list entirely (a real gap, not an
    // implementer omission) -- Config Snapshots is a regular, always-visible tab in every
    // auth mode, so it belongs here alongside the other 11, not in the Local-mode-only
    // captureAuthViews set. "jspillers" (the operator name on the mocked snapshot runs)
    // proves the tenant-scoped data fetch resolved, matching the desktop script's own
    // already-verified wait marker for this exact view.
    await gotoTab(page, "Config Snapshots");
    await page.getByText("jspillers", { exact: false }).first().waitFor({ timeout: 20_000 });
  },
};

async function main() {
  fs.mkdirSync(outDir, { recursive: true });
  const { chromium, devices } = loadPlaywright();
  const DEVICES = buildDevices(devices).filter((d) => !deviceFilter || deviceFilter.includes(d.name));
  const views = Object.entries(AUTHENTICATED_VIEWS).filter(([name]) => !viewFilter || viewFilter.includes(name));
  const AUTH_VIEW_NAMES = ["login", "register", "security"];

  // A typo'd token in PCBRIDGE_CAPTURE_DEVICES/PCBRIDGE_CAPTURE_VIEWS must not silently drop just
  // that one entry while the rest of the run reports success -- validate every requested token
  // against the full known-name set, not just check whether the overall filtered result is
  // non-empty (a filter of "dashboard,tennants" would otherwise capture dashboard alone and exit
  // 0, quietly skipping the Tenants coverage the caller actually asked for).
  if (deviceFilter) {
    const knownDeviceNames = buildDevices(devices).map((d) => d.name);
    const unknown = deviceFilter.filter((name) => !knownDeviceNames.includes(name));
    if (unknown.length > 0) {
      throw new Error(`PCBRIDGE_CAPTURE_DEVICES has unknown device(s): ${unknown.join(",")} (known: ${knownDeviceNames.join(",")})`);
    }
  }
  if (viewFilter) {
    const knownViewNames = [...Object.keys(AUTHENTICATED_VIEWS), ...AUTH_VIEW_NAMES];
    const unknown = viewFilter.filter((name) => !knownViewNames.includes(name));
    if (unknown.length > 0) {
      throw new Error(`PCBRIDGE_CAPTURE_VIEWS has unknown view(s): ${unknown.join(",")} (known: ${knownViewNames.join(",")})`);
    }
  }

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
      if (!viewFilter || AUTH_VIEW_NAMES.some((view) => viewFilter.includes(view))) {
        await captureAuthViews(browser, device);
      }
    }
  } finally {
    if (browser) await browser.close();
  }

  console.log(`\nCaptured screenshots in ${path.relative(repoRoot, outDir)}`);
  if (overflowFailures > 0) {
    console.error(`\n${overflowFailures} view/device pair(s) had page-level horizontal overflow -- see OVERFLOW lines above.`);
    process.exitCode = 1;
  }
  const unmatchedRoutes = getUnmatchedRouteCount();
  if (unmatchedRoutes > 0) {
    console.error(`\n${unmatchedRoutes} API call(s) had no mock match (returned an empty 200) -- see MOCK MISS lines above. A view relying on that data may have rendered broken or empty and falsely passed the overflow check.`);
    process.exitCode = 1;
  }
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});

async function captureAuthViews(browser, device) {
  console.log(`  [auth] Login/Register/Security on ${device.name}...`);

  const loginPage = await browser.newPage({ ...device });
  await installApiMock(loginPage, { authenticated: false, authModeOverride: "Local" });
  await loginPage.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await freezeAnimations(loginPage);
  await loginPage.getByRole("button", { name: "Sign in with a passkey" }).waitFor({ timeout: 20_000 });
  await captureView(loginPage, device, "login");

  await loginPage.getByRole("button", { name: "Register", exact: true }).click();
  await loginPage.getByText("Create an account", { exact: true }).waitFor({ timeout: 20_000 });
  await captureView(loginPage, device, "register");
  await loginPage.close();

  const securedPage = await browser.newPage({ ...device });
  await installApiMock(securedPage, { authModeOverride: "Local" });
  await securedPage.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await freezeAnimations(securedPage);
  await securedPage.locator("input[type=email]").fill("jspillers@example.com");
  await securedPage.locator("input[type=password]").fill("correct-horse-battery-staple-1");
  await securedPage.getByRole("button", { name: "Sign in", exact: true }).click();
  // gotoTab's own drawer-vs-tabs detection uses isVisible(), which checks current state rather
  // than genuinely waiting -- calling it immediately after the sign-in click risks racing the
  // authenticated AppShell's own mount (React re-rendering from the Login screen to the app
  // shell isn't synchronous with the click resolving). Wait for something that only exists once
  // the authenticated shell has actually mounted, in both its mobile and desktop forms, before
  // handing off to gotoTab's own state check.
  await securedPage.getByLabel("Account menu").waitFor({ timeout: 20_000 });
  await gotoTab(securedPage, "Security");
  await securedPage.getByText("YubiKey 5C", { exact: false }).waitFor({ timeout: 20_000 });
  await captureView(securedPage, device, "security");
  await securedPage.close();
}
