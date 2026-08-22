# Roadmap

Things we want to come back to. Not scheduled, not sequenced -- just tracked so they don't get lost.

## Mobile UX testing

The manual half of this landed: `docs/scripts/capture-mobile-media.mjs` screenshots all 15 current
views across five touch device profiles (Galaxy/iPhone/Pixel/folded-foldable/unfolded-foldable) and
asserts no page-level horizontal overflow at each -- see `docs/mobile.md`. Still open: this only
runs manually today, not as a first-class part of the test suite alongside `npm run build`/`npx
vitest run` -- wiring it into CI (or at least a pre-PR automated gate) is the remaining piece.

## Config Snapshots v2

Current Config Snapshots (section/whole-tenant diff, workbooks, git sync) works but is limited.
No specifics yet -- revisit once there's a concrete pain point driving the next iteration.

## Contracts: desired-app editor

**In progress** -- design spec written:
`docs/superpowers/specs/2026-08-22-contracts-desired-app-editor-design.md` (branch
`feat/contracts-desired-apps`). Surfaced by the usability workstream's friction survey as a feature
gap, not a friction fix, so it was deliberately left out of that workstream and picked up as its
own piece of work afterward.

## AppShell: route history / deep links

Tabs are plain component-swap state, not real routes -- there's no browser history, no deep
linking to a specific tab, and refresh/back/bookmark all lose whatever screen (and any in-progress
draft or show-once output) was open. Also surfaced by the usability workstream's survey; explicitly
scoped out of that pass as architectural rather than a friction fix. Would need routing wired
through `App.tsx`'s tab state and each screen's own local state reconciled with URL params.

## RBAC: real roles with delegation

Today's only privileged flag is `ITenantAccessService.IsSystemAdmin`, all-or-nothing and not
tenant-scoped (see `CLAUDE.md`'s own standing warning about not letting it bypass per-tenant
`TenantAccessGrant` checks). As more admin-gated actions accumulate (App Templates, and now the
Contracts desired-app editor), worth a real review of what roles actually make intuitive sense here
and whether delegation below "full system admin" is worth modeling properly. Flagged by the user
as something to do before the next version release, not urgent enough to block current work.

## Design language: "quest-driven" playful nudges

The Contracts desired-app editor's disabled-template state (a package-less app template, hidden by
default) uses a deliberately playful, actionable "quest chip" nudge -- amber, encouraging,
literally the fix-it action rather than just an explanation. The user liked this enough to wonder
whether it's worth applying more broadly as a mental design model across the app (turning "this is
disabled/incomplete" states into inviting, actionable nudges rather than flat disabled UI). Not
committing to that as a system yet -- revisit once there's more than one example to generalize from.
