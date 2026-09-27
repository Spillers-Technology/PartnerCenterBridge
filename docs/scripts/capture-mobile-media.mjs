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
//   PCBRIDGE_CAPTURE_DEVICES=galaxy,fold-closed PCBRIDGE_CAPTURE_VIEWS=home,tenants node docs/scripts/capture-mobile-media.mjs
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
// View steps -- each takes the authenticated, mocked `page` and drives it to one view's
// resting/landing state. Navigation is by URL: every screen has a real route (see
// docs/specs/ops-workbench.md section C), so a view is "load this path, wait for its data".
// ---------------------------------------------------------------------------

const CONTOSO = "11111111-1111-1111-1111-111111111111";

// Loads a route directly. A full page load per view keeps each capture independent of the
// previous view's state (open menus, drawers, in-flight fetches); the mock and Vite's module
// cache make it cheap. freezeAnimations has to be re-applied after every load.
async function gotoRoute(page, route) {
  await page.goto(new URL(route, baseUrl).toString(), { waitUntil: "domcontentloaded" });
  await freezeAnimations(page);
}

// Page headings are h2 (the shell's brand is not a heading); scoping waits to the heading role
// keeps them from matching the same word in the navigation.
const heading = (page, name) => page.getByRole("heading", { name, exact: true }).first();

// Access Parity with both people in the URL: a link naming all three of tenant/source/target now
// compares automatically on load, so no click is needed here.
async function openParityPlan(page) {
  await gotoRoute(page, `/operations/access-parity?tenant=${CONTOSO}&source=priya.shah%40contoso.com&target=maya.chen%40contoso.com`);
  await page.getByText("Groups to add", { exact: false }).first().waitFor({ timeout: 20_000 });
}

// Offboarding from a person link: the exact-match user is selected, then the plan is previewed.
async function openOffboardPlan(page) {
  await gotoRoute(page, `/operations/offboard?tenant=${CONTOSO}&user=priya.shah%40contoso.com`);
  const preview = page.getByRole("button", { name: "Preview plan", exact: true });
  await preview.waitFor({ timeout: 20_000 });
  await page.waitForFunction(() => [...document.querySelectorAll("button")].some((b) => b.textContent === "Preview plan" && !b.disabled), null, { timeout: 20_000 });
  await preview.click();
  await page.getByText("steps will run", { exact: false }).waitFor({ timeout: 20_000 });
}

const AUTHENTICATED_VIEWS = {
  home: async (page) => {
    await gotoRoute(page, "/");
    // "Needs attention" only renders once the mocked dashboard has loaded; "Finish setting up"
    // once diagnostics has (the mock has an unconfigured Exchange module).
    await page.getByText("Needs attention", { exact: false }).waitFor({ timeout: 20_000 });
    await page.getByText("Finish setting up", { exact: true }).waitFor({ timeout: 20_000 });
  },
  people: async (page) => {
    await gotoRoute(page, "/people");
    await page.getByLabel(/Name or UPN/).waitFor({ timeout: 20_000 });
  },
  "people-results": async (page) => {
    // The results table (with its per-row fix buttons) is the overflow-prone part of People.
    await gotoRoute(page, "/people?q=chen");
    await page.getByText("match(es) across", { exact: false }).waitFor({ timeout: 20_000 });
  },
  person: async (page) => {
    await gotoRoute(page, `/people/${CONTOSO}/u1`);
    await page.getByText("Operations Analyst", { exact: true }).waitFor({ timeout: 20_000 });
    await page.getByText("2 managed devices", { exact: true }).waitFor({ timeout: 20_000 });
  },
  "person-access": async (page) => {
    await gotoRoute(page, `/people/${CONTOSO}/u1?tab=access`);
    await page.getByText("Operations - All (dynamic)", { exact: true }).waitFor({ timeout: 20_000 });
  },
  "person-auth": async (page) => {
    await gotoRoute(page, `/people/${CONTOSO}/u1?tab=auth`);
    await page.getByText("FIDO2 security key", { exact: true }).waitFor({ timeout: 20_000 });
  },
  "person-mailbox": async (page) => {
    // The mocked workbench has no Exchange module: the Unavailable state with its fix link.
    await gotoRoute(page, `/people/${CONTOSO}/u1?tab=mailbox`);
    await page.getByText("Mailbox unavailable", { exact: true }).waitFor({ timeout: 20_000 });
  },
  "person-devices": async (page) => {
    await gotoRoute(page, `/people/${CONTOSO}/u1?tab=devices`);
    await page.getByText("CONTOSO-LT-0142", { exact: true }).waitFor({ timeout: 20_000 });
  },
  "person-history": async (page) => {
    await gotoRoute(page, `/people/${CONTOSO}/u1?tab=history`);
    await page.getByText("Open the latest run's evidence", { exact: true }).waitFor({ timeout: 20_000 });
  },
  tenants: async (page) => {
    await gotoRoute(page, "/tenants");
    await page.getByText("Wingtip Partners", { exact: false }).waitFor({ timeout: 20_000 });
  },
  tenant: async (page) => {
    await gotoRoute(page, `/tenants/${CONTOSO}`);
    // "Recent runs" rows only exist once the tenant's runs have loaded.
    await page.getByText("jspillers", { exact: false }).first().waitFor({ timeout: 20_000 });
  },
  snapshots: async (page) => {
    // Config Snapshots now lives on the tenant workspace; "jspillers" (the operator on the
    // mocked snapshot runs) proves the tenant-scoped fetch resolved.
    await gotoRoute(page, `/tenants/${CONTOSO}?tab=snapshots`);
    await page.getByText("Diff two snapshots", { exact: true }).waitFor({ timeout: 20_000 });
    await page.getByText("jspillers", { exact: false }).first().waitFor({ timeout: 20_000 });
  },
  operations: async (page) => {
    await gotoRoute(page, "/operations");
    // Known fixes are fetched; the workflow names only render once the catalog has loaded.
    await page.getByText("Mailbox archive repair", { exact: true }).waitFor({ timeout: 20_000 });
  },
  newhire: async (page) => {
    // NewHire's own wide content (SKU/group checkboxes) is gated behind selecting a tenant, which
    // this capture never does -- the heading IS the view's resting state here.
    await gotoRoute(page, "/operations/onboard");
    await heading(page, "New hire").waitFor({ timeout: 20_000 });
  },
  offboard: async (page) => {
    await gotoRoute(page, "/operations/offboard");
    await heading(page, "Offboard").waitFor({ timeout: 20_000 });
  },
  deploy: async (page) => {
    // Wait for a real tenant checkbox label: the tenant fieldset renders once the tenants fetch
    // resolves and is exactly the wide content this capture needs to have rendered.
    await gotoRoute(page, "/operations/deploy");
    await page.getByText("Contoso Ltd", { exact: false }).first().waitFor({ timeout: 20_000 });
  },
  workflows: async (page) => {
    await gotoRoute(page, "/operations/workflows");
    await page.getByRole("button", { name: "Mailbox archive repair", exact: true }).waitFor({ timeout: 20_000 });
  },
  "workflow-prefilled": async (page) => {
    // A known fix deep link with the tenant and target user in the URL: the form is filled in.
    await gotoRoute(page, `/operations/workflows/mailbox-archive?tenant=${CONTOSO}&user=maya.chen%40contoso.com`);
    await page.getByRole("button", { name: "Diagnose", exact: true }).waitFor({ timeout: 20_000 });
  },
  "access-parity": async (page) => {
    await gotoRoute(page, "/operations/access-parity");
    await heading(page, "Mirror access").waitFor({ timeout: 20_000 });
  },
  "access-parity-plan": async (page) => {
    await openParityPlan(page);
  },
  "access-parity-result": async (page) => {
    await openParityPlan(page);
    await page.getByRole("button", { name: "Add Maya Chen to 4 groups", exact: true }).click();
    await page.getByRole("dialog").getByRole("button", { name: "Add to 4 groups", exact: true }).click();
    await page.getByRole("button", { name: "Copy ticket notes", exact: true }).waitFor({ timeout: 20_000 });
  },
  "offboard-plan": async (page) => {
    await openOffboardPlan(page);
  },
  "offboard-result": async (page) => {
    await openOffboardPlan(page);
    await page.getByRole("button", { name: "Offboard user", exact: true }).click();
    await page.getByRole("dialog").getByRole("button", { name: "Offboard", exact: true }).click();
    await page.getByRole("button", { name: "Copy ticket notes", exact: true }).waitFor({ timeout: 20_000 });
  },
  "contract-policy": async (page) => {
    await gotoRoute(page, "/operations/contracts");
    await page.getByRole("button", { name: "Offboarding policy", exact: true }).first().click();
    await page.getByRole("button", { name: "Save policy", exact: true }).waitFor({ timeout: 20_000 });
  },
  contracts: async (page) => {
    await gotoRoute(page, "/operations/contracts");
    await page.getByText("Managed Workstations", { exact: false }).waitFor({ timeout: 20_000 });
  },
  templates: async (page) => {
    await gotoRoute(page, "/operations/templates");
    await page.getByText("7-Zip 24.08", { exact: false }).waitFor({ timeout: 20_000 });
  },
  activity: async (page) => {
    // Runs and deployments together. "Update available" is a humanized deployment status that
    // only exists once the mocked deployments have loaded into the table.
    await gotoRoute(page, "/activity");
    await page.getByText("Update available", { exact: false }).first().waitFor({ timeout: 20_000 });
    await page.getByText("License assignment repair", { exact: true }).first().waitFor({ timeout: 20_000 });
  },
  approvals: async (page) => {
    // "workflow.remediate" is the actionType column value, only present once pendingActions loaded.
    await gotoRoute(page, "/activity/approvals");
    await page.getByText("workflow.remediate", { exact: false }).first().waitFor({ timeout: 20_000 });
  },
  run: async (page) => {
    await gotoRoute(page, "/activity/runs/r3");
    await page.getByText("SKU still in error state", { exact: false }).first().waitFor({ timeout: 20_000 });
  },
  "run-evidence": async (page) => {
    // A partially successful Access Parity apply: changes, a failed verification, ticket notes.
    await gotoRoute(page, "/activity/runs/r6");
    await page.getByRole("button", { name: "Copy ticket notes", exact: true }).waitFor({ timeout: 20_000 });
  },
  palette: async (page) => {
    await gotoRoute(page, "/");
    await page.getByText("Needs attention", { exact: false }).waitFor({ timeout: 20_000 });
    await page.keyboard.press("Control+k");
    await page.getByRole("combobox", { name: "Search commands" }).fill("con");
    await page.getByText("Open tenant: Contoso Ltd", { exact: true }).waitFor({ timeout: 20_000 });
  },
  settings: async (page) => {
    await gotoRoute(page, "/settings");
    await page.getByText("Workbench health", { exact: true }).waitFor({ timeout: 20_000 });
  },
  microsoft: async (page) => {
    await gotoRoute(page, "/settings/microsoft");
    await page.getByRole("button", { name: "Add tenant with Microsoft", exact: true }).waitFor({ timeout: 20_000 });
  },
  workbench: async (page) => {
    // The long command in the Exchange module's quest chip is the overflow risk on this page.
    await gotoRoute(page, "/settings/workbench");
    await page.getByText("Install-Module ExchangeOnlineManagement", { exact: false }).first().waitFor({ timeout: 20_000 });
  },
  notfound: async (page) => {
    await gotoRoute(page, "/no/such/page");
    await heading(page, "Page not found").waitFor({ timeout: 20_000 });
  },
};

async function main() {
  fs.mkdirSync(outDir, { recursive: true });
  const { chromium, devices } = loadPlaywright();
  const DEVICES = buildDevices(devices).filter((d) => !deviceFilter || deviceFilter.includes(d.name));
  const views = Object.entries(AUTHENTICATED_VIEWS).filter(([name]) => !viewFilter || viewFilter.includes(name));
  const AUTH_VIEW_NAMES = ["login", "register", "setup", "security"];

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
  console.log(`  [auth] Login/Register/Setup/Security on ${device.name}...`);
  const wants = (name) => !viewFilter || viewFilter.includes(name);

  const loginPage = await browser.newPage({ ...device });
  await installApiMock(loginPage, { authenticated: false, authModeOverride: "Local" });
  // A deep link while signed out lands on /login (and returns there after sign-in).
  await loginPage.goto(new URL("/tenants", baseUrl).toString(), { waitUntil: "domcontentloaded" });
  await freezeAnimations(loginPage);
  await loginPage.getByRole("button", { name: "Sign in with a passkey" }).waitFor({ timeout: 20_000 });
  if (wants("login")) await captureView(loginPage, device, "login");

  await loginPage.getByRole("button", { name: "Register", exact: true }).click();
  await loginPage.getByText("Create an account", { exact: true }).waitFor({ timeout: 20_000 });
  if (wants("register")) await captureView(loginPage, device, "register");
  await loginPage.close();

  if (wants("setup")) {
    // A brand-new workbench (no users yet) routes straight to first-user setup.
    const setupPage = await browser.newPage({ ...device });
    await installApiMock(setupPage, { authenticated: false, authModeOverride: "Local", needsFirstUser: true });
    await setupPage.goto(baseUrl, { waitUntil: "domcontentloaded" });
    await freezeAnimations(setupPage);
    await setupPage.getByText("Set up this workbench", { exact: true }).waitFor({ timeout: 20_000 });
    await captureView(setupPage, device, "setup");
    await setupPage.close();
  }

  if (wants("security")) {
    const securedPage = await browser.newPage({ ...device });
    await installApiMock(securedPage, { authModeOverride: "Local" });
    await securedPage.goto(new URL("/settings/security", baseUrl).toString(), { waitUntil: "domcontentloaded" });
    await freezeAnimations(securedPage);
    await securedPage.locator("input[type=email]").fill("jspillers@example.com");
    await securedPage.locator("input[type=password]").fill("correct-horse-battery-staple-1");
    await securedPage.getByRole("button", { name: "Sign in", exact: true }).click();
    // Signing in returns to the deep link that sent us to /login.
    await securedPage.getByText("YubiKey 5C", { exact: false }).waitFor({ timeout: 20_000 });
    await captureView(securedPage, device, "security");
    await securedPage.close();
  }
}
