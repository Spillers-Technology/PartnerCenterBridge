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

const require = createRequire(import.meta.url);
const repoRoot = path.resolve(fileURLToPath(new URL("../..", import.meta.url)));
const outDir = path.join(repoRoot, "docs", "assets", "screenshots");
const baseUrl = process.env.PCBRIDGE_CAPTURE_BASE_URL || "http://127.0.0.1:5173";
const debugCapture = process.env.PCBRIDGE_CAPTURE_DEBUG === "1";

function loadPlaywright() {
  const candidates = [
    process.env.PLAYWRIGHT_NODE_MODULES
      ? path.join(process.env.PLAYWRIGHT_NODE_MODULES, "playwright")
      : null,
    path.join(repoRoot, "web", "node_modules", "playwright"),
    "playwright",
  ].filter(Boolean);

  for (const candidate of candidates) {
    try {
      return require(candidate);
    } catch {
      // try the next location
    }
  }

  throw new Error(
    [
      "Playwright is required to capture product media.",
      "Install it in a temp directory, then point PLAYWRIGHT_NODE_MODULES at that node_modules folder:",
      "  npm install --prefix %TEMP%\\pcbridge-playwright playwright",
      "  $env:PLAYWRIGHT_NODE_MODULES=\"$env:TEMP\\pcbridge-playwright\\node_modules\"",
      "Start the SPA first: cd web; npm run dev",
      "  node docs/scripts/capture-product-media.mjs",
    ].join("\n")
  );
}

function minutesAgo(mins) {
  return new Date(Date.now() - mins * 60_000).toISOString();
}

// ---------------------------------------------------------------------------
// Mocked data - a small but coherent MSP world.
// ---------------------------------------------------------------------------

const tenants = [
  { id: "11111111-1111-1111-1111-111111111111", tenantId: "aa11...", displayName: "Contoso Ltd", defaultDomain: "contoso.onmicrosoft.com", status: "Active", contractId: "c1" },
  { id: "22222222-2222-2222-2222-222222222222", tenantId: "bb22...", displayName: "Fabrikam Inc", defaultDomain: "fabrikam.onmicrosoft.com", status: "Active", contractId: "c1" },
  { id: "33333333-3333-3333-3333-333333333333", tenantId: "cc33...", displayName: "Tailspin Toys", defaultDomain: "tailspintoys.onmicrosoft.com", status: "Active", contractId: "c2" },
  { id: "44444444-4444-4444-4444-444444444444", tenantId: "dd44...", displayName: "Adventure Works", defaultDomain: "adventure-works.onmicrosoft.com", status: "Active", contractId: "c2" },
  { id: "55555555-5555-5555-5555-555555555555", tenantId: "ee55...", displayName: "Wingtip Partners", defaultDomain: "wingtip.onmicrosoft.com", status: "NoDelegation" },
];

const contracts = [
  { id: "c1", name: "Managed Workstations", notes: "Full Win32 baseline + provisioning", tenantCount: 2, desiredAppCount: 3 },
  { id: "c2", name: "Standard Care", notes: "Core apps only", tenantCount: 2, desiredAppCount: 1 },
];

const templates = [
  { id: "t1", displayName: "7-Zip 24.08", publisher: "Igor Pavlov", contentVersion: 3, hasPackage: true, contractId: "c1", detectionRules: [], assignments: [] },
  { id: "t2", displayName: "Google Chrome Enterprise 126", publisher: "Google LLC", contentVersion: 5, hasPackage: true, contractId: "c1", detectionRules: [], assignments: [] },
  { id: "t3", displayName: "Company Portal branding", publisher: "Contoso IT", contentVersion: 2, hasPackage: true, contractId: "c1", detectionRules: [], assignments: [] },
  { id: "t4", displayName: "FortiClient VPN", publisher: "Fortinet", contentVersion: 1, hasPackage: false, detectionRules: [], assignments: [] },
];

const deployments = [
  { id: "d1", appTemplateId: "t1", tenantId: tenants[0].id, intuneAppId: "9b1c...a1", deployedTemplateVersion: 3, status: "Succeeded", lastSyncedAt: minutesAgo(95) },
  { id: "d2", appTemplateId: "t1", tenantId: tenants[1].id, intuneAppId: "7f2d...c4", deployedTemplateVersion: 2, status: "UpdateAvailable", lastSyncedAt: minutesAgo(120) },
  { id: "d3", appTemplateId: "t2", tenantId: tenants[0].id, intuneAppId: "42aa...9e", deployedTemplateVersion: 5, status: "Succeeded", lastSyncedAt: minutesAgo(140) },
  { id: "d4", appTemplateId: "t2", tenantId: tenants[1].id, intuneAppId: "42bb...1f", deployedTemplateVersion: 4, status: "UpdateAvailable", lastSyncedAt: minutesAgo(150) },
  { id: "d5", appTemplateId: "t3", tenantId: tenants[0].id, intuneAppId: "c103...77", deployedTemplateVersion: 2, status: "Succeeded", lastSyncedAt: minutesAgo(200) },
  { id: "d6", appTemplateId: "t3", tenantId: tenants[1].id, deployedTemplateVersion: 2, status: "Failed", lastError: "content commit rejected: 409 conflict on committedContentVersion", lastSyncedAt: minutesAgo(35) },
  { id: "d7", appTemplateId: "t1", tenantId: tenants[2].id, intuneAppId: "5510...ab", deployedTemplateVersion: 3, status: "Succeeded", lastSyncedAt: minutesAgo(300) },
  { id: "d8", appTemplateId: "t1", tenantId: tenants[3].id, intuneAppId: "5511...cd", deployedTemplateVersion: 3, status: "Succeeded", lastSyncedAt: minutesAgo(320) },
];

const workflows = [
  {
    id: "compromised-lockdown", name: "Compromised account lockdown", category: "Identity",
    description: "Block sign-in, revoke all sessions, and disable inbox rules that forward, redirect, or delete mail.",
    inputs: [{ key: "userUpn", label: "User UPN or id", placeholder: "user@contoso.com", required: true, type: "text" }],
  },
  {
    id: "license-repair", name: "License assignment repair", category: "Identity",
    description: "Fix a user whose license won't apply - sets usage location and reprocesses stuck SKUs.",
    inputs: [
      { key: "userUpn", label: "User UPN or id", placeholder: "user@contoso.com", required: true, type: "text" },
      { key: "usageLocation", label: "Usage location (2-letter)", placeholder: "US", required: false, default: "US", type: "text" },
    ],
  },
  {
    id: "mfa-reset", name: "MFA / auth method reset", category: "Identity",
    description: "Revoke sessions and clear registered authentication methods so the user re-registers MFA.",
    inputs: [{ key: "userUpn", label: "User UPN or id", placeholder: "user@contoso.com", required: true, type: "text" }],
  },
  {
    id: "password-reset", name: "Password reset + session revoke", category: "Identity",
    description: "Set a temporary must-change password and revoke all sessions so the old credential stops working everywhere.",
    inputs: [{ key: "userUpn", label: "User UPN or id", placeholder: "user@contoso.com", required: true, type: "text" }],
  },
  {
    id: "mailbox-archive", name: "Mailbox archive repair", category: "Mailbox",
    description: "Fix a full mailbox that is not archiving: enable archive + auto-expand, ensure a retention policy, clear processing blockers, and kick the Managed Folder Assistant. Re-run to nudge the asynchronous move.",
    inputs: [
      { key: "identity", label: "Mailbox UPN or alias", placeholder: "user@contoso.com", required: true, type: "text" },
      { key: "retentionPolicyName", label: "Retention policy to assign if none", placeholder: "Default MRM Policy", required: false, default: "Default MRM Policy", type: "text" },
      { key: "enableAutoExpandingArchive", label: "Enable auto-expanding archive", required: false, default: "true", type: "bool" },
      { key: "clearProcessingBlocks", label: "Clear retention hold / ELC blocks", required: false, default: "true", type: "bool" },
      { key: "triggerProcessing", label: "Trigger the Managed Folder Assistant", required: false, default: "true", type: "bool" },
    ],
  },
];

const mailboxDiagnosis = {
  healthy: false,
  findings: [
    { name: "Primary mailbox size", status: "Warning", detail: "48.9 GB of 50 GB (98%)" },
    { name: "Archive mailbox", status: "Blocker", detail: "Archive not enabled" },
    { name: "Auto-expanding archive", status: "Warning", detail: "Disabled" },
    { name: "Retention policy", status: "Blocker", detail: "No retention policy assigned - the assistant has nothing to act on" },
    { name: "Retention hold", status: "Warning", detail: "RetentionHoldEnabled = true - blocks the Managed Folder Assistant" },
    { name: "ELC processing", status: "Warning", detail: "ElcProcessingDisabled = true" },
    { name: "Managed Folder Assistant", status: "Info", detail: "Last processed 6 days ago" },
  ],
};

const runs = [
  { id: "r1", workflowId: "mailbox-archive", workflowName: "Mailbox archive repair", tenantId: tenants[0].id, tenantName: "Contoso Ltd", kind: "Remediate", operator: "jspillers", inputs: {}, findings: [], steps: [], succeeded: true, healthy: true, startedAt: minutesAgo(40), durationMs: 5210 },
  { id: "r2", workflowId: "mfa-reset", workflowName: "MFA / auth method reset", tenantId: tenants[2].id, tenantName: "Tailspin Toys", kind: "Remediate", operator: "jspillers", inputs: {}, findings: [], steps: [], succeeded: true, healthy: true, startedAt: minutesAgo(70), durationMs: 1890 },
  { id: "r3", workflowId: "license-repair", workflowName: "License assignment repair", tenantId: tenants[2].id, tenantName: "Tailspin Toys", kind: "Remediate", operator: "amorgan", inputs: {}, findings: [], steps: [], succeeded: false, healthy: false, error: "usage location set, but SKU still in error state after reprocess", startedAt: minutesAgo(110), durationMs: 4400 },
  { id: "r4", workflowId: "compromised-lockdown", workflowName: "Compromised account lockdown", tenantId: tenants[1].id, tenantName: "Fabrikam Inc", kind: "Remediate", operator: "jspillers", inputs: {}, findings: [], steps: [], succeeded: true, healthy: true, startedAt: minutesAgo(180), durationMs: 3120 },
  { id: "r5", workflowId: "password-reset", workflowName: "Password reset + session revoke", tenantId: tenants[0].id, tenantName: "Contoso Ltd", kind: "Diagnose", operator: "amorgan", inputs: {}, findings: [], steps: [], succeeded: true, healthy: true, startedAt: minutesAgo(220), durationMs: 640 },
];

const dashboard = {
  stats: {
    tenants: 5, tenantsNoDelegation: 1,
    deployments: 8, deploymentsFailed: 1, deploymentsUpdateAvailable: 2,
    runsLast24h: 6, runsFailedLast7d: 1,
  },
  needsAttention: [
    { kind: "Deployment failed", tenantId: tenants[1].id, tenantName: "Fabrikam Inc", subject: "Company Portal branding", detail: "content commit rejected: 409 conflict on committedContentVersion", when: minutesAgo(35) },
    { kind: "Workflow failed", tenantId: tenants[2].id, tenantName: "Tailspin Toys", subject: "License assignment repair", detail: "usage location set, but SKU still in error state after reprocess", when: minutesAgo(110) },
    { kind: "No delegation", tenantId: tenants[4].id, tenantName: "Wingtip Partners", subject: "wingtip.onmicrosoft.com", detail: "GDAP relationship missing or expired - the bridge cannot act here.", when: minutesAgo(600) },
  ],
  recentRuns: runs,
};

const searchResult = {
  tenantsSearched: 4,
  hits: [
    { tenantId: tenants[0].id, tenantName: "Contoso Ltd", id: "u1", displayName: "Maya Chen", userPrincipalName: "maya.chen@contoso.com" },
    { tenantId: tenants[2].id, tenantName: "Tailspin Toys", id: "u2", displayName: "Marco Chen", userPrincipalName: "marco.chen@tailspintoys.com" },
  ],
  errors: [
    { tenantId: tenants[4].id, tenantName: "Wingtip Partners", message: "GDAP relationship missing or expired" },
  ],
};

const skus = [
  { skuId: "sku-e3", skuPartNumber: "SPE_E3", enabled: 25, consumed: 21 },
  { skuId: "sku-bp", skuPartNumber: "O365_BUSINESS_PREMIUM", enabled: 40, consumed: 33 },
  { skuId: "sku-eop", skuPartNumber: "EXCHANGE_S_STANDARD", enabled: 10, consumed: 4 },
];

const groups = [
  { id: "g1", displayName: "All Staff" },
  { id: "g2", displayName: "VPN Users" },
  { id: "g3", displayName: "Managed Workstations" },
];

const directoryUsers = [
  { id: "u10", displayName: "Priya Shah", userPrincipalName: "priya.shah@contoso.com" },
  { id: "u11", displayName: "Sam Rivera", userPrincipalName: "sam.rivera@contoso.com" },
];

const provisioningTemplate = {
  contractId: "c1", usageLocation: "US", upnDomain: "contoso.com",
  defaultJobTitle: "Associate", defaultDepartment: "Operations",
  licenseSkuIds: ["sku-e3"], groupIds: ["g1", "g3"],
};

// ---------------------------------------------------------------------------
// API mock router
// ---------------------------------------------------------------------------

function json(route, body, status = 200) {
  return route.fulfill({ status, contentType: "application/json", body: JSON.stringify(body) });
}

async function handleApi(route) {
  const request = route.request();
  const url = new URL(request.url());
  const apiPath = url.pathname.replace(/^\/api/, "");
  const method = request.method();
  if (debugCapture) console.log(`API ${method} ${apiPath}`);

  if (method === "GET" && apiPath === "/dashboard") return json(route, dashboard);
  if (method === "GET" && apiPath === "/tenants") return json(route, tenants);
  if (method === "POST" && apiPath === "/tenants/sync") return json(route, tenants);
  if (method === "GET" && apiPath === "/contracts") return json(route, contracts);
  if (method === "GET" && apiPath === "/apptemplates") return json(route, templates);
  if (method === "GET" && apiPath === "/deployments") return json(route, deployments);

  if (method === "POST" && apiPath === "/deployments") {
    const body = request.postDataJSON?.() ?? {};
    const targetIds = body.tenantIds ?? tenants.slice(0, 2).map((t) => t.id);
    const template = templates.find((t) => t.id === body.templateId) ?? templates[0];
    return json(route, targetIds.map((tid, i) => ({
      id: `new-${i}`, appTemplateId: template.id, tenantId: tid,
      intuneAppId: `${(9000 + i).toString(16)}...f${i}`, deployedTemplateVersion: template.contentVersion,
      status: "Succeeded",
    })));
  }

  if (method === "GET" && apiPath === "/search/users") return json(route, searchResult);

  if (method === "GET" && apiPath === "/workflows") return json(route, workflows);
  if (method === "GET" && apiPath === "/workflows/runs") return json(route, runs);

  let match = apiPath.match(/^\/workflows\/([^/]+)\/diagnose$/);
  if (method === "POST" && match) return json(route, mailboxDiagnosis);

  match = apiPath.match(/^\/workflows\/([^/]+)\/remediate$/);
  if (method === "POST" && match) {
    return json(route, {
      succeeded: true,
      steps: [
        { name: "Enable archive mailbox", success: true, detail: "Enable-Mailbox -Archive" },
        { name: "Enable auto-expanding archive", success: true, detail: "Set-Mailbox -AutoExpandingArchive" },
        { name: "Assign retention policy", success: true, detail: "Default MRM Policy" },
        { name: "Clear retention hold + ELC block", success: true, detail: "RetentionHoldEnabled = false" },
        { name: "Trigger Managed Folder Assistant", success: true, detail: "Start-ManagedFolderAssistant" },
      ],
      postState: { ...mailboxDiagnosis, healthy: false },
      ephemeral: {},
    });
  }

  match = apiPath.match(/^\/directory\/([^/]+)\/skus$/);
  if (method === "GET" && match) return json(route, skus);
  match = apiPath.match(/^\/directory\/([^/]+)\/groups$/);
  if (method === "GET" && match) return json(route, groups);
  match = apiPath.match(/^\/directory\/([^/]+)\/users$/);
  if (method === "GET" && match) return json(route, directoryUsers);

  match = apiPath.match(/^\/contracts\/([^/]+)\/provisioning-template$/);
  if (method === "GET" && match) return json(route, provisioningTemplate);

  match = apiPath.match(/^\/contracts\/([^/]+)\/plan$/);
  if (method === "GET" && match) {
    return json(route, [
      { tenantId: tenants[0].id, tenantName: "Contoso Ltd", templateId: "t1", templateName: "7-Zip 24.08", action: "UpToDate" },
      { tenantId: tenants[1].id, tenantName: "Fabrikam Inc", templateId: "t1", templateName: "7-Zip 24.08", action: "Update" },
    ]);
  }

  return json(route, {});
}

async function waitForServer() {
  const deadline = Date.now() + 60_000;
  while (Date.now() < deadline) {
    try {
      const res = await fetch(baseUrl, { signal: AbortSignal.timeout(3000) });
      if (res.ok) return;
    } catch {
      // keep waiting
    }
    await new Promise((resolve) => setTimeout(resolve, 500));
  }
  throw new Error(`Timed out waiting for ${baseUrl}`);
}

async function gotoTab(page, label) {
  await page.locator("header nav button", { hasText: new RegExp(`^${label}$`) }).click();
}

async function main() {
  fs.mkdirSync(outDir, { recursive: true });
  const { chromium } = loadPlaywright();

  let browser;
  try {
    console.log(`Using Partner Center Bridge SPA at ${baseUrl}...`);
    await waitForServer();
    console.log("Launching Chromium...");
    browser = await chromium.launch({ headless: true });
    const page = await browser.newPage({ viewport: { width: 1440, height: 960 }, deviceScaleFactor: 1 });
    if (debugCapture) {
      page.on("console", (m) => console.log(`BROWSER ${m.type()}: ${m.text()}`));
      page.on("pageerror", (e) => console.log(`BROWSER pageerror: ${e.message}`));
    }
    await page.route("**/*", (route) => {
      const pathname = new URL(route.request().url()).pathname;
      return pathname.startsWith("/api/") ? handleApi(route) : route.continue();
    });

    console.log("Rendering Dashboard...");
    await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
    await page.addStyleTag({
      content: `*, *::before, *::after {
        transition-duration: 0s !important;
        animation-duration: 0s !important;
        caret-color: transparent !important;
      }`,
    });
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

    console.log(`Captured screenshots in ${path.relative(repoRoot, outDir)}`);
  } finally {
    if (browser) await browser.close();
  }
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
