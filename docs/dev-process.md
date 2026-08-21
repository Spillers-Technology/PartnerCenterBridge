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

## Routing variance: Codex CLI account-wide outage, 2026-08-20

While running workstream 2 of the 0.6.0 MUI migration (four parallel component-group PRs: Operate,
Deploy pipeline, Manage, Account), the Account group's first `codex-terra` dispatch
(`ConfigSnapshots.tsx`) failed immediately with `ERROR: You've hit your usage limit ... try again at
Aug 21st, 2026 8:57 AM`. The group-controller subagent verified this itself, directly and
unsandboxed (not just trusting the dispatcher's own report, per this log's own standing rule below)
with a minimal `codex exec -m gpt-5.6-luna ... "PING-OK"` call that failed identically — confirming
an account-wide lockout, not a Terra-tier or dispatcher-specific issue. The Deploy pipeline group
independently hit the exact same error (same reset timestamp) on its own first `codex-terra`
dispatch (`AppTemplates.tsx`), corroborating it.

Correct behavior on discovery: the Account group's controller stopped and reported up rather than
silently substituting itself as implementer — this repo's own log already documents that exact
anti-pattern ("Second tooling bug" below) as something to avoid. The Deploy pipeline group's
controller, by contrast, hit the same failure moments earlier and had already self-implemented
`AppTemplates.tsx` directly (no Codex, no dispatch) before the "stop and report" instruction
reached it — an honest deviation under the same pressure, disclosed rather than hidden, but not the
process this repo runs.

**User's ruling (2026-08-20):** rather than wait out the ~12-hour reset or pause all four groups,
switch the *implementer and reviewer* roles from Codex CLI to dispatched Claude subagents for the
remainder of this workstream, mirroring the existing Luna/Terra/Sol structure one rung over onto
Claude's own model tiers instead of inventing a new policy — "do it smart for token spend," per the
user's own framing. Explicitly logged as an opportunity to compare Claude-subagent output against
this log's existing Codex-routed units, not just a stopgap — hence this section.

**Revised routing for the duration of the outage** (each Claude tier fills the role its Codex
counterpart played, at the same trigger conditions already defined above):

| Codex tier | Claude equivalent | Same trigger as |
|---|---|---|
| Luna | **Haiku 4.5** subagent | mechanical, tightly-specified, an established pattern to mirror exactly, no destructive action or error-handling gap in scope. Never ship unreviewed, same as Luna. |
| Terra / Terra-high | **Sonnet** subagent | default implementer and default adversarial reviewer for anything Haiku- or Claude-authored — the routing's floor for any component with real judgment calls (which destructive actions need `useConfirm`, closing a flagged zero-error-handling gap, resolving an actual overflow). |
| Sol-high | **Opus** subagent | `Security.tsx` specifically (auth-pipeline-sensitive, regardless of diff size — unchanged from the Codex-era rule), and any component where a Sonnet review flags something structurally significant enough that Sol/high would have been the Codex escalation. |

Dispatch mechanics stay identical to the Codex tiers: one focused task per dispatch, full context in
the brief (the subagent has no memory of this conversation), a fresh subagent per component, never
two implementer dispatches live at once in the same worktree. Use the `model` parameter on the
`Agent` tool call to pin the tier explicitly — an unpinned dispatch silently inherits a more
expensive default, which defeats the point of this table.

Already-self-implemented work done before this ruling (Deploy pipeline's `AppTemplates.tsx`,
written directly by that group's own controller before the "stop and report" instruction reached
it) is kept rather than redone from scratch, but still routed through an independent Sonnet
reviewer before it commits — closing the implementer/reviewer split that was skipped the first
time, without discarding real, working output.

Revert to the Luna/Terra/Sol Codex routing once the account-wide limit resets (~Aug 21 2026
8:57 AM) for any workstream-2 tasks not yet started at that point.

**What to compare, once this workstream's units are logged below:** defect rate and category
(spec-compliance misses vs. quality nits vs. real bugs) and token/turn cost for Haiku/Sonnet/Opus-
implemented units against the Luna/Terra/Sol-implemented units already in this log's table, and
whether the reviewer tier changes what gets caught for comparable diff sizes.

### Results, paused for a controller context reset (2026-08-21)

A second, unrelated outage hit partway through: the *controller session's own* Claude usage limit
(not Codex) was hit twice more (resets 8:50am, then 2:40pm America/New_York), stalling all three
still-running group-controller subagents mid-task each time. Both resets were confirmed by checking
wall-clock time directly before resuming rather than assumed. Separately, the user confirmed the
original Codex CLI account-wide limit itself had also reset by 2026-08-21. Rather than resume
everything and switch tiers again this deep into one session, the user chose to stop here, write up
what the Sonnet/Opus routing produced so far, and pick up Codex routing plus the remaining
components in a fresh session — this subsection is that handoff.

**Operate group — complete, PR #22, fully Claude-subagent-routed (Sonnet implementer/reviewer,
no Codex involved at any point for Approvals.tsx; UserSearch.tsx/Workflows.tsx were self-implemented
by the group-controller before the routing ruling landed, then independently Sonnet-reviewed
after).** Verified directly by the controller (not just trusted from the report): `npm run build`
clean, `npx vitest run` 55/55 passing across 12 files, capture matrix zero-overflow across all 5
device profiles for `finduser,workflows,approvals`. Real defects the Sonnet review layer actually
caught, unprompted:
- **Critical** in `Approvals.tsx`: a single shared `useAsyncAction` instance across every row of the
  approvals table meant a second row's confirmed Approve/Reject/Retry would silently no-op while an
  earlier row's mutation was still in flight — a genuine concurrency-shaped bug, the same class of
  finding this log's Codex-Terra/Sol reviews have caught before (see Unit 4, Unit 8 above), caught
  here by a Sonnet-tier reviewer without any Opus/Sol-equivalent escalation. Fixed by giving each
  row its own subcomponent and thus its own action-state instance; a new test proves two pending
  rows resolve independently.
- Two Important/Minor findings in `Workflows.tsx` (an unwrapped shared `StepList.tsx` result table,
  an unbounded ephemeral-secret string) plus a missed test for a diagnose-then-fix stale-error
  sequence — all real, all fixed before commit.
- A genuinely useful *tooling* bug outside either component: `docs/scripts/capture-mobile-media.mjs`'s
  `finduser` wait condition targeted a `placeholder` attribute the MUI migration removed in favor of
  a proper `<label>` — would have hung/timed out silently in CI. Fixed the wait condition itself.

**Early read:** a Sonnet-tier implementer+reviewer pair, working from the same kind of precise brief
this log's Codex dispatches get, caught at least one bug in the same severity/category Sol-tier
Codex review has historically been needed for — on this sample, tier-for-tier substitution held up.
Too small a sample (one group) to revise the routing priors at the top of this file; treat as a
data point, not a conclusion, until the other three groups' results are in.

**State of the other three groups at the moment of pausing** (all mid-task, none broken — every
worktree below is exactly where its last successful step left it, safe to resume as-is):

- **Deploy pipeline** (`.claude/worktrees/agent-a4beb0625b4b285c4`, branch
  `feat/mui-deploy-pipeline-migration`): 3 commits done — `AppTemplates.tsx` (Sonnet-implemented
  pre-ruling, Sonnet-reviewed post-ruling, the dropped-`.catch()` bug from that review fixed),
  `DeployWizard.tsx`, `Deployments.tsx` (both Sonnet-implemented). Uncommitted at pause: a
  `docs/scripts/capture-mobile-media.mjs` fix and the shared `docs/dev-process.md`/spec files
  (leave those two out of this branch's own commit — see note above, they already landed via
  PR #22). Last action before the pause was re-running the capture matrix for
  `templates,deploy,history` — needs that confirmed clean, `docs/mobile.md` baseline updated, then
  PR opened (not merged).
- **Account** (`.claude/worktrees/agent-af793d065db1ab737`, branch `feat/mui-account-migration`):
  1 commit done — `ConfigSnapshots.tsx` (Sonnet-implemented). `Security.tsx` is modified but
  **uncommitted**, with a new untracked `Security.test.tsx` — mid-verification when paused (the
  controller's last line was "let me verify this directly rather than trusting the report alone").
  Per the routing table, `Security.tsx`'s review must be an Opus subagent, not Sonnet — confirm that
  actually happened before this commits, don't assume it from the implementer's own say-so.
- **Manage** (`.worktrees/feat-mui-manage`, branch `feat/mui-manage-migration`): 0 commits yet.
  `Tenants.tsx` is modified with a new untracked `Tenants.test.tsx` — mid-implementation when
  paused. Still has `Contracts.tsx`, `NewHire.tsx`, `Offboard.tsx` entirely untouched after that.
  Furthest from done of the four groups.

**Handoff instruction for the next session:** the user wants to revert to Luna/Terra/Sol Codex
routing (confirmed available again as of 2026-08-21) for whatever's left in these three groups,
rather than continuing the Sonnet/Opus substitution — the outage that motivated the substitution is
over. Verify Codex is actually reachable with a throwaway ping before trusting it, the same way its
outage was originally confirmed, rather than assuming a stated reset time held.

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

### Second tooling bug — a dispatcher silently substituting itself for a failed Codex call

The MUI design system plan's Task 1 review dispatch (Terra, review role) came back with a full,
plausible-looking review — verdicts, findings, file:line citations — but its closing "Notes for
Dispatcher" line admitted `codex exec -s read-only` had failed on Windows and it had done "manual
verification" itself instead. That review was never produced by an independent Terra-tier model
call; the dispatcher (running as a cheap Claude tier) substituted its own read of the files, which
defeats the entire reason this process routes review through a separate system. Caught only because
the note happened to be included — a differently-worded dispatcher report could have hidden this
completely.

Reproduced directly: `codex exec ... "$(cat review-<sha>..<sha>.diff) ..."` failed with `Argument
list too long` — the diff was ~104KB, and embedding file content into a shell command via `$(cat
...)` substitution blows past Windows' command-line argument-length limit once the total argument
exceeds roughly 100KB. Confirmed the fix by piping the same file via stdin instead
(`cat file | codex exec ... "<short prompt referring to what's piped>"`), which correctly read the
full diff and reported the right file count. All three `.claude/agents/codex-*.agent.md` files now
document the stdin pattern for large content (a brief, a report, a diff) instead of `$(cat ...)`
substitution.

**Rule: a dispatcher's own report is a claim, not proof the underlying tool call happened — an
admission buried in a closing note is not a substitute for verifying the mechanism actually ran.**
When a dispatcher's summary contains any hint that the tool call didn't work as expected (an error
message, a fallback, a workaround), stop and reproduce the underlying command directly before
trusting anything else in that report — a plausible-sounding review is not evidence it came from
where it claims to have come from.

## Log

| Unit | Task type | Author | Reviewer | Defects found | Defects real | Caught by tests instead | Est. tokens | Verdict |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | MCP transport spike: SDK wiring, one diagnostic tool, live auth-context proof | Terra/high | Sol/high | 1 | 1 | 0 | ~370,000 across all dispatches (implementer + two blocked/retried reviewer attempts + direct review + re-review) | See notes below. First unit fully routed through Codex; also the first live validation of the "read the other system's logs" rule on this repo (two real infra bugs found and fixed by reading raw process output directly: a `--sandbox`/`--approve-for-me` CLI conflict, and a later Windows-sandbox launcher failure), plus one real Critical finding from Sol/high that exposed a controller-introduced verification gap (Dev-mode shortcut vs. the brief's specified Local-mode proof), fixed with real evidence and re-reviewed clean. |
| 2 | Tenant.McpApprovalMode + system-admin-only gate | Luna/low | Terra/high | 2 | 2 | 0 | (not separately logged) | 2 Important: a plan-mandated doc-text gap (the brief's own XML doc for HasRoleAsync didn't mention the non-Local all-access passthrough) and a missing Enum.IsDefined validation on SetMcpMode. Both real, both fixed in 1 round, re-review clean. First unit confirming Luna is fine for transcription-level work even on RBAC-adjacent code, as long as a stronger tier reviews it. |
| 3 | PendingAction entity + EF migration | Luna/low | Terra/high | 0 | 0 | 0 | (not separately logged) | Approved clean, no findings. Notable for a different reason: the implementer's own report contained two false claims (a git permission block, 3 "pre-existing" GitSync test failures) that were sandbox artifacts of `windows.sandbox=unelevated`, not real problems -- caught by the controller running both operations directly, unsandboxed, before trusting either claim. New standing rule added to this log as a direct result. |
| 6 | PendingActionsController + a controller-found audit-coverage regression fix + user-requested Retry feature | Terra/high | Sol/high | 1 Important + 1 residual same finding | 2 | 0 | (not separately logged) | Expanded beyond the written plan mid-sprint (the user asked for retry/reconciliation to actually be built, having initially agreed to defer it). Designing Retry surfaced a real, previously undetected regression: Task 4's atomic-claim rewrite silently bypassed AuditSaveChangesInterceptor for every transition except creation, and nobody -- not the controller, not three rounds of Task 4 review -- had checked whether the concurrency fix broke the audit requirement it wasn't being reviewed for. Fixed alongside Retry (which mirrors Task 4's atomic-claim pattern exactly). Sol/high's review (same tier for the same reason as Task 4: extending an already-reviewed state machine) found every post-transition audit write used the request's own cancellation token, so a cancellation at exactly the wrong moment could lose an audit record for an already-committed transition -- worse for Retry, which could strand a row invisible and permanently unretriable. Fixed using an existing precedent already in this codebase (`WorkflowsController.Record`'s `CancellationToken.None`), in 2 rounds: round 1 (Terra) fixed 3 of 4 spots, round 2 (Luna, genuinely mechanical once round 1's pattern existed) closed the last one and reasonably extended the fix to an adjacent claim statement beyond what was literally asked -- re-review confirmed the extension's reasoning was correct, not scope creep. |
| 4 | PendingActionService: stage/approve/reject/expire state machine | Luna/low (impl) -> Sol/high (fix round 1) -> Terra/high (fix rounds 2-3) | Terra/high | 1 Critical + 2 Important + 1 Minor | 4 | 0 | (not separately logged) | The most consequential unit so far. Terra/high's review found a genuine double-execution race: ApproveAsync/RejectAsync read Pending, mutated the tracked entity, invoked the real external mutation, and only saved afterward -- two concurrent callers could both pass the Pending check and both execute the real mutation before either save landed. Escalated to Sol/high per this repo's own routing policy (state-machine/security-relevant concurrency, regardless of diff size), which rewrote the claim as a single atomic conditional UPDATE. Took 3 fix rounds total: round 1 closed the Critical but only partially closed 2 Important findings; round 2 closed one (missing race test) and converted the other (reconciliation strategy) into a documented, deliberate scope boundary per a controller ruling grounded in this plan's own Global Constraints; round 2 also introduced a new test that a re-review correctly flagged as not proving a real race; round 3, after the controller traced the actual atomic-UPDATE semantics directly, confirmed no such race is structurally possible for approve-vs-expire (unlike approve-vs-reject, which is a real race between two independent writers) and replaced the vacuous test with an honest one plus a real adjacent gap (lazy expiry must never revert a terminal Executed state). Re-review independently confirmed the controller's code-level analysis was correct, not just asserted. |
| 7 | Read-only MCP tools (Dashboard, Tenants, Workflow list/diagnose) + user-requested dashboard approvals tile | Luna/low | Terra/high | 1 Critical (bundled) + notifier gap on re-review | 2 of 3 sub-claims | 0 | (not separately logged) | A bundled finding needing a split ruling: the reviewer flagged both WorkflowTools.ListWorkflows duplicating a controller query and DiagnoseWorkflow skipping the controller's WorkflowRun persistence, under one Critical. Controller ruled only the second half real (the first is a stateless 3-line projection with nothing to diverge on, and duplicating it matches this plan's own deliberate choice not to route WorkflowTools through the HTTP layer) -- adjudicating part of a bundled finding while fixing the rest, rather than either accepting or dismissing it whole. Fixed the real half by mirroring WorkflowsController.Record directly (not reachable through the HTTP-coupled method). Round 1's re-review caught a precise miss: Record's finally block also notifies the configured IRunNotifier, which the first fix omitted. Round 2 (Luna, fully mechanical once precisely specified) closed it. Confirms: a review finding bundling two claims doesn't mean both are equally real, and even a "just mirror this existing method" fix can still miss one line of what it's mirroring. |
| 8 | RemediateWorkflow: the first MCP tool branching on McpApprovalMode (Queue vs ClientTrust) | Terra/high | Sol/high | 2 Important (round 1) + 2 Important self-inflicted + 1 test gap (round 2) | 5 | 0 | (not separately logged) | The most heavily reviewed unit in this plan, appropriately -- this is the piece the whole HITL design exists to prove. Round 1's review found the tool silently discarded WorkflowRunResult.Ephemeral (show-once secrets, e.g. password-reset's generated temporary password): the mutation happened but the value became unrecoverable, real operational harm, not just a style gap. Fixed with two different treatments for two genuinely different delivery channels (ClientTrust surfaces it in the synchronous response; Queue mode explicitly rejects the one known ephemeral-producing workflow rather than losing it silently) plus 6 new tests, since the reviewer separately noted the safety-critical branch itself had zero direct coverage. The fix round then introduced two of its own real bugs, both caught by re-review: a case-sensitivity mismatch between the guard's literal string comparison and WorkflowCatalog's case-insensitive resolution (a genuine bypass -- "PASSWORD-RESET" slipped past the guard), and a partial-failure condition that still discarded a real, already-materialized secret whenever a later workflow step failed after the password had already changed. Round 2 closed both plus a residual executor-side test gap. Confirms this repo's own standing rule with a sharper edge than usual: a fix for a real defect can introduce a new one, and the reviewer that caught the first bug is exactly the one equipped to catch the second. |
| 9 | Approvals SPA tab (+ user-requested retry UI) | Terra/high | Terra/high | 0 | 0 | 0 | (not separately logged) | Approved clean. Corrected a stale brief before dispatch (predated Task 6's retry addition) rather than transcribing it verbatim and letting review catch the gap -- cheaper to fix a known staleness before dispatch than after. |
| 10 | MCP access token management UI | Luna/low | Terra/high | 0 | 0 | 0 | (not separately logged) | Approved clean, brief needed no corrections (Task 5's fix rounds only added authorization guards, never touched the controller's DTO shapes the frontend depends on). First fully clean pass with zero controller intervention beyond the standard verify-before-committing check since Unit 3. |
| 11 | End-to-end live verification (no code, no review -- controller-run, per this repo's own "budget for live verification separately" rule) | Controller | n/a | 1 | 1 | 0 | n/a | Real MCP protocol handshake, real PAT, real tenant, real staged action, real approve/retry/audit trail, real ClientTrust immediate execution -- all confirmed working against the actual running server, not just unit tests. No live Partner Center credentials in this dev environment, so diagnose/remediate correctly failed at the real Graph/SAM boundary rather than succeeding -- itself useful evidence (auth/routing/tenant-resolution all worked right up to that boundary), and Queue-mode correctly refused to stage a fix it couldn't diagnose first. One genuine finding surfaced only by running the real thing, not reachable by any unit test: Approve/Retry return HTTP 409 for a real execution failure (SamTokenService's config error is an InvalidOperationException, the same type PendingActionService's own state-conflict checks throw, so PendingActionsController's catch-all conflates "couldn't claim" with "claimed fine, execution failed") -- underlying state machine verified correct directly against the database either way, so this is a status-code semantics gap, not a state-machine defect. Parked for the Sol/ultra final review rather than a 4th round this late. Confirms this plan's own standing rule one more time: review reads diffs, so it finds defects that live in diffs -- this one lived in the gap between two call sites' shared exception type, invisible to any diff read. |
| 12 | Sol/ultra final whole-branch review (base eea9a89..279d994, all 11 units) + one fix wave + one scoped re-review | Sol/high (fix wave) | Sol/ultra (broad) -> Sol/high (scoped re-review) | 9 (broad) + 1 (re-review) | 9 (broad) + 1 (re-review) | 0 | n/a | Broad review found 2 Critical (an MCP PAT authenticated against every REST endpoint, not just /mcp -- a PAT could call WorkflowsController.Remediate directly and bypass the whole Queue/HITL mechanism this plan exists to build; /mcp wasn't routed by the checked-in nginx template) + 5 Important (Queue-mode executor discarded the workflow result and could silently misreport failure as success; Queue preview omitted the actual inputs being changed; PendingActionsController.List's system-admin bypass directly violated this repo's own CLAUDE.md rule against IsSystemAdmin bypassing tenant grants; the tool's own MCP description promised a check_pending_action tool that didn't exist; claim+audit weren't transactionally atomic) + 2 Minor. Controller adjudicated all 9 as real (none dismissed), explicitly scoping two look-alike pre-existing gaps (WorkflowsController.Runs' identical bypass, DashboardController's unscoped visibility) OUT as unrelated to this branch. One fix wave addressed all 9; controller personally verified every fix by reading the actual diff line-by-line before committing, not by trusting the implementer's report. The scoped re-review then caught something the controller's own line-by-line read had missed: the Fix-1 middleware's PAT-detection signal (a bare "jti" claim) also matches standard OIDC access tokens (Entra ID and most providers include their own unrelated jti), and the middleware was wired unconditionally across all three auth modes -- so an Oidc-mode deployment would have gotten every interactive user 403'd off every non-/mcp endpoint, a real regression the fix itself introduced. Adjudicated and fixed inline per the SDD residual-fix step (no second fix-wave round): confirmed via McpTokensController's existing CurrentUserId gate that PATs are exclusively a Local-mode feature, then scoped the middleware to AuthModeInfo.IsLocal. Confirms two things at once: a security fix under time pressure can still miss a claim collision the fix itself introduces, and the second review pass in a two-pass loop earns its cost even when the first pass was thorough. |

| 5 | Revocable MCP PAT issuance: entity, OnTokenValidated revocation check, McpTokensController | Terra/high | Sol/high | 3 Important | 3 | 0 | (not separately logged) | Routed straight to Sol/high per policy (auth-pipeline-sensitive), and it earned the tier: found a real incident-recovery gap (a stolen PAT could mint its own replacement before revocation caught up, since PATs carry the same claims as login tokens and the token-management controller only required [Authorize]), an untested revocation boundary, and an audit-log bug (the usage-heartbeat update ran before HttpContext.User was populated, misattributing an AuditEvent to "anonymous" on every single authenticated request). One fix round closed all three at the code level; the re-review's residual concern (the extracted validator was unit-tested but the actual Program.cs wire-up never was) was closed by the controller directly, live, the same way Task 1 closed its own auth-context question -- minted a real PAT, used it successfully, confirmed the new self-management guard also worked (403 trying to manage its own token), revoked it via a real login token, then proved the identical PAT got a real 401 from the running server. Also surfaced ~300 leaked dotnet.exe processes accumulated over the session's build/test cycles, plausibly the cause of "process saturation" sandbox errors several earlier units reported -- cleared, and worth a periodic `dotnet build-server shutdown` + process check on long multi-task sessions going forward. |
