# Ops Workbench (0.9.0) -- implementation plan and shared contract

**Status:** in progress on `feat/ops-workbench`. Working notes, not user docs. This file is the
contract parallel workstreams build against; change it here first if a shape must move.

## Goal

Move PCB from "a set of MSP screens" to a Microsoft 365 operations workbench: start from the
tenant, person, problem or outcome; PCB plans, applies, verifies and records. Core loop stays
**Diagnose -> Plan -> Apply -> Verify -> Record**.

## Workstreams

| # | Workstream | Area | Owner |
|---|---|---|---|
| A | Local Workbench host: hosting profile, SQLite persistence, SPA hosting, CLI (`doctor`, `--port`, `--no-browser`), first-run status API, publish profile | `src/PartnerCenterBridge.Api`, `src/PartnerCenterBridge.Data*`, new `src/PartnerCenterBridge.Data.Sqlite` | astra/ultra |
| B | Operation/evidence model, Access Parity, person workspace API, offboarding policy v2 | `src/PartnerCenterBridge.Core`, `.Graph`, `.Exchange`, `.Api` controllers | astra/ultra |
| C | Real routing, new shell/IA, People workspace UI, evidence UI, Access Parity UI, command palette | `web/` | Claude Opus |
| D | CI + Local Workbench publish validation | `.github/workflows` | Claude Sonnet |
| E | Docs, screenshots, ROADMAP | `README.md`, `docs/` | Claude Sonnet |
| F | Adversarial review + packaged-binary smoke test | whole branch | astra (read-only) + orchestrator |

## Invariants (all workstreams)

- ASCII-only compiled C# string literals. No AI attribution in commits.
- Instance access (`IInstanceAccessService`) and tenant access (`ITenantAccessService`) stay
  separate; every tenant-scoped endpoint checks a tenant grant (Viewer to read, Operator to mutate).
- Server/container mode (Postgres, nginx, Docker images, `deploy/`) keeps working unchanged.
- No generic "run arbitrary Graph request / PowerShell" surface.
- Never report success without verification; surface partial success honestly.
- Never fake capability: a section PCB cannot read reports `Unavailable` with the reason.

## A. Hosting profiles

- `Hosting:Profile` = `Server` (default, today's behavior) | `Local`.
  Local is selected by `--local` on the command line, or by default in the published Local
  Workbench build (baked in at publish time; the container build never gets it).
- Local profile implies: SQLite at `%LOCALAPPDATA%\PartnerCenterBridge\pcb.db`
  (`$XDG_DATA_HOME`/`~/.local/share` elsewhere); Data Protection keys, logs, packages and
  generated config under the same root; `Auth:Mode=Local` by default (never Dev); a generated
  `Auth:Local:SigningKey` persisted protected on first launch; Kestrel bound to `127.0.0.1` only;
  canonical origin `http://localhost:<port>` (passkey RP-ID `localhost`), with requests for
  `127.0.0.1:<port>` redirected to the canonical origin. Default port 5080; `--port N`.
  Binding to a non-loopback address requires an explicit `--listen <addr>` plus a warning.
- `Persistence:Provider` = `Postgres` | `Sqlite`. SQLite has its own migrations assembly
  (`PartnerCenterBridge.Data.Sqlite`); both provider migration sets are generated from the same
  `BridgeDbContext` model. Startup applies migrations for the active provider.
- SPA: the Local build embeds the Vite `dist` output and serves it from ASP.NET Core with
  fallback-to-`index.html` for non-`/api`, non-`/mcp`, non-`/health` paths (deep links survive
  refresh).
- Second launch on an occupied port: if `/health` on that port answers as PCB, open the browser
  to it and exit 0; otherwise print an actionable error.

### Status endpoints

`GET /api/system/status` (anonymous; nothing sensitive):
```json
{ "profile": "Local", "version": "0.9.0", "authMode": "Local", "needsFirstUser": true }
```

`GET /api/system/diagnostics` (authenticated; details only for instance Administrator,
others get the capability flags only):
```json
{
  "checks": [
    { "id": "database", "label": "Database", "status": "Ok|Warning|Error|NotConfigured",
      "detail": "SQLite at C:\\Users\\...\\pcb.db", "fix": null },
    { "id": "exchange-module", "label": "Exchange Online module", "status": "NotConfigured",
      "detail": "pwsh found, ExchangeOnlineManagement not installed",
      "fix": { "label": "Install module", "command": "pwsh -c \"Install-Module ExchangeOnlineManagement -Scope CurrentUser\"", "route": null } }
  ],
  "capabilities": { "graph": true, "exchange": false, "partnerCenter": true }
}
```
Check ids: `hosting`, `database`, `data-protection`, `auth`, `sam`, `tenants`, `pwsh`,
`exchange-module`, `exchange-app`. `fix.route` is an SPA route (e.g. `/settings/microsoft`).

## B. Operation and evidence model

Extends the existing `IWorkflow` / `WorkflowRun` model; it does not replace it.

- `IPlannedOperation : IWorkflow` adds `PlanAsync(tenant, inputs)` -> `OperationPlan` and
  `ApplyAsync(tenant, inputs, selectedItemIds)` -> `OperationEvidence` (apply re-plans
  server-side, applies only still-eligible selected items, re-queries and verifies).
- Every persisted `WorkflowRun` gains a nullable `Evidence` JSON column. Existing workflows get
  evidence through an adapter (diagnosis -> preflight, steps -> changes, post-diagnosis ->
  verification). Offboarding runs are persisted as runs with evidence too.
- Ticket notes and Markdown are generated server-side from the structured evidence.

```ts
type Outcome = "Succeeded" | "PartiallySucceeded" | "Failed" | "NoChangeNeeded"
             | "VerificationFailed" | "Planned";
interface PlanItem { id: string; action: string; objectType: string; objectId: string;
  objectName: string; destructive: boolean; eligible: boolean; category: string;
  reason?: string | null; }            // reason: why ineligible / why skipped
interface OperationPlan { operationId: string; operationName: string; tenantId: string;
  target: { kind: string; id: string; displayName: string };
  preflight: Finding[]; items: PlanItem[]; warnings: string[]; limitations: string[]; }
interface ChangeResult { planItemId: string; action: string; objectName: string;
  attempted: boolean; succeeded: boolean; detail?: string | null; }
interface VerificationCheck { name: string; passed: boolean; detail?: string | null; }
interface OperationEvidence {
  runId: string; operationId: string; operationName: string;
  tenant: { id: string; displayName: string; tenantId: string };
  target: { kind: string; id: string; displayName: string } | null;
  operator: string; startedAt: string; completedAt: string; outcome: Outcome;
  preflight: Finding[]; plan: PlanItem[]; changes: ChangeResult[];
  verification: VerificationCheck[]; warnings: string[]; limitations: string[];
  failures: string[]; ticketNotes: string; }
```

Endpoints:
- `GET /api/workflows/runs/{runId}/evidence` -> `OperationEvidence` (JSON)
- `GET /api/workflows/runs/{runId}/evidence?format=markdown` -> `text/markdown` download
- `GET /api/workflows/runs?tenantId=&targetId=&take=` (existing, gains `targetId` filter and
  `outcome` on each row)

### Access Parity (`access-parity`)

- `POST /api/tenants/{tenantId}/operations/access-parity/plan`
  `{ sourceUserId, targetUserId }` -> `OperationPlan`
- `POST /api/tenants/{tenantId}/operations/access-parity/apply`
  `{ sourceUserId, targetUserId, itemIds: string[] }` -> `OperationEvidence`
- Tenant role: Viewer to plan, Operator to apply.
- Additive only. Items are groups the source is a direct member of and the target is not.
  Categories: `Security`, `Microsoft365`, `MailEnabledSecurity`, `Distribution`, `Dynamic`,
  `RoleAssignable`, `OnPremSynced`, `DirectoryRole`, `AlreadyMember`. Only `Security` and
  `Microsoft365` cloud groups are eligible; the rest are listed with the reason they are not
  copied. Target memberships not held by the source are never touched. Limitation always
  stated: SharePoint direct permissions, app role assignments, Exchange mailbox/calendar
  permissions and Teams-only private channels are not compared.
- Apply is idempotent: an item already present counts as `NoChangeNeeded`, not a failure.
  Verification re-reads the target's memberships from Graph.

### Person workspace

`GET /api/tenants/{tenantId}/people/{userId}` -> each section independently
`{ status: "Ok" | "Unavailable" | "Error", reason?: string, data?: ... }`:
`profile` (displayName, upn, mail, accountEnabled, jobTitle, department,
onPremisesSyncEnabled, createdDateTime, lastSignIn if readable), `licenses` (skuPartNumber,
skuId), `groups` (id, displayName, category as above), `authMethods` (method types),
`mailbox` (Unavailable with the missing dependency when Exchange is not configured),
`devices` (Intune managed devices), `recentRuns` (last 10 PCB runs targeting this user).

### Offboarding policy v2

- `Contract.OffboardingPolicy` (JSON): `blockSignIn`, `revokeSessions`,
  `groupCleanup` (`None|RemoveAssignable|RemoveAll`), `convertMailboxToShared`,
  `removeLicenses`, `hideFromGal`, `forwardTo` (optional), `managerAccess`
  (`None|FullAccess`), `wipeDevices` (`None|Retire`), `followUpDays` (delete reminder).
  Defaults reproduce today's offboarding behavior.
- `POST /api/provisioning/terminate/plan` -> `OperationPlan` (ordered; destructive flagged;
  hybrid-synced accounts flagged with a guardrail warning); `POST /api/provisioning/terminate`
  gains evidence. Mailbox conversion runs before license removal and license removal is
  skipped (with reason) if conversion cannot be verified.

## C. Web routes

```
/                          Home
/people                    search (?q=)
/people/:tenantId/:userId  person workspace (?tab=summary|access|licensing|auth|mailbox|devices|history)
/people/:tenantId/:userId/actions/:operationId   contextual action (plan/apply/evidence)
/tenants                   list
/tenants/:tenantId         tenant workspace (?tab=overview|access|contract|snapshots|history)
/operations                catalog
/operations/access-parity  (?tenant=&source=&target=)
/operations/onboard        /operations/offboard  /operations/deploy
/operations/workflows/:workflowId
/operations/contracts      /operations/templates
/activity                  unified history (?tenant=&kind=)
/activity/runs/:runId      evidence view
/activity/approvals
/settings                  /settings/microsoft  /settings/security  /settings/workbench
/login  /register
```
