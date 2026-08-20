# Mobile/Desktop Capture-Matrix Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build permanent, repo-owned Playwright tooling that screenshots every current view (all 15:
12 tabs + Security + Login + Register) across 5 device profiles and asserts no page-level
horizontal overflow at each — turning the ad hoc live check from workstream 1's Task 9 into a
reusable, repeatable capture matrix.

**Architecture:** Extract the existing desktop capture script's mock API + helpers into a shared
`docs/scripts/mock-api.mjs`; refactor the desktop script to use it (behavior-preserving); add a new
`docs/scripts/capture-mobile-media.mjs` driving a table of (view, device) pairs through the same
mock, running an overflow assertion before each screenshot.

**Tech Stack:** Node.js (ESM), Playwright (loaded externally, never a `package.json` dependency,
per this repo's existing pattern in `docs/scripts/README.md`).

**Spec:** [docs/superpowers/specs/2026-08-19-mobile-capture-matrix-design.md](../specs/2026-08-19-mobile-capture-matrix-design.md)

## Global Constraints

- No new `package.json` dependency for Playwright — load via `PLAYWRIGHT_NODE_MODULES` env var or
  `web/node_modules/playwright`, exactly matching `docs/scripts/capture-product-media.mjs`'s
  existing `loadPlaywright()` pattern.
- Device short names: `galaxy`, `iphone`, `pixel`, `fold-closed`, `fold-open` — exactly these five.
- Output directory `docs/assets/screenshots/mobile/` must be gitignored (working artifacts, not
  committed) — unlike `docs/assets/screenshots/` itself, which holds the committed desktop hero
  shots and stays un-ignored.
- Env var names: `PCBRIDGE_CAPTURE_BASE_URL`, `PCBRIDGE_CAPTURE_DEBUG` (existing, unchanged),
  `PCBRIDGE_CAPTURE_OUT`, `PCBRIDGE_CAPTURE_DEVICES`, `PCBRIDGE_CAPTURE_VIEWS` (new, mobile script
  only).
- The desktop script's own output (`docs/assets/screenshots/pcbridge-*.jpg`) must be
  pixel-equivalent after the `mock-api.mjs` extraction — this is a behavior-preserving refactor,
  not a rewrite.
- ASCII-only string literals (repo-wide convention, CLAUDE.md).
- No AI attribution in commit messages.

---

### Task 1: Extract `mock-api.mjs`, refactor the desktop script to use it

**Files:**
- Create: `docs/scripts/mock-api.mjs`
- Modify: `docs/scripts/capture-product-media.mjs`

**Interfaces:**
- Produces: `installApiMock(page, { authenticated = true, authModeOverride = null } = {})` — wires
  `/api/*` interception on `page`; `docs/scripts/capture-mobile-media.mjs` (Task 2) will import
  this identically.
- Produces: `freezeAnimations(page)`, `loadPlaywright()`, `waitForServer(baseUrl)` — all moved
  as-is from the current script.

- [ ] **Step 1: Create `docs/scripts/mock-api.mjs`**

Move every mock data object (`tenants`, `contracts`, `templates`, `deployments`, `workflows`,
`mailboxDiagnosis`, `runs`, `dashboard`, `pendingActions`, `searchResult`, `skus`, `groups`,
`directoryUsers`, `provisioningTemplate`, `meProfile`, `passkeys`, `mcpTokens`, `configSections`,
`snapshotRuns`, `snapshotDiff`) and the `minutesAgo()` helper and `handleApi()` router function
verbatim from `docs/scripts/capture-product-media.mjs` into this new file, with these changes:

```js
// docs/scripts/mock-api.mjs
// Shared mocked-/api/* fixtures and Playwright helpers used by both capture-product-media.mjs
// (desktop hero shots) and capture-mobile-media.mjs (the mobile/desktop verification matrix).
import { createRequire } from "node:module";
import path from "node:path";
import { fileURLToPath } from "node:url";

const require = createRequire(import.meta.url);
const repoRoot = path.resolve(fileURLToPath(new URL("../..", import.meta.url)));

export function loadPlaywright() {
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
    ].join("\n")
  );
}

export async function waitForServer(baseUrl) {
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

export async function freezeAnimations(page) {
  await page.addStyleTag({
    content: `*, *::before, *::after {
      transition-duration: 0s !important;
      animation-duration: 0s !important;
      caret-color: transparent !important;
    }`,
  });
}

function minutesAgo(mins) {
  return new Date(Date.now() - mins * 60_000).toISOString();
}

// ---------------------------------------------------------------------------
// Mocked data - a small but coherent MSP world. (paste verbatim from the current
// capture-product-media.mjs: tenants, contracts, templates, deployments, workflows,
// mailboxDiagnosis, runs, dashboard, pendingActions, searchResult, skus, groups,
// directoryUsers, provisioningTemplate, meProfile, passkeys, mcpTokens, configSections,
// snapshotRuns, snapshotDiff -- every `const` between "Mocked data" and "API mock router"
// in the current file, unchanged)
// ---------------------------------------------------------------------------

// ... (paste here)

function json(route, body, status = 200) {
  return route.fulfill({ status, contentType: "application/json", body: JSON.stringify(body) });
}

export function installApiMock(page, { authenticated = true, authModeOverride = null } = {}) {
  async function handleApi(route) {
    const request = route.request();
    const url = new URL(request.url());
    const apiPath = url.pathname.replace(/^\/api/, "");
    const method = request.method();
    if (process.env.PCBRIDGE_CAPTURE_DEBUG === "1") console.log(`API ${method} ${apiPath}`);

    if (method === "GET" && apiPath === "/auth/mode") return json(route, { mode: authModeOverride || "Dev" });
    if (method === "GET" && apiPath === "/auth/me") {
      if (!authenticated) return json(route, {}, 401);
      return json(route, meProfile);
    }

    // ... (paste the rest of handleApi's route matching verbatim from the current file,
    // removing its own /auth/mode and /auth/me lines since they're now above)

    return json(route, {});
  }

  return page.route("**/*", (route) => {
    const pathname = new URL(route.request().url()).pathname;
    return pathname.startsWith("/api/") ? handleApi(route) : route.continue();
  });
}
```

The current file's module-level mutable `authModeOverride` variable becomes a per-call parameter
(`installApiMock(page, { authModeOverride: "Local" })`) instead of shared state — the mobile script
(Task 2) will run many more page contexts than the desktop script's handful, so implicit shared
mutable state is worth removing here rather than carrying it forward.

- [ ] **Step 2: Refactor `docs/scripts/capture-product-media.mjs` to import from `mock-api.mjs`**

Remove every piece moved in Step 1 (`loadPlaywright`, `waitForServer`, `minutesAgo`, all mock data
consts, `json`, `handleApi`) and the three duplicated `page.addStyleTag({ content: \`*, *::before...
\` })` animation-freezing blocks. Add at the top:

```js
import { installApiMock, freezeAnimations, loadPlaywright, waitForServer } from "./mock-api.mjs";
```

Replace every `await page.route("**/*", (route) => { ... handleApi ... })` call site with
`await installApiMock(page)` (default `authenticated: true, authModeOverride: null` matches the
current Dev-mode default for the first several screens), and `await installApiMock(loginPage, {
authModeOverride: "Local" })` / `await installApiMock(securedPage, { authModeOverride: "Local" })`
for the two page contexts currently using the module-level `authModeOverride = "Local"` toggle.
Replace every inline `addStyleTag` animation-freeze block with `await freezeAnimations(page)` (or
`loginPage`/`securedPage` as appropriate).

- [ ] **Step 3: Regenerate the desktop screenshots and diff against committed ones**

```bash
cd web && npm run dev   # terminal 1, leave running
```

In terminal 2, from the repo root:

```bash
git stash -u -- docs/assets/screenshots   # snapshot the currently-committed versions aside
PLAYWRIGHT_NODE_MODULES="$TEMP/pcbridge-playwright/node_modules" node docs/scripts/capture-product-media.mjs
git status --short docs/assets/screenshots   # should show only the 9 pcbridge-*.jpg files
```

(If no `$TEMP/pcbridge-playwright` install exists yet, `npm install --prefix
"$TEMP/pcbridge-playwright" playwright` first, per the error message `loadPlaywright()` prints.)

Visually compare each regenerated `docs/assets/screenshots/pcbridge-*.jpg` against the
stashed-aside version (open both, or use an image diff tool if available) — they must be
structurally identical (same layout, same content, same colors); minor JPEG re-encoding artifacts
are fine, any structural difference is not and means the refactor changed behavior. Once confirmed
identical: `git stash drop` (discard the snapshot-aside, keep the regenerated files as the new
committed versions — they should be byte-for-byte or near-identical anyway).

- [ ] **Step 4: Commit**

```bash
git add docs/scripts/mock-api.mjs docs/scripts/capture-product-media.mjs docs/assets/screenshots/pcbridge-*.jpg
git commit -m "Extract shared mock-api.mjs from the desktop capture script"
```

---

### Task 2: Device profiles, script skeleton, overflow assertion, first 3 views proven

**Files:**
- Create: `docs/scripts/capture-mobile-media.mjs`

**Interfaces:**
- Consumes: `installApiMock`, `freezeAnimations`, `loadPlaywright`, `waitForServer` from
  `./mock-api.mjs` (Task 1).
- Produces: the `DEVICES` array and `assertNoOverflow(page, label)` helper that Tasks 3-4 add view
  entries to — establishes the exact loop shape those tasks extend.

- [ ] **Step 1: Create `docs/scripts/capture-mobile-media.mjs` with the device matrix, overflow
  helper, and the first 3 views (Dashboard, Tenants, Find User) fully wired**

```js
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
  await page.locator("header nav button, header [role=tab]", { hasText: new RegExp(`^${label}$`) }).click();
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
```

Note the `dashboard` entry has no `gotoTab` call — it's the landing tab after login, matching the
desktop script's own Dashboard step. `gotoTab`'s selector (`header nav button, header [role=tab]`)
covers both the still-vanilla-CSS legacy nav (`<nav><button>`) and the MUI `AppShell`'s `Tabs`
(`role=tab`) — desktop-width devices in this matrix always see the `Tabs` variant (only `xs` shows
the Drawer), so this selector only ever needs to match one or the other per device, never both at
once.

- [ ] **Step 2: Add `.gitignore` entry**

```
# Mobile capture-matrix working artifacts -- not part of the shipped repo
docs/assets/screenshots/mobile/
```

- [ ] **Step 3: Run against the first 3 views on all 5 devices**

```bash
cd web && npm run dev   # terminal 1
```

```bash
PLAYWRIGHT_NODE_MODULES="$TEMP/pcbridge-playwright/node_modules" node docs/scripts/capture-mobile-media.mjs
```

Expected: 15 screenshots (3 views x 5 devices) in `docs/assets/screenshots/mobile/`, zero OVERFLOW
lines (Dashboard is MUI-migrated; Tenants and Find User are still vanilla CSS and creating a phone
nav from the still-flat `<nav>` is a KNOWN pre-existing gap -- if OVERFLOW appears for
`tenants-galaxy`/`tenants-iphone`/etc. or `finduser-*` at phone widths, that is the *expected*,
already-documented baseline from workstream 1's own live-verification pass, not a bug in this task;
confirm the exit code is 1 in that case and treat that as PASS for this step, not a blocker).
Zero OVERFLOW for `dashboard-*` at every device is the one hard requirement for this step, since
that view is already MUI-migrated and known-clean.

- [ ] **Step 4: Commit**

```bash
git add docs/scripts/capture-mobile-media.mjs .gitignore
git commit -m "Add mobile capture matrix: device profiles, overflow assertion, first 3 views"
```

---

### Task 3: Add the remaining 8 already-covered-in-desktop-script authenticated views

**Files:**
- Modify: `docs/scripts/capture-mobile-media.mjs`

**Interfaces:**
- Consumes: the `AUTHENTICATED_VIEWS` object shape established in Task 2 — add one entry per row
  below, following the exact pattern of Task 2's `tenants`/`finduser` entries.

- [ ] **Step 1: Add these 8 entries to `AUTHENTICATED_VIEWS`** (alphabetized by tab label for
  readability, insert in any order):

| Key | Steps |
|---|---|
| `approvals` | `await gotoTab(page, "Approvals");` then `await page.getByText("Mutating actions requested through MCP", { exact: false }).waitFor({ timeout: 20_000 });` |
| `contracts` | `await gotoTab(page, "Contracts");` then `await page.getByText("Managed Workstations", { exact: false }).waitFor({ timeout: 20_000 });` |
| `deploy` | `await gotoTab(page, "Deploy");` then `await page.getByText("Deploy a template", { exact: true }).waitFor({ timeout: 20_000 });` |
| `history` | `await gotoTab(page, "History");` then `await page.getByText("Deployment history", { exact: true }).waitFor({ timeout: 20_000 });` |
| `newhire` | `await gotoTab(page, "New Hire");` then `await page.getByText("New hire", { exact: true }).waitFor({ timeout: 20_000 });` |
| `offboard` | `await gotoTab(page, "Offboard");` then `await page.getByText("Offboard", { exact: true }).waitFor({ timeout: 20_000 });` |
| `templates` | `await gotoTab(page, "App Templates");` then `await page.getByText("7-Zip 24.08", { exact: false }).waitFor({ timeout: 20_000 });` |
| `workflows` | `await gotoTab(page, "Workflows");` then `await page.getByRole("button", { name: "Mailbox archive repair", exact: true }).waitFor({ timeout: 20_000 });` |

Each entry follows the exact shape of Task 2's `tenants` entry:
```js
newhire: async (page) => {
  await gotoTab(page, "New Hire");
  await page.getByText("New hire", { exact: true }).waitFor({ timeout: 20_000 });
},
```

- [ ] **Step 2: Run the full authenticated-view set on all 5 devices**

```bash
PLAYWRIGHT_NODE_MODULES="$TEMP/pcbridge-playwright/node_modules" node docs/scripts/capture-mobile-media.mjs
```

Expected: 55 screenshots (11 authenticated views x 5 devices). Review the console output's OVERFLOW
lines -- every legacy (non-MUI-migrated) view is expected to show overflow at phone widths
(`galaxy`/`iphone`/`pixel`/`fold-closed`); this is the known baseline, not a blocker for this task.
Record the exact list of which (view, device) pairs overflowed in the commit message's body (Step
3) -- this becomes the concrete punch list for future component-migration sub-projects.

- [ ] **Step 3: Commit**

```bash
git add docs/scripts/capture-mobile-media.mjs
git commit -m "$(cat <<'EOF'
Add the remaining 8 authenticated views to the mobile capture matrix

<paste the exact list of (view, device) pairs that showed OVERFLOW here,
one per line -- this is the known baseline for future migration work>
EOF
)"
```

---

### Task 4: Add auth-mode views (Login, Register, Security) via separate page contexts

**Files:**
- Modify: `docs/scripts/capture-mobile-media.mjs`

**Interfaces:**
- Consumes: `installApiMock(page, { authenticated, authModeOverride })` from `mock-api.mjs`
  (Task 1) — the `authModeOverride: "Local"` + `authenticated: false`/`true` parameters this task
  needs are already part of that function's signature.
- Produces: nothing new for later tasks — this is the last set of views.

- [ ] **Step 1: Add a separate driving path for the 3 Local-mode auth views**

These three views need `authModeOverride: "Local"` (the app only renders a Login screen at all in
Local mode) and, for Login/Register specifically, `authenticated: false` (so `/auth/me` 401s and
the login screen renders instead of skipping straight past it) -- unlike the 11
`AUTHENTICATED_VIEWS`, which all share one already-authenticated Dev-mode page per device. Add a
second per-device block in `main()`, after the `AUTHENTICATED_VIEWS` loop and before `await
page.close()`... actually, since these three need their own fresh page contexts (Login/Register
unauthenticated; Security needs a real sign-in flow first, mirroring the desktop script's
`securedPage` pattern), give them their own loop over `DEVICES`, structured as:

```js
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
  await gotoTab(securedPage, "Security");
  await securedPage.getByText("YubiKey 5C", { exact: false }).waitFor({ timeout: 20_000 });
  await captureView(securedPage, device, "security");
  await securedPage.close();
}
```

Call `await captureAuthViews(browser, device)` inside the existing `for (const device of DEVICES)`
loop in `main()`, right after `await page.close()` for the authenticated-views page. Add `login`,
`register`, `security` to the `viewFilter` check the same way the other views respect
`PCBRIDGE_CAPTURE_VIEWS` -- wrap the whole `captureAuthViews` call in `if (!viewFilter ||
["login", "register", "security"].some((v) => viewFilter.includes(v))) { ... }`, and skip
individual `captureView` calls inside it for views not in `viewFilter` if you want finer-grained
filtering (acceptable to skip this refinement and just filter at the whole-function level if it
keeps the code simpler -- use your judgment, this is a minor UX nicety for the `--filter` flow, not
a correctness requirement).

- [ ] **Step 2: Run the complete matrix (all 15 views x 5 devices)**

```bash
PLAYWRIGHT_NODE_MODULES="$TEMP/pcbridge-playwright/node_modules" node docs/scripts/capture-mobile-media.mjs
```

Expected: 75 screenshots. Login, Register, Dashboard should show zero OVERFLOW at every device
(all three are MUI-migrated). Security is NOT yet MUI-migrated (out of scope for workstream 1's
three flagship screens) -- expect it to show the same known-baseline overflow the other legacy
views show at phone widths.

- [ ] **Step 3: Commit**

```bash
git add docs/scripts/capture-mobile-media.mjs
git commit -m "Add Login, Register, and Security to the mobile capture matrix"
```

---

### Task 5: `docs/mobile.md`, `docs/scripts/README.md` addition

**Files:**
- Create: `docs/mobile.md`
- Modify: `docs/scripts/README.md`

**Interfaces:** none (documentation only).

- [ ] **Step 1: Create `docs/mobile.md`**

```markdown
# Mobile support

Partner Center Bridge's web client is being brought up to mobile-usable standards incrementally
(0.6.0 UX workstream). This doc tracks the current state and the tooling that verifies it.

## Supported device classes

| Class | Widths | Representative devices | What must hold |
|---|---|---|---|
| Phones | 360-430px | Galaxy S9+ (360), iPhone 15 (393), Pixel 7 (412) | No horizontal page scroll; dialogs full-screen |
| Folded foldables | 344px | Galaxy Z Fold cover screen | Same as phones -- narrowest supported viewport |
| Unfolded foldables / small tablets | 600-900px | Z Fold open, iPad Mini | Windowed dialogs; two-column layouts where they fit |
| Desktop | 900px+ | -- | Unchanged |

## Breakpoint strategy

MUI's default breakpoints (established in the [MUI design system foundation
spec](superpowers/specs/2026-08-19-mui-design-system-foundation-design.md)): `xs` (<600px) = phone,
`sm`-`md` (600-900px) = foldable/tablet, `lg`+ = desktop. `useIsPhone()`
(`web/src/hooks/useIsPhone.ts`) is the shared hook for any component that needs to branch on phone
vs windowed layout.

## Touch rules for future work

1. No hover-only affordances -- anything revealed on `:hover` must also be reachable on touch.
2. Every wheel/hover interaction needs a touch equivalent.
3. Interactive targets >= 40px on touch-primary layouts.
4. No horizontal page scroll, ever -- wide content (tables) scrolls inside its own `overflowX:
   auto` container (see `Dashboard.tsx`'s `TableContainer` for the established pattern), never the
   page itself.

## Running the matrix

The capture harness screenshots every current view across five touch device profiles with a fully
mocked API -- no backend or database needed:

```bash
cd web && npm run dev        # terminal 1
node docs/scripts/capture-mobile-media.mjs   # terminal 2
```

Playwright is loaded externally (never a package.json dependency) -- see
[docs/scripts/README.md](scripts/README.md) for setup. Output lands in
`docs/assets/screenshots/mobile/` (gitignored working artifacts; `PCBRIDGE_CAPTURE_OUT`
overrides). Filter while iterating:

```bash
PCBRIDGE_CAPTURE_DEVICES=galaxy PCBRIDGE_CAPTURE_VIEWS=dashboard,tenants node docs/scripts/capture-mobile-media.mjs
```

Review shots for: no horizontal page scroll, visible touch affordances, nothing clipped at the
right edge. A non-zero exit code means at least one (view, device) pair overflowed -- the console
output names exactly which.

## Current baseline (as of the mobile capture-matrix foundation landing)

MUI-migrated views (Dashboard, Login, Register, the app shell nav) pass cleanly at every device.
The remaining views (Tenants, Contracts, App Templates, Deploy, History, New Hire, Offboard,
Workflows, Approvals, Config Snapshots, Security, Find User) are still on the pre-MUI vanilla CSS
and are *expected* to show phone-width overflow until each migrates in its own sub-project -- this
is a known, tracked baseline, not a regression.

## Rules for future views

- Any new view or dialog must be added to `capture-mobile-media.mjs` (and `mock-api.mjs` if it
  needs new fixture data) and pass the matrix at 360px before merge.
- New views that intentionally still show overflow (not yet migrated) should say so in their PR
  description, same as the current baseline above.

## Known limitations

- This first pass captures each view's resting/landing state only, not deep interaction flows
  (a filled-in form's results, an open dropdown mid-selection). Deepen coverage for a specific view
  in that view's own migration sub-project if a real gap surfaces there.
- No CI gate yet -- this is a local/manual check for now, run before each PR that touches a view.
```

- [ ] **Step 2: Add a short section to `docs/scripts/README.md` documenting the new script**,
  following that file's existing structure for `capture-product-media.mjs` (read the file first to
  match its heading style and level exactly) -- cover: what it captures (all 15 views x 5 devices),
  where output lands, that it shares `mock-api.mjs` with the desktop script, and the same
  `PLAYWRIGHT_NODE_MODULES` setup instructions already documented for the desktop script (point to
  that existing section rather than repeating it verbatim).

- [ ] **Step 3: Commit**

```bash
git add docs/mobile.md docs/scripts/README.md
git commit -m "Add docs/mobile.md and document the mobile capture matrix in scripts/README.md"
```

---

### Task 6: Final verification (controller-run, no dispatch)

**Files:** none.

- [ ] **Step 1: `cd web && npm run build`** -- must stay green (this plan only touches
  `docs/scripts/` and `docs/mobile.md`, no `web/src` changes).

- [ ] **Step 2: Run the complete matrix one final time** and confirm the exit-code/OVERFLOW output
  matches the expected baseline recorded in Task 3/4's commits (4 clean views: dashboard, login,
  register, and the app shell nav visible in every authenticated screenshot; ~11 views showing
  known, already-documented overflow at phone widths).

- [ ] **Step 3: Spot-check 3-4 screenshots directly** (open the actual `.jpg` files in
  `docs/assets/screenshots/mobile/`) -- confirm they're real, legible renders (not blank/error
  pages), covering at least one clean view (`dashboard-galaxy.jpg`) and one known-overflowing
  legacy view (`tenants-iphone.jpg`) so the punch list is grounded in an actual visual, not just
  the numeric assertion.

- [ ] **Step 4: Confirm `ROADMAP.md`'s "Mobile UX testing" item can be updated** -- read its current
  wording and, if this sub-project's tooling now satisfies what it asks for (Playwright device
  emulation as a repeatable check, not a one-off), update or remove that entry to reflect the
  tooling now existing; if the entry's ambition also implies a CI gate (not yet built, per
  `docs/mobile.md`'s "Known limitations"), narrow the entry's wording instead of removing it
  outright -- use judgment based on the entry's exact current text.
