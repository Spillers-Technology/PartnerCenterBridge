# Workstream 2: Per-Component MUI Migration -- Design

Status: approved by Joey 2026-08-20, ready for parallel implementation planning.

Implements workstream 2 of the 0.6.0 UX push described in
[2026-08-19-mui-design-system-foundation-design.md](2026-08-19-mui-design-system-foundation-design.md)
(workstream 1, merged, PR #20) and verified against
[2026-08-19-mobile-capture-matrix-design.md](2026-08-19-mobile-capture-matrix-design.md)'s tooling
(merged, PR #21). This spec covers migrating the remaining ~11 components to MUI. Workstream 3
(i18n) and workstream 4 (animations) are out of scope here -- animations fold into this workstream
opportunistically (MUI's `Fade`/`Grow`/`Collapse` cost nothing extra when already touching a
component), but are not a separate acceptance bar.

## Why four independent sub-projects, not one

The ~11 remaining components are file-disjoint (`web/src/components/*.tsx`, one file per screen, no
shared file among them beyond `AppShell.tsx`/`theme.ts`/the three hooks -- all already merged and
frozen). Grouped by the app's own tab structure into four sub-projects that can implement, test, and
PR independently and in parallel:

| Group | Components | Baseline capture-matrix status |
|---|---|---|
| **Operate** | `UserSearch.tsx` (Find User), `Workflows.tsx`, `Approvals.tsx` | Find User passes; Workflows, Approvals overflow |
| **Deploy pipeline** | `AppTemplates.tsx`, `DeployWizard.tsx` (Deploy), `Deployments.tsx` (History) | Deploy passes; App Templates, History overflow |
| **Manage** | `Tenants.tsx`, `Contracts.tsx`, `NewHire.tsx`, `Offboard.tsx` | New Hire, Offboard pass; Tenants, Contracts overflow |
| **Account** | `ConfigSnapshots.tsx`, `Security.tsx` | Both overflow |

("Passes" = already clean in the `docs/mobile.md` baseline despite not being MUI-migrated yet --
migrating it must not regress that.)

## The migration recipe (identical across all four groups)

Every component migration mirrors the pattern already proven and merged in workstream 1. Read these
four files before starting any task -- they are the spec, not just inspiration:

- `web/src/theme.ts` -- do not add new tokens; the palette is closed.
- `web/src/components/AppShell.tsx` -- layout chrome, already wraps every screen.
- `web/src/components/Login.tsx` + `Login.test.tsx` -- the reference for a form screen:
  `useAsyncAction` per async operation, `TextField`/`Button`/`Alert`, RTL test using
  `getByLabelText`/`getByRole`, API module mocked with `vi.mock`.
- `web/src/components/Dashboard.tsx` + `Dashboard.test.tsx` -- the reference for a data/table
  screen: `Table`/`TableContainer` (the established no-horizontal-page-scroll pattern per
  `docs/mobile.md`), `Chip` for status with a paired text label (never color-only), `Skeleton` for
  the loading state, a `status: "loading" | "empty" | "ready"` convention.

**Every component in this workstream must, as part of its migration (not a follow-up pass):**

1. Adopt `useAsyncAction` (`web/src/hooks/useAsyncAction.ts`) for every async call, replacing any
   hand-rolled `useState` busy/error pattern.
2. Adopt `useConfirm` (`web/src/hooks/useConfirm.tsx`) for every destructive action (revoke access,
   remove a passkey, offboard a user, apply a workflow remediation, deploy) --
   `if (!(await confirm({...}))) return;` before the mutating call. Identifying which actions in a
   given component are destructive is part of the task, not something this spec enumerates
   component-by-component.
3. Adopt `useToast` (`web/src/hooks/useToast.tsx`) for a "done -- here's what happened" close-out
   after a state-changing action completes (especially New Hire, Offboard, Deploy).
4. Replace hand-rolled forms/tables with MUI `TextField`/`Select`/`FormControl`/`Table` equivalents;
   labels associate via MUI's built-in `label` prop, not a bare `<label>`.
5. Never color-only status indication -- pair every status `Chip`/badge with a text label.
6. Any wide content (a table) scrolls inside its own `TableContainer`, never the page.
7. ASCII-only string literals in every `.tsx`/`.ts` file (repo-wide convention, see root
   `CLAUDE.md` -- non-ASCII in a compiled C# literal has shipped as mojibake before; the same
   caution applies here for consistency even though the TS toolchain isn't known to have the bug).
8. RTL test file per component, mirroring `Login.test.tsx`'s structure: mock `../api` (and any other
   external module the component imports), render wrapped in `<ThemeProvider theme={theme}>`,
   assert on real user-facing text/labels/roles, cover the happy path, the error path (via a
   rejected mock), and -- if the component has a destructive action -- that the `useConfirm` dialog
   gates the mutating call (cancel does not call it; confirm does).
9. `styles.css` and any not-yet-migrated component are untouched by a given group's PR --
   cross-group file changes are out of scope and a sign the group boundary was drawn wrong.

## Verification

Each group's PR must, before opening:

- `cd web && npm run build` clean (no TypeScript errors).
- `cd web && npm test` clean (existing suite plus every new component test file).
- The capture matrix run for exactly this group's views, filtered via
  `PCBRIDGE_CAPTURE_VIEWS=<comma-separated-view-keys>` (see `docs/scripts/README.md` /
  `docs/mobile.md` for the exact invocation -- `cd web && npm run dev` in one terminal,
  `node docs/scripts/capture-mobile-media.mjs` in another). View keys match the tab keys in
  `App.tsx` (`finduser`, `workflows`, `approvals`, `templates`, `deploy`, `history`, `tenants`,
  `contracts`, `newhire`, `offboard`, `snapshots`, `security`).
- Every view that was in the 31-pair overflow baseline (see table above) must pass cleanly
  (zero-overflow exit code) after migration. Every view that already passed must still pass --
  migrating it must not introduce a regression.
- `docs/mobile.md`'s baseline table and the workstream-1 scope-boundary section it links to get
  updated to reflect the group's views moving from "not yet migrated" to "migrated" -- each group's
  PR carries that doc update for its own views only.

## Dev process

Per `docs/dev-process.md`'s routing policy: Terra/high implements each component (real
implementation work following an established, concrete pattern -- no from-scratch design decision
left, this spec and the Login/Dashboard reference files *are* the design). Terra/high reviews
adversarially. Escalate to Sol/high for `Security.tsx` specifically (auth/session-adjacent,
regardless of diff size, per the routing policy's own standing rule) and for any component where a
reviewer flags something concurrency- or security-shaped.

## Scope boundary

Out of scope for all four groups: `AppShell.tsx`, `theme.ts`, the three shared hooks, `App.tsx`'s
routing (tab keys and their component mapping stay exactly as they are), i18n, and any animation
work beyond what MUI provides for free on components already being touched.
