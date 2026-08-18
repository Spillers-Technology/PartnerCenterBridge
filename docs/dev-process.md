# Dev Process Log

Records every round where implementation or review work was routed to Codex CLI (`codex exec`,
tiers Luna/Terra/Sol — see `.claude/agents/codex-{luna,terra,sol}.agent.md`), so the routing
policy below stays derived from measurements on this repo rather than imported wholesale from
another project. Adapted from the routing policy and findings first established in a separate
project's `dev-process.md`; kept here going forward for PartnerCenterBridge specifically.

## Routing

- **Luna** — mechanical work: tightly-specified, tests or an exact shape already given, an
  established pattern to mirror. Never ship unreviewed.
- **Terra** — default consultant: real implementation work without a from-scratch design
  decision, and the default adversarial reviewer for anything Claude or Luna authored.
- **Terra/high** — escalation: nontrivial logic, more than a couple files, anything Terra/medium
  would be guessing on.
- **Sol/high** — architecture, security, tricky debugging, and anything touching threads,
  lifetimes, shutdown ordering, or a resource that must be released, regardless of diff size.
- **Sol/ultra** — multi-file implementation (5+ files) or a broad review pass. As an
  *implementer*, requires the user's explicit approval in-session before dispatch — never
  self-initiated.

These are starting priors, not fixed law — revise whichever rule the log below stops supporting.

## Standing rules carried over

- **A green suite is evidence about the tests, not the code.** Ask reviewers to grade the tests
  explicitly, not just the implementation.
- **Review reads diffs, so it finds defects that live in diffs.** Bugs in lifetime, deployment,
  startup/shutdown ordering, or cross-run state need live verification against the running app,
  not just a diff read — budget for that separately.
- **A review finding can be right about the problem and wrong about the fix.** Adjudicate the
  problem, choose the fix yourself.
- **When a reviewer indicts the spec/design rather than the code, believe it.**
- **Re-read diffs after a fix round** — regressions enter there as often as anywhere else.
- **Verify before trusting, in both directions.** Reproduce a reviewer's finding directly before
  accepting it; reproduce a fix's effect directly before calling it closed. A "red" report can be
  an artifact of the test run, not the code, just as often as a "green" one can be hiding a real
  defect — check which before acting on either.

### First-unit tooling bug — found by reading the background process's own output

Task 1's first dispatch attempt (Terra, implement role) sat "in progress" for over 30 minutes.
The dispatching subagent kept re-arming a wait/poll loop on faith rather than checking the
underlying `codex exec` background shell's actual exit status. Reading that process's own output
file directly (`bda6yq61z.output`, 8 lines) showed it had died in under a second: `error: the
argument '--sandbox <SANDBOX_MODE>' cannot be used with '--approve-for-me'`, exit code 2. All
three `.claude/agents/codex-*.agent.md` implement-role commands paired `-s workspace-write` with
`--approve-for-me`, which this codex CLI version rejects as mutually exclusive —
`--approve-for-me` already implies the workspace-write sandbox on its own. Fixed in all three
files, then live-verified (not just assumed) with a throwaway `codex exec ... --approve-for-me`
call that wrote a real file, confirming `sandbox: workspace-write` in its own startup banner.

**Rule, restated from the source project's own log and now confirmed on this one: when an
integration fails, read the other system's logs before touching your own code, and before
trusting a "still running" status on faith.** A dispatcher polling a background task must check
that task's actual output/exit code, not just re-arm another wait.

## Log

| Unit | Task type | Author | Reviewer | Defects found | Defects real | Caught by tests instead | Est. tokens | Verdict |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | MCP transport spike: SDK wiring, one diagnostic tool, live auth-context proof | Terra/high | Sol/high | 1 | 1 | 0 | ~370,000 across all dispatches (implementer + two blocked/retried reviewer attempts + direct review + re-review) | See notes below. First unit fully routed through Codex; also the first live validation of the "read the other system's logs" rule on this repo (two real infra bugs found and fixed by reading raw process output directly: a `--sandbox`/`--approve-for-me` CLI conflict, and a later Windows-sandbox launcher failure), plus one real Critical finding from Sol/high that exposed a controller-introduced verification gap (Dev-mode shortcut vs. the brief's specified Local-mode proof), fixed with real evidence and re-reviewed clean. |
