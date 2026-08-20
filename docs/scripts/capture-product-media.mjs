#!/usr/bin/env node
// Captures current Partner Center Bridge product screenshots for the GitHub Pages site.
// It runs the real React SPA (web/) and intercepts every /api/* call with mocked, realistic
// MSP data, so no backend, database, or Microsoft tenant is needed.
//
//   cd web && npm run dev        # serves the SPA on http://127.0.0.1:5173
//   node docs/scripts/capture-product-media.mjs
//
// Output: docs/assets/screenshots/pcbridge-*.jpg
import { createRequire } from "node:module";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { installApiMock, freezeAnimations, loadPlaywright, waitForServer } from "./mock-api.mjs";

const repoRoot = path.resolve(fileURLToPath(new URL("../..", import.meta.url)));
const outDir = path.join(repoRoot, "docs", "assets", "screenshots");
const baseUrl = process.env.PCBRIDGE_CAPTURE_BASE_URL || "http://127.0.0.1:5173";
const debugCapture = process.env.PCBRIDGE_CAPTURE_DEBUG === "1";
async function gotoTab(page, label) {
  // The app shell's flat <nav><button> markup was replaced by MUI Tabs (role="tab") when the
  // responsive AppShell landed (PR #20) -- this selector went stale then and nothing had run the
  // script since to notice.
  await page.getByRole("tab", { name: label, exact: true }).click();
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

    console.log("Rendering Dashboard...");
    await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
    await freezeAnimations(page);
    await page.getByText("Needs attention", { exact: false }).waitFor({ timeout: 20_000 });
    await page.screenshot({ path: path.join(outDir, "pcbridge-dashboard.jpg"), type: "jpeg", quality: 92 });

    console.log("Rendering Workflows (Diagnose)...");
    await gotoTab(page, "Workflows");
    await page.getByRole("button", { name: "Mailbox archive repair", exact: true }).click();
    await page.locator("select").first().selectOption({ label: "Contoso Ltd" });
    await page.getByPlaceholder("user@contoso.com").fill("maya.chen@contoso.com");
    await page.getByRole("button", { name: "Diagnose", exact: true }).click();
    await page.getByText("blocks the Managed Folder Assistant", { exact: false }).waitFor({ timeout: 20_000 });
    await page.screenshot({ path: path.join(outDir, "pcbridge-workflows.jpg"), type: "jpeg", quality: 92 });

    console.log("Rendering Deploy (fan-out results)...");
    await gotoTab(page, "Deploy");
    await page.locator("select").first().selectOption({ label: "7-Zip 24.08 v3" });
    const boxes = page.locator("fieldset .check input[type=checkbox]");
    await boxes.nth(0).check();
    await boxes.nth(1).check();
    await boxes.nth(2).check();
    await page.getByRole("button", { name: /^Deploy to/ }).click();
    await page.getByText("Intune app id", { exact: false }).waitFor({ timeout: 20_000 });
    await page.screenshot({ path: path.join(outDir, "pcbridge-deploy.jpg"), type: "jpeg", quality: 92 });

    console.log("Rendering Find User (cross-tenant search)...");
    await gotoTab(page, "Find User");
    await page.getByPlaceholder(/Name or UPN/).fill("chen");
    await page.getByRole("button", { name: "Search", exact: true }).click();
    await page.getByText("match(es) across", { exact: false }).waitFor({ timeout: 20_000 });
    await page.screenshot({ path: path.join(outDir, "pcbridge-finduser.jpg"), type: "jpeg", quality: 92 });

    console.log("Rendering Tenants (contract model)...");
    await gotoTab(page, "Tenants");
    await page.getByText("Wingtip Partners", { exact: false }).waitFor({ timeout: 20_000 });
    await page.screenshot({ path: path.join(outDir, "pcbridge-tenants.jpg"), type: "jpeg", quality: 92 });

    console.log("Rendering Approvals (MCP human-in-the-loop queue)...");
    await gotoTab(page, "Approvals");
    await page.getByText("usage location set, but SKU still in error state", { exact: false }).waitFor({ timeout: 20_000 });
    await page.screenshot({ path: path.join(outDir, "pcbridge-approvals.jpg"), type: "jpeg", quality: 92 });

    // --- Auth:Mode=Local screens: Login (passkey-primary), Register, Security, Config Snapshots.
    // Separate pages/contexts because these need their own unauthenticated -> authenticated
    // lifecycle, distinct from the auth-disabled "Dev" mode the screens above ran under -- each
    // gets its own installApiMock({ authModeOverride: "Local" }) call below.

    console.log("Rendering Login (passkey-primary)...");
    const loginPage = await browser.newPage({ viewport: { width: 1440, height: 960 }, deviceScaleFactor: 1 });
    await installApiMock(loginPage, { authModeOverride: "Local" });
    await loginPage.goto(baseUrl, { waitUntil: "domcontentloaded" });
    await freezeAnimations(loginPage);
    await loginPage.getByRole("button", { name: "Sign in with a passkey" }).waitFor({ timeout: 20_000 });
    await loginPage.screenshot({ path: path.join(outDir, "pcbridge-login.jpg"), type: "jpeg", quality: 92 });

    console.log("Rendering Register...");
    await loginPage.getByRole("button", { name: "Register", exact: true }).click();
    await loginPage.getByText("Create an account", { exact: true }).waitFor({ timeout: 20_000 });
    await loginPage.screenshot({ path: path.join(outDir, "pcbridge-register.jpg"), type: "jpeg", quality: 92 });
    await loginPage.close();

    console.log("Signing in (mocked) to render Security...");
    const securedPage = await browser.newPage({ viewport: { width: 1440, height: 960 }, deviceScaleFactor: 1 });
    await installApiMock(securedPage, { authModeOverride: "Local" });
    await securedPage.goto(baseUrl, { waitUntil: "domcontentloaded" });
    await freezeAnimations(securedPage);
    await securedPage.locator("input[type=email]").fill("jspillers@example.com");
    await securedPage.locator("input[type=password]").fill("correct-horse-battery-staple-1");
    await securedPage.getByRole("button", { name: "Sign in", exact: true }).click();
    await securedPage.getByRole("tab", { name: "Security", exact: true }).waitFor({ timeout: 20_000 });
    await securedPage.getByRole("tab", { name: "Security", exact: true }).click();
    await securedPage.getByText("YubiKey 5C", { exact: false }).waitFor({ timeout: 20_000 });
    await securedPage.screenshot({ path: path.join(outDir, "pcbridge-security.jpg"), type: "jpeg", quality: 92 });

    console.log("Rendering Config Snapshots (diff view)...");
    await gotoTab(securedPage, "Config Snapshots");
    await securedPage.getByText("jspillers", { exact: false }).first().waitFor({ timeout: 20_000 });
    const diffSelects = securedPage.locator("fieldset select");
    await diffSelects.nth(0).selectOption({ index: 1 });
    await diffSelects.nth(1).selectOption({ index: 2 });
    await securedPage.getByRole("button", { name: "View diff", exact: true }).click();
    await securedPage.getByText("Block legacy authentication", { exact: false }).waitFor({ timeout: 20_000 });
    await securedPage.screenshot({ path: path.join(outDir, "pcbridge-config-snapshots.jpg"), type: "jpeg", quality: 92 });
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
