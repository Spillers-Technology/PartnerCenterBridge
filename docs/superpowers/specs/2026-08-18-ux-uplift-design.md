# SPA UX Uplift — Design

Status: **Superseded 2026-08-19** by
[2026-08-19-mui-design-system-foundation-design.md](2026-08-19-mui-design-system-foundation-design.md).
The Radix/hand-rolled-tokens direction below was never implemented (this repo's MCP-server
deferral condition was satisfied, but no code was written against this spec). The successor spec
adopts Material UI instead and folds this document's two real findings — no confirm before any
destructive action, and two forms with no error handling at all — in as requirements rather than
losing them. Kept here for the audit findings and reasoning, not as a live implementation target.

~~Status: approved by Joey 2026-08-18, ready for implementation planning (implementation deferred
until the MCP server work lands — see Sequencing below).~~

## Motivation

A read-only audit of `web/src` (full findings in this spec's history, not reproduced here)
surfaced 15 concrete issues, two of them real bugs: **zero confirmation before any destructive
action anywhere in the app** (revoke access, remove a passkey, offboard a user, apply a known-fix
workflow, deploy) and **two forms (`Contracts.tsx`, `AppTemplates.tsx`) with no error handling at
all** — a failed request throws unhandled and the user sees nothing. The other thirteen findings
— inconsistent error formatting, a loading/empty-state flash on almost every tab, unlabeled form
fields, a 12-tab nav with no responsive handling, no confirm before the app's single riskiest
action (multi-tenant deploy) — all trace back to one root cause: there is no shared hook or
component for "submit with busy+error+labeled field," so each of ~13 components hand-rolled its
own version, and quality varies by whoever wrote it most carefully.

Beyond fixing what's broken, the explicit goal here is a step up in perceived quality — this tool
exists to be *less painful than native Microsoft admin tooling* (Entra admin center, Intune admin
center, Exchange admin center), and today's flat, inconsistent, confirmation-free UI doesn't earn
that contrast yet. "Feels good to use" is a real requirement of this spec, not a nice-to-have on
top of the bug fixes.

## Direction

Clean modern SaaS (Linear/Vercel/Stripe-dashboard register): generous whitespace, a real
type/color/spacing scale, subtle motion, dense-but-calm data tables. Two new frontend dependencies,
both deliberately narrow: **Radix UI primitives** (unstyled, accessible — Dialog, Toast,
DropdownMenu) for the pieces that are genuinely tedious to get right by hand (focus trapping,
escape-to-close, ARIA wiring), and nothing else — no separate motion library. Radix's primitives
expose `data-state` attributes (`open`/`closed`, etc.) that CSS transitions key off directly, so
motion is handled with plain CSS on top of Radix's state, not a second animation dependency. This
keeps the app's current zero-heavy-dependency character while buying real accessibility in the
two or three places hand-rolling it is a bad trade.

## Design tokens

New custom properties in `web/src/styles.css` (or a new `web/src/tokens.css` it imports — decided
in the implementation plan), replacing today's ad hoc magic values:

- **Color**: a neutral ramp (backgrounds, borders, text at several emphasis levels) plus an accent
  color and a semantic status ramp (success/warning/error/info) used consistently everywhere a
  finding, a deployment status, or a form error currently picks its own color. This is the
  single highest-leverage token set — `FindingStatus`, `DeploymentStatus`, and `WorkflowRunResult`
  already model exactly this state in the backend; the frontend should render all three through
  the same status-color vocabulary instead of three different ad hoc treatments.
- **Type scale**: a small fixed set of sizes/weights, replacing per-component font-size values.
- **Spacing scale**: 4px base unit, replacing arbitrary rem/px values scattered through the CSS.
- **Radius scale**: 2-3 steps (control, card, overlay).
- **Elevation**: two levels (card, overlay/dialog) via consistent shadow tokens.
- **Motion**: two or three duration/easing tokens (fast for hover/press feedback, standard for
  panel/dialog transitions), used by the CSS driving Radix's `data-state` transitions.

## Shared primitives

One new file per primitive under `web/src/`, each replacing N bespoke versions of the same pattern:

- **`useAsyncAction`** (hook) — wraps an async call with `busy`/`error`/`success` state and a
  single `run()` entry point. Replaces the hand-rolled `useState<T|null>` + `busy` + try/catch +
  `setError` block duplicated with small variations across essentially every mutating action in
  the app (`Contracts.tsx`, `AppTemplates.tsx`, `Tenants.tsx`, `NewHire.tsx`, `Offboard.tsx`,
  `Workflows.tsx`, `Security.tsx`, `DeployWizard.tsx`, `ConfigSnapshots.tsx`, `Login.tsx`,
  `Register.tsx`). This single hook is what makes `Contracts.tsx`/`AppTemplates.tsx`'s missing
  try/catch structurally impossible going forward rather than a bug to remember not to reintroduce.
- **`<Field>`** (component) — a labeled input/select/textarea wrapper, consistent spacing and
  label association. Replaces every placeholder-only input.
- **`<ConfirmDialog>`** (component, Radix Dialog) + a **`useConfirm()`** hook that returns a
  promise resolving true/false — every destructive action call site becomes `if (!(await
  confirm({...}))) return;` before the mutating call. This is the fix for the audit's top finding.
- **Toast system** (Radix Toast, one `<ToastProvider>` mounted once in `App.tsx` + a `useToast()`
  hook) — replaces every static inline `{error && <p className="error">...}` paragraph with a
  consistent, dismissible notification, and becomes where a real "done — here's what happened"
  success moment lives after New Hire/Offboard/Deploy complete (closing the audit's "no close-out
  state" finding) instead of leaving a stale populated form on screen.
- **`<Skeleton>`** (component) + a `status: "loading" | "empty" | "ready"` convention (rather than
  today's implicit "empty array = nothing to show yet") — closes the loading/empty-state flash
  finding structurally: a component can no longer render "No X yet" before its first fetch
  resolves, because loading and empty become distinct states instead of the same falsy check.

## Navigation

Replace the flat 12-tab top row with a left sidebar, grouped by intent:

- **Operate** — Dashboard, Find User, Workflows
- **Deploy** — Templates, Deploy, History
- **Manage** — Tenants, Contracts, New Hire, Offboard, Snapshots
- **Account** — Security (Local auth mode only, matching today's conditional tab)

This is structural, not cosmetic: today's `nav { display: flex }` with no `flex-wrap` and no
overflow handling is a plausible direct cause of "felt clunky" at real window widths — a sidebar
scrolls vertically instead of clipping horizontally, so the overflow bug can't recur regardless of
how many tabs the app grows to. The grouping also gives the app hierarchy it doesn't have today
(Templates -> Deploy -> History currently reads as three unrelated peers; grouped under *Deploy*
they read as one pipeline).

## Dashboard

Move from flat stat counts + raw tables to a card-based layout: each stat becomes a card using the
new semantic status colors (a "Tenants without delegation" count in the warning color, not the
same neutral treatment as "Total tenants"), and the "needs attention" list gets clearer visual
separation from "everything's fine" state. This is the app's first impression on every session —
worth the disproportionate care.

## Safety-net fixes (the two real bugs, not just polish)

- Every destructive action (`Tenants.revoke`, `Security.removePasskey`, `Offboard.submit`,
  `Workflows.fix`, `DeployWizard.deploy`) routes through `useConfirm()` before its mutating call.
- `Contracts.tsx`'s `create` and `AppTemplates.tsx`'s `create` move onto `useAsyncAction`, closing
  the unhandled-rejection gap as a side effect of adopting the shared primitive rather than as a
  separate patch — there is no longer a code path that can hand-roll its own (missing) error
  handling.

## Sequencing

Implementation is deferred until the MCP server work (`feat/mcp-server`) merges. Both initiatives
eventually touch `web/src/App.tsx`, and running them as parallel worktrees risks a real merge
conflict on that file for no benefit — this spec's implementation plan gets written once MCP
lands, on a branch cut fresh from `main` at that point (this branch, `feat/ux-uplift`, currently
holds only this spec commit and gets rebased or recreated then, whichever is cleaner at the time).

## Suggested phasing (for the implementation plan, not decided here)

Same pattern as the MCP work: one plan for the foundation (tokens, the five shared primitives,
Radix installed, the sidebar nav, the two safety-net fixes, one flagship retrofit — Dashboard — as
proof the pattern holds up in a real component), then a follow-up mechanical pass retrofitting the
remaining ~10 components onto the shared primitives, well-suited to `codex-luna`/`codex-terra`
dispatch once the pattern is established and reviewed, per `docs/dev-process.md`'s routing policy.
