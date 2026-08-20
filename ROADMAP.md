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
