#!/usr/bin/env node
// Captures current Partner Center Bridge product screenshots for the GitHub Pages site.
// It runs the real React SPA (web/) and intercepts every /api/* call with mocked, realistic
// MSP data, so no backend, database, or Microsoft tenant is needed.
//
//   cd web && npm run dev        # serves the SPA on http://127.0.0.1:5173
//   node docs/scripts/capture-product-media.mjs
//
// Output: docs/assets/screenshots/pcbridge-*.jpg
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { installApiMock, freezeAnimations, loadPlaywright, waitForServer } from "./mock-api.mjs";

const repoRoot = path.resolve(fileURLToPath(new URL("../..", import.meta.url)));
const outDir = path.join(repoRoot, "docs", "assets", "screenshots");
const baseUrl = process.env.PCBRIDGE_CAPTURE_BASE_URL || "http://127.0.0.1:5173";
const debugCapture = process.env.PCBRIDGE_CAPTURE_DEBUG === "1";
const CONTOSO = "11111111-1111-1111-1111-111111111111";

// Every screen has a real route (docs/specs/ops-workbench.md section C), so navigation is by URL.
async function gotoRoute(page, route) {
  await page.goto(new URL(route, baseUrl).toString(), { waitUntil: "domcontentloaded" });
  await freezeAnimations(page);
}

async function shoot(page, name) {
  await page.screenshot({ path: path.join(outDir, name), type: "jpeg", quality: 92 });
}

async function main() {
  fs.mkdirSync(outDir, { recursive: true });
  const { chromium } = loadPlaywright();

  let browser;
  try {
    console.log(`Using Partner Center Bridge SPA at ${baseUrl}...`);
    await waitForServer(baseUrl);
    console.log("Launching Chromium...");
    browser = await chromium.launch({ headless: true });
    const page = await browser.newPage({ viewport: { width: 1440, height: 960 }, deviceScaleFactor: 1 });
    if (debugCapture) {
      page.on("console", (m) => console.log(`BROWSER ${m.type()}: ${m.text()}`));
      page.on("pageerror", (e) => console.log(`BROWSER pageerror: ${e.message}`));
    }
    await installApiMock(page);

    console.log("Rendering Home...");
    await gotoRoute(page, "/");
    await page.getByText("Needs attention", { exact: false }).waitFor({ timeout: 20_000 });
    await page.getByText("Finish setting up", { exact: true }).waitFor({ timeout: 20_000 });
    await shoot(page, "pcbridge-dashboard.jpg");

    console.log("Rendering Workflows (Diagnose, opened pre-filled from a person)...");
    await gotoRoute(page, `/operations/workflows/mailbox-archive?tenant=${CONTOSO}&user=maya.chen%40contoso.com`);
    await page.getByRole("button", { name: "Diagnose", exact: true }).click();
    await page.getByText("blocks the Managed Folder Assistant", { exact: false }).waitFor({ timeout: 20_000 });
    await shoot(page, "pcbridge-workflows.jpg");

    console.log("Rendering Deploy (fan-out results)...");
    await gotoRoute(page, "/operations/deploy");
    await page.getByLabel("Template").click();
    await page.getByRole("option", { name: "7-Zip 24.08 v3", exact: false }).click();
    const boxes = page.locator("fieldset input[type=checkbox]");
    await boxes.nth(0).check();
    await boxes.nth(1).check();
    await boxes.nth(2).check();
    await page.getByRole("button", { name: /^(Re)?[Dd]eploy to/ }).click();
    // Deploy is gated behind a confirmation dialog.
    const deployDialog = page.getByRole("dialog");
    await deployDialog.getByRole("button", { name: "Deploy", exact: true }).click();
    await page.getByText("Intune app id", { exact: false }).waitFor({ timeout: 20_000 });
    await shoot(page, "pcbridge-deploy.jpg");

    console.log("Rendering People (cross-tenant search)...");
    await gotoRoute(page, "/people?q=chen");
    await page.getByText("match(es) across", { exact: false }).waitFor({ timeout: 20_000 });
    await shoot(page, "pcbridge-finduser.jpg");

    console.log("Rendering Tenants (contract model)...");
    await gotoRoute(page, "/tenants");
    await page.getByText("Wingtip Partners", { exact: false }).waitFor({ timeout: 20_000 });
    await shoot(page, "pcbridge-tenants.jpg");

    console.log("Rendering Approvals (MCP human-in-the-loop queue)...");
    await gotoRoute(page, "/activity/approvals");
    await page.getByText("usage location set, but SKU still in error state", { exact: false }).waitFor({ timeout: 20_000 });
    await shoot(page, "pcbridge-approvals.jpg");

    // --- Auth:Mode=Local screens: Login (passkey-primary), Register, Security, Config Snapshots.
    // Separate pages because these need their own unauthenticated -> authenticated lifecycle,
    // distinct from the auth-disabled "Dev" mode the screens above ran under.

    console.log("Rendering Login (passkey-primary)...");
    const loginPage = await browser.newPage({ viewport: { width: 1440, height: 960 }, deviceScaleFactor: 1 });
    await installApiMock(loginPage, { authenticated: false, authModeOverride: "Local" });
    await gotoRoute(loginPage, "/");
    await loginPage.getByRole("button", { name: "Sign in with a passkey" }).waitFor({ timeout: 20_000 });
    await shoot(loginPage, "pcbridge-login.jpg");

    console.log("Rendering Register...");
    await loginPage.getByRole("button", { name: "Register", exact: true }).click();
    await loginPage.getByText("Create an account", { exact: true }).waitFor({ timeout: 20_000 });
    await shoot(loginPage, "pcbridge-register.jpg");
    await loginPage.close();

    console.log("Signing in (mocked) to render Security...");
    const securedPage = await browser.newPage({ viewport: { width: 1440, height: 960 }, deviceScaleFactor: 1 });
    await installApiMock(securedPage, { authModeOverride: "Local" });
    // Deep link while signed out: sign-in returns to it.
    await gotoRoute(securedPage, "/settings/security");
    await securedPage.locator("input[type=email]").fill("jspillers@example.com");
    await securedPage.locator("input[type=password]").fill("correct-horse-battery-staple-1");
    await securedPage.getByRole("button", { name: "Sign in", exact: true }).click();
    await securedPage.getByText("YubiKey 5C", { exact: false }).waitFor({ timeout: 20_000 });
    await shoot(securedPage, "pcbridge-security.jpg");

    console.log("Rendering Config Snapshots (diff view, on the tenant workspace)...");
    await gotoRoute(securedPage, `/tenants/${CONTOSO}?tab=snapshots`);
    await securedPage.getByText("jspillers", { exact: false }).first().waitFor({ timeout: 20_000 });
    await securedPage.getByLabel("Before").click();
    await securedPage.getByRole("option").nth(1).click();
    await securedPage.getByLabel("After").click();
    await securedPage.getByRole("option").nth(2).click();
    await securedPage.getByRole("button", { name: "View diff", exact: true }).click();
    await securedPage.getByText("Block legacy authentication", { exact: false }).waitFor({ timeout: 20_000 });
    await shoot(securedPage, "pcbridge-config-snapshots.jpg");
    await securedPage.close();

    console.log(`Captured screenshots in ${path.relative(repoRoot, outDir)}`);
  } finally {
    if (browser) await browser.close();
  }
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
