# Mobile support

Partner Center Bridge's web client is being brought up to mobile-usable standards incrementally
(0.6.0 UX workstream). This doc tracks the current state and the tooling that verifies it.

## Supported device classes

| Class | Widths | Representative devices | What must hold |
|---|---|---|---|
| Phones | 320-430px | Galaxy S9+ (320), iPhone 15 (393), Pixel 7 (412) | No horizontal page scroll; dialogs full-screen |
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

## Current baseline

The matrix produces 75 captures (15 views x 5 devices).

Thirteen views pass cleanly at every device: Dashboard, Find User, Deploy, New Hire, Offboard,
Login, Register, Workflows, Approvals, App Templates, History, **Config Snapshots**, and
**Security**. The last two are newly migrated (Account group, workstream 2 of the 0.6.0 MUI
migration) -- both were in the original pre-migration overflow baseline and now pass the full
5-device matrix cleanly (`PCBRIDGE_CAPTURE_VIEWS=snapshots,security`, zero-overflow exit code),
confirmed directly, not assumed. Workflows/Approvals (Operate group) and App Templates/History
(Deploy pipeline group) were migrated and verified the same way in the two sibling PRs merged
just before this one.

Two views still show the known pre-migration overflow baseline, tracked work rather than a
regression, owned by the one remaining workstream-2 sub-project (Manage): Tenants and Contracts.
This is 10 overflowing view/device pairs, down from the original 31.

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
