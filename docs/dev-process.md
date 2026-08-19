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
- **A codex sandbox restriction can itself produce a false "pre-existing failure" or "permission
  denied" claim.** `windows.sandbox=unelevated`'s restricted-fs/restricted-network posture blocks
  some real operations (writing `.git/index.lock`, spawning a subprocess a test needs) that work
  fine unsandboxed — the implementer's own honest report of what it saw can still be wrong about
  *why*. Run the same command directly, unsandboxed, before accepting "pre-existing" or
  "permission blocked" as the explanation (Unit 3: a claimed 3-test GitSync failure and a claimed
  git permission block were both sandbox artifacts — 63/63 passed and the commit succeeded
  immediately when run directly).

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
| 2 | Tenant.McpApprovalMode + system-admin-only gate | Luna/low | Terra/high | 2 | 2 | 0 | (not separately logged) | 2 Important: a plan-mandated doc-text gap (the brief's own XML doc for HasRoleAsync didn't mention the non-Local all-access passthrough) and a missing Enum.IsDefined validation on SetMcpMode. Both real, both fixed in 1 round, re-review clean. First unit confirming Luna is fine for transcription-level work even on RBAC-adjacent code, as long as a stronger tier reviews it. |
| 3 | PendingAction entity + EF migration | Luna/low | Terra/high | 0 | 0 | 0 | (not separately logged) | Approved clean, no findings. Notable for a different reason: the implementer's own report contained two false claims (a git permission block, 3 "pre-existing" GitSync test failures) that were sandbox artifacts of `windows.sandbox=unelevated`, not real problems -- caught by the controller running both operations directly, unsandboxed, before trusting either claim. New standing rule added to this log as a direct result. |
| 6 | PendingActionsController + a controller-found audit-coverage regression fix + user-requested Retry feature | Terra/high | Sol/high | 1 Important + 1 residual same finding | 2 | 0 | (not separately logged) | Expanded beyond the written plan mid-sprint (the user asked for retry/reconciliation to actually be built, having initially agreed to defer it). Designing Retry surfaced a real, previously undetected regression: Task 4's atomic-claim rewrite silently bypassed AuditSaveChangesInterceptor for every transition except creation, and nobody -- not the controller, not three rounds of Task 4 review -- had checked whether the concurrency fix broke the audit requirement it wasn't being reviewed for. Fixed alongside Retry (which mirrors Task 4's atomic-claim pattern exactly). Sol/high's review (same tier for the same reason as Task 4: extending an already-reviewed state machine) found every post-transition audit write used the request's own cancellation token, so a cancellation at exactly the wrong moment could lose an audit record for an already-committed transition -- worse for Retry, which could strand a row invisible and permanently unretriable. Fixed using an existing precedent already in this codebase (`WorkflowsController.Record`'s `CancellationToken.None`), in 2 rounds: round 1 (Terra) fixed 3 of 4 spots, round 2 (Luna, genuinely mechanical once round 1's pattern existed) closed the last one and reasonably extended the fix to an adjacent claim statement beyond what was literally asked -- re-review confirmed the extension's reasoning was correct, not scope creep. |
| 4 | PendingActionService: stage/approve/reject/expire state machine | Luna/low (impl) -> Sol/high (fix round 1) -> Terra/high (fix rounds 2-3) | Terra/high | 1 Critical + 2 Important + 1 Minor | 4 | 0 | (not separately logged) | The most consequential unit so far. Terra/high's review found a genuine double-execution race: ApproveAsync/RejectAsync read Pending, mutated the tracked entity, invoked the real external mutation, and only saved afterward -- two concurrent callers could both pass the Pending check and both execute the real mutation before either save landed. Escalated to Sol/high per this repo's own routing policy (state-machine/security-relevant concurrency, regardless of diff size), which rewrote the claim as a single atomic conditional UPDATE. Took 3 fix rounds total: round 1 closed the Critical but only partially closed 2 Important findings; round 2 closed one (missing race test) and converted the other (reconciliation strategy) into a documented, deliberate scope boundary per a controller ruling grounded in this plan's own Global Constraints; round 2 also introduced a new test that a re-review correctly flagged as not proving a real race; round 3, after the controller traced the actual atomic-UPDATE semantics directly, confirmed no such race is structurally possible for approve-vs-expire (unlike approve-vs-reject, which is a real race between two independent writers) and replaced the vacuous test with an honest one plus a real adjacent gap (lazy expiry must never revert a terminal Executed state). Re-review independently confirmed the controller's code-level analysis was correct, not just asserted. |
| 5 | Revocable MCP PAT issuance: entity, OnTokenValidated revocation check, McpTokensController | Terra/high | Sol/high | 3 Important | 3 | 0 | (not separately logged) | Routed straight to Sol/high per policy (auth-pipeline-sensitive), and it earned the tier: found a real incident-recovery gap (a stolen PAT could mint its own replacement before revocation caught up, since PATs carry the same claims as login tokens and the token-management controller only required [Authorize]), an untested revocation boundary, and an audit-log bug (the usage-heartbeat update ran before HttpContext.User was populated, misattributing an AuditEvent to "anonymous" on every single authenticated request). One fix round closed all three at the code level; the re-review's residual concern (the extracted validator was unit-tested but the actual Program.cs wire-up never was) was closed by the controller directly, live, the same way Task 1 closed its own auth-context question -- minted a real PAT, used it successfully, confirmed the new self-management guard also worked (403 trying to manage its own token), revoked it via a real login token, then proved the identical PAT got a real 401 from the running server. Also surfaced ~300 leaked dotnet.exe processes accumulated over the session's build/test cycles, plausibly the cause of "process saturation" sandbox errors several earlier units reported -- cleared, and worth a periodic `dotnet build-server shutdown` + process check on long multi-task sessions going forward. |
