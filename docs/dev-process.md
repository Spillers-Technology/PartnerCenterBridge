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

## Log

| Unit | Task type | Author | Reviewer | Defects found | Defects real | Caught by tests instead | Est. tokens | Verdict |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
