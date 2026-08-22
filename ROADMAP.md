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

Contracts can only be named and previewed today -- there's no way to edit a contract's desired-app
list from the UI. `AppTemplates.tsx` already carries a `contractId` on its model, but the create
form never sets it, and the provisioning-template API (`api.ts`) isn't wired to any component.
Surfaced by the usability workstream's friction survey as a feature gap, not a friction fix, so it
was deliberately left out of that workstream. Needs a contract detail flow with desired-app editing
and a dry-run handoff before this is worth picking up.

## AppShell: route history / deep links

Tabs are plain component-swap state, not real routes -- there's no browser history, no deep
linking to a specific tab, and refresh/back/bookmark all lose whatever screen (and any in-progress
draft or show-once output) was open. Also surfaced by the usability workstream's survey; explicitly
scoped out of that pass as architectural rather than a friction fix. Would need routing wired
through `App.tsx`'s tab state and each screen's own local state reconciled with URL params.
