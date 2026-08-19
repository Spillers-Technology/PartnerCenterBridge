# Roadmap

Things we want to come back to. Not scheduled, not sequenced -- just tracked so they don't get lost.

## Mobile UX testing

Right now nothing in this repo catches mobile rendering regressions (text cutoff, overflow, tap
target sizing) before a human notices them live. Want the same assurance `anchordesk` is aiming
for: Playwright with device emulation (viewport + user-agent presets for common phones/tablets) as
a first-class part of the test suite, not an afterthought -- run alongside the existing `npm run
build` check, not just invoked manually.

## Config Snapshots v2

Current Config Snapshots (section/whole-tenant diff, workbooks, git sync) works but is limited.
No specifics yet -- revisit once there's a concrete pain point driving the next iteration.
