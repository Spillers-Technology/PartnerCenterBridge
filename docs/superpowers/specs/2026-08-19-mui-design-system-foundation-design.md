# MUI Design System Foundation — Design

Status: approved by Joey 2026-08-19, ready for implementation planning.

Supersedes [2026-08-18-ux-uplift-design.md](2026-08-18-ux-uplift-design.md) (Radix + hand-rolled
tokens direction, never implemented). This spec is **Workstream 1 of 4** toward a 0.6.0 UX release:

1. **This spec** — design language + MUI foundation + app shell + shared primitives.
2. Per-component MUI migration, interleaved with a mobile/desktop verification capture matrix
   (AnchorDesk-style: Playwright, five device profiles, document-width overflow assertions).
3. i18n — EN default + ES/ZH/FR/DE, layered in per-component once each is MUI-migrated.
4. Micro-animations — mostly free via MUI's `Fade`/`Grow`/`Collapse`/`Slide`, folded into
   workstreams 2-3 rather than a standalone pass.

Each later workstream gets its own spec once the one before it lands.

## Motivation

Two threads converge here. First, a read-only audit of `web/src` (full findings in the superseded
spec's history) found real bugs: **zero confirmation before any destructive action** anywhere in
the app (revoke access, remove a passkey, offboard a user, apply a workflow fix, deploy) and **two
forms with no error handling at all** (`Contracts.tsx`, `AppTemplates.tsx` — a failed request
throws unhandled and the user sees nothing), plus a flat 12-tab nav with zero responsive handling
that overflows below ~900px. Second, the ambition has grown beyond patching those: adopt Material
UI as the app's design system outright, replacing the current hand-rolled `styles.css`, to buy a
real breakpoint system, accessible dialog/menu/table primitives, and a coherent visual language in
one move instead of hand-building each piece.

This tool exists to be *less painful than native Microsoft admin tooling* (Entra admin center,
Intune admin center). Today's flat, inconsistent, confirmation-free UI doesn't earn that contrast.

## Direction

Adopt `@mui/material` (latest, v9.x — supports React 17/18/19, no conflict with this repo's React
19.2 pin) + `@emotion/react`/`@emotion/styled` (MUI's peer deps) + `@mui/icons-material`. This is a
deliberate reversal of the superseded spec's "stay dependency-light" philosophy — the new goal is a
complete, opinionated design system, not a minimal accessible-primitives layer. MUI's `sx`
breakpoint system *is* the responsive strategy; its `Dialog`/`Snackbar`/`TextField`/`Skeleton`
components directly replace what the superseded spec had to hand-build with Radix.

Breakpoints use MUI's defaults (xs<600, sm 600, md 900, lg 1200, xl 1536) — the same values
AnchorDesk's `docs/mobile.md` already documents for its own MUI-based client, so both products
share one device-width mental model even though their palettes differ.

## Design tokens (theme.ts)

Single `web/src/theme.ts`, dark-first, the source of truth for every color/spacing/radius value
going forward (no more per-component magic values in CSS).

**Surfaces — kept, already solid:**

| Token | Value |
|---|---|
| `background.default` | `#0f172a` |
| `background.paper` | `#1e293b` |
| `divider` | `#334155` |
| `text.primary` | `#e2e8f0` |
| `text.secondary` | `#94a3b8` |
| `shape.borderRadius` | `8` |

**Primary — replaced.** The current `--accent` (`#38bdf8`, Tailwind sky-400) is numerically fine
but reads as the single most common default-dashboard blue. Moved to an indigo family for a more
distinctive identity while keeping the cool tone:

| Token | Value | Contrast (bg / panel) |
|---|---|---|
| `primary.light` | `#a5b4fc` | hover/highlight states |
| `primary.main` | `#818cf8` | 8.6:1 / 6.8:1 |
| `primary.dark` | `#6366f1` | filled buttons, pressed state |

**Status trio — shifted one tonal step brighter (500→400), applied systematically.** The current
`--err` (`#ef4444`, red-500) measures **3.89:1 against `background.paper`** — fails WCAG AA's
4.5:1 for normal text, since error badges/text render on panel surfaces, not directly on `bg`. All
three status colors move to their 400-shade for a consistent, principled rule rather than a
one-off patch:

| Token | Old | New | Contrast vs panel |
|---|---|---|---|
| `success.main` | `#22c55e` | `#4ade80` | 6.4:1 |
| `warning.main` | `#f59e0b` | `#fbbf24` | 6.8:1 |
| `error.main` | `#ef4444` | `#f87171` | 5.3:1 (was 3.89 — now passes) |

**Accessibility rule for every status usage going forward:** never color-only (a real colorblind
concern with red/green) — pair with an icon or label in chips/badges, not hue alone.

## App shell

Replace the `<nav>` row of 12+ buttons (`App.tsx`) with:

- **`AppBar` + scrollable `Tabs`** (`variant="scrollable" scrollButtons="auto"`) at `sm`+ widths.
- **`Drawer`** behind a hamburger `IconButton` at `xs` (phone) width — 12 tab labels cannot fit one
  row at 360px regardless of styling, so this is a structural swap, not a style tweak.
- User/sign-out folds into an account `Menu` (icon-triggered) instead of inline text + button,
  reclaiming header width on phone.
- A shared **`useIsPhone()`** hook (`useMediaQuery(theme.breakpoints.down('sm'))`) goes in now,
  since every subsequent dialog migration in workstream 2 needs it for `fullScreen={isPhone}`.

This directly closes the superseded spec's nav-overflow finding — a `Drawer` scrolls vertically
instead of a flat row clipping horizontally, so the bug structurally cannot recur regardless of how
many tabs the app grows to. (The superseded spec's alternative fix, a permanent left sidebar, is
dropped in favor of MUI's own responsive Tabs/Drawer pattern, consistent with adopting MUI as the
whole design system rather than one custom nav component on top of it.)

## Shared primitives

Carried forward from the superseded spec's reasoning (still the right diagnosis — one missing hook
is why ~13 components each hand-rolled their own busy/error handling), rebuilt on MUI instead of
Radix. Two of the original five are no longer needed because MUI ships them natively:

- **`useAsyncAction`** (hook, unchanged design) — wraps an async call with `busy`/`error`/`success`
  state and a single `run()` entry point. Replaces the duplicated `useState` + try/catch + manual
  `setError` block in `Contracts.tsx`, `AppTemplates.tsx`, `Tenants.tsx`, `NewHire.tsx`,
  `Offboard.tsx`, `Workflows.tsx`, `Security.tsx`, `DeployWizard.tsx`, `ConfigSnapshots.tsx`,
  `Login.tsx`, `Register.tsx`. Makes `Contracts.tsx`/`AppTemplates.tsx`'s missing error handling
  structurally impossible going forward, not just fixed once.
- **`useConfirm()`** + a `<ConfirmDialog>` built on MUI's `Dialog` (not Radix) — every destructive
  action call site becomes `if (!(await confirm({...}))) return;`. Same fix as the superseded
  spec's top finding, different primitive underneath.
- **Toast/notification system** via MUI's `Snackbar` + `Alert` (no extra dependency — `notistack`
  or similar is unnecessary since MUI ships the pieces directly) + a `useToast()` hook, mounted
  once in `App.tsx`. Replaces every static inline `{error && <p className="error">...}` and becomes
  the "done — here's what happened" close-out moment after New Hire/Offboard/Deploy complete.
- **No custom `<Field>` needed** — MUI's `TextField`/`Select`/`FormControl` already provide
  label association, helper text, and error state built in.
- **No custom `<Skeleton>` needed** — MUI ships `Skeleton` directly. Pairs with a
  `status: "loading" | "empty" | "ready"` convention per component (replacing today's implicit
  "empty array = nothing to show yet") to close the loading/empty-state flash finding structurally.

**Structural rule for workstream 2:** every component migration to MUI must adopt `useAsyncAction`
(and `useConfirm` if it has a destructive action) as part of that migration, not as a follow-up
pass — this is how the safety-net fixes actually land across all ~13 components rather than staying
scoped to whatever this spec touches directly.

## Scope boundary — what this spec actually migrates

To keep this bounded, only three surfaces move to MUI in this workstream:

- **Theme + app shell** (`App.tsx` nav/header) — every other screen still renders through it, so it
  has to exist first.
- **Login + Register** — smallest, standalone screens; good proof that theme + `useAsyncAction`
  work end-to-end for a real async form with error states.
- **Dashboard** — the app's first impression every session; exercises the status-color tokens
  directly (a "needs attention" list, tenant/deployment stat tiles) and proves the primitives hold
  up in a data-heavy, non-trivial component, per the superseded spec's own reasoning for choosing
  it as the flagship retrofit.

Everything else (Tenants, Contracts, App Templates, Deploy, Deployments, New Hire, Offboard,
Workflows, Approvals, Config Snapshots, Security, Find User — ~10 components) stays on the current
vanilla CSS during this workstream and migrates one-by-one in workstream 2, each migration pairing
one component with its mobile-capture-matrix entry. Old `styles.css` and new MUI components coexist
safely — MUI scopes its classes via Emotion hashes, no collision with `.grid`/`.row`/`.badge`.

## Testing / verification for this workstream

No capture-matrix build yet (workstream 2's job — it needs real migrated views to test, not just
shell chrome). For this spec: a manual phone-width (375px) devtools pass on the new shell + Login +
Register + Dashboard, plus confirming `npm run build` and existing tests stay green. `dotnet
build`/`dotnet test` are unaffected (frontend-only change).

## Dev process

Per `docs/dev-process.md`'s routing policy: Terra/high implements the theme + shell + three-screen
migration (real implementation work, no from-scratch architectural decision left — this spec *is*
the design decision), Terra/high reviews adversarially, escalate to Sol/high only if the reviewer
flags something concurrency/security-shaped (unlikely for a frontend-only change, but the app shell
touches auth-mode branching in `App.tsx`, which is worth a second look if touched).
