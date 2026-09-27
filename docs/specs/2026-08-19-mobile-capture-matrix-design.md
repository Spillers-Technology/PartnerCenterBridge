# Mobile/Desktop Capture-Matrix Foundation — Design

Status: approved by Joey 2026-08-19, ready for implementation planning.

Sub-project 1 of **Workstream 2** (per-component MUI migration + mobile verification), itself the
second of four workstreams toward a 0.6.0 UX release (workstream 1, the MUI design system
foundation, shipped in PR #20). This sub-project builds the verification tooling; it does not
migrate any new component. Directly closes the outstanding half of `ROADMAP.md`'s "Mobile UX
testing" item.

## Motivation

Nothing in this repo today catches mobile rendering regressions (text cutoff, horizontal overflow,
tap-target sizing) before a human notices them live — `ROADMAP.md` has tracked this gap since
before workstream 1 started. Workstream 1 proved, ad hoc, during its own final verification pass
(Task 9) that a real Playwright check catches real problems: a live 375px pass against the migrated
Dashboard confirmed zero page-level overflow (`scrollWidth === innerWidth`), while the same check
against the still-vanilla-CSS Tenants screen found real overflow (509px content in a 375px
viewport). That ad hoc script was disposable, single-use, and covered exactly one view. This
sub-project turns it into permanent, repo-owned tooling covering every current view, matching the
device-emulation approach `docs/mobile.md`-equivalent tooling already uses successfully in the
sibling AnchorDesk project.

## Direction

Extend, don't replace, the desktop capture script that already exists
(`docs/scripts/capture-product-media.mjs`, added for the docs site's screenshots). Its Playwright
loading strategy (`playwright-core` via `PLAYWRIGHT_NODE_MODULES`, or full `playwright` as a
documented fallback — never a `package.json` dependency), its mocked-`/api/*` approach, and its
`authModeOverride` pattern for capturing unauthenticated (Login/Register) and Local-mode (Security)
screens alongside the normal Dev-mode authenticated tabs are all sound and get reused, not
reinvented.

## Shared fixtures refactor

Extract `capture-product-media.mjs`'s inline mock data (tenants, contracts, templates, deployments,
workflows, dashboard, pending actions, search results, skus, groups, directory users,
provisioning template, auth/passkey/MCP-token fixtures, config snapshot runs/diffs) and its
`handleApi()` router into a new `docs/scripts/mock-api.mjs`, exporting:

- `installApiMock(page, { authenticated = true, authModeOverride = null } = {})` — wires
  `page.route("**/*", ...)` to intercept `/api/*` with the mock router and let everything else
  through. `authenticated: false` makes `/auth/me` return 401 (renders the login screen);
  `authModeOverride` forces `/auth/mode`'s response (`"Dev"` default, `"Local"` for
  Login/Register/Security captures) — mirrors the existing script's `authModeOverride` module
  variable exactly, just made an explicit parameter instead of shared mutable state, since the new
  mobile script runs many more page contexts than the desktop script's handful.
- `freezeAnimations(page)` — the existing `addStyleTag` transition/animation-killing snippet,
  factored out (currently duplicated three times in the desktop script).
- `loadPlaywright()`, `waitForServer(baseUrl)` — moved as-is; both scripts need them.

`capture-product-media.mjs` is refactored to import all of this instead of defining it inline. Its
own output (`docs/assets/screenshots/pcbridge-*.jpg`, the docs-site hero shots) does not change —
this is a pure extraction, verified by diffing the regenerated screenshots against the current
committed ones before/after.

## Device matrix

Five profiles, matching AnchorDesk's:

| Short name | Width | Source |
|---|---|---|
| `galaxy` | ~360px | Playwright's built-in `devices["Galaxy S9+"]` (or nearest available Galaxy preset) |
| `iphone` | ~393px | Playwright's built-in `devices["iPhone 15"]` |
| `pixel` | ~412px | Playwright's built-in `devices["Pixel 7"]` |
| `fold-closed` | 344px | Custom: `{ width: 344, height: 882, isMobile: true, hasTouch: true, deviceScaleFactor: 2 }` — no Playwright preset exists for a folded foldable's cover screen |
| `fold-open` | 717px | Custom: `{ width: 717, height: 512, isMobile: true, hasTouch: true, deviceScaleFactor: 2 }` — no Playwright preset for an unfolded foldable either |

Built-in device descriptors are used where they exist because they bundle accurate
`userAgent`/`isMobile`/`hasTouch`/`deviceScaleFactor` — real touch-device emulation, not just a
narrow viewport, matching the standing rule (already documented from the earlier MUI foundation
work) that hover-vs-touch behavior genuinely differs and narrow-window testing alone can miss it.

## New `docs/scripts/capture-mobile-media.mjs`

Drives all 15 current views — the 12 tabs in `App.tsx`'s `TABS` array, plus Security
(Local-mode-only tab), plus Login and Register (unauthenticated) — across all 5 device profiles.
For each (view, device) pair:

1. Navigate/select the tab (reusing `gotoTab()` from the existing script for authenticated tabs; a
   separate page context with `authModeOverride: "Local"` for Login/Register/Security, exactly
   mirroring how the desktop script already separates these).
2. Wait for real content (a stable, view-specific text/element — same approach the desktop script
   already uses per view).
3. **Assert no page-level horizontal overflow** —
   `page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)` — before
   writing the screenshot. This is the actual regression-catching mechanism; the screenshot is
   secondary evidence for human review. A failed assertion logs the view/device pair and the
   actual scroll-width-vs-viewport-width numbers, and the script's overall exit code reflects any
   failure (non-zero if any view/device pair overflowed).
4. Screenshot to `docs/assets/screenshots/mobile/<view>-<device>.jpg`.

**Deliberate scope simplification:** each view is captured at its resting/landing state (navigate,
wait, screenshot) — not the desktop script's deeper interaction flows (filling the diagnose form,
clicking through to results, etc.). Proving every view renders and doesn't overflow at every width
is this sub-project's job; deeper per-view interaction coverage (a specific dialog, a specific
form's mobile layout) is each component's own migration sub-project's job to add if a real gap
surfaces there — matches the "add real evidence, not aspirational completeness" principle from the
workstream 1 spec's own review findings.

Output directory `docs/assets/screenshots/mobile/` is gitignored (75 working artifacts, not
committed) — add it to `.gitignore` alongside the existing `docs/assets/screenshots/` handling
(that directory currently has no blanket ignore since the desktop hero shots *are* committed; the
`mobile/` subdirectory needs its own explicit ignore line).

Environment variables, matching the existing script's naming convention:

| Variable | Purpose |
|---|---|
| `PCBRIDGE_CAPTURE_BASE_URL` | (already exists) web client URL, default `http://127.0.0.1:5173` |
| `PCBRIDGE_CAPTURE_DEBUG` | (already exists) `1` for browser console logs |
| `PCBRIDGE_CAPTURE_OUT` | Mobile script only: override output directory |
| `PCBRIDGE_CAPTURE_DEVICES` | Mobile script only: comma list of device short names to filter |
| `PCBRIDGE_CAPTURE_VIEWS` | Mobile script only: comma list of view names to filter |

## `docs/mobile.md`

New doc, this repo's equivalent of AnchorDesk's `docs/mobile.md`:

- **Supported device classes** table (phones 360-430px, folded foldables 344px, unfolded
  foldables/small tablets 600-900px, desktop 900px+) and what must hold at each.
- **Breakpoint strategy** — points at the MUI defaults already established in the workstream 1
  spec (xs<600 phone, sm-md 600-900 tablet, lg+ desktop) rather than re-deriving it.
- **Touch rules for future work** — no hover-only affordances, every wheel/hover interaction needs
  a touch equivalent, interactive targets ≥40px on touch-primary layouts, no horizontal page
  scroll ever (wide content scrolls inside its own `overflowX: auto` container — already the
  pattern `Dashboard.tsx`'s `TableContainer` established in workstream 1).
- **Running the matrix** — the two-terminal `npm run dev` / `node
  docs/scripts/capture-mobile-media.mjs` invocation, the Playwright install fallback (pointing at
  `docs/scripts/README.md`, which already documents this), the filter env vars.
- **Rules for future views** — a new view or dialog must be added to
  `capture-mobile-media.mjs` (and `mock-api.mjs` if it needs new fixture data) and pass the matrix
  at 360px before merge. Mirrors AnchorDesk's identical rule.
- **Known limitations** — this first pass captures resting/landing state only, not deep
  interaction flows (stated plainly, not hidden).

`docs/scripts/README.md` gets a short addition documenting `capture-mobile-media.mjs` alongside the
existing `capture-product-media.mjs` entry, following that file's existing structure.

## Testing / verification for this sub-project

No new component code, so no vitest/RTL tests. Verification is the matrix run itself:

1. `npm run build` in `web/` (unaffected — this sub-project only touches `docs/scripts/` and adds
   `docs/mobile.md`) stays green.
2. Regenerate the desktop screenshots (`node docs/scripts/capture-product-media.mjs`) after the
   `mock-api.mjs` extraction and diff them against the currently-committed
   `docs/assets/screenshots/pcbridge-*.jpg` files — must be pixel-identical (or visually
   indistinguishable; minor JPEG re-encoding differences are acceptable, structural differences are
   not), proving the refactor didn't change desktop capture behavior.
3. Run the new mobile matrix (`node docs/scripts/capture-mobile-media.mjs`) for real, against the
   real running dev server. Expected result, stated as the acceptance criterion up front: the 4
   already-MUI views (shell nav visible in every authenticated screenshot, Login, Register,
   Dashboard) pass the overflow assertion on all 5 devices; the ~11 legacy views are *expected* to
   fail it on phone-width devices (this is the known, already-confirmed baseline — Tenants
   specifically measured at 509px content in a 375px viewport during workstream 1's own
   verification pass) — the script's non-zero exit code on this first real run is *correct*
   behavior, not a bug to chase, and becomes the concrete, numbered punch list each future
   component-migration sub-project works through.

## Dev process

Per `docs/dev-process.md`'s routing policy: this is real implementation work with an established
pattern to mirror (the existing desktop script, AnchorDesk's own capture-matrix design) — no
from-scratch architectural decision left. Terra/high implements (multiple files, moderate
complexity in the device-profile/fixture-extraction logic), Terra/high reviews adversarially.
