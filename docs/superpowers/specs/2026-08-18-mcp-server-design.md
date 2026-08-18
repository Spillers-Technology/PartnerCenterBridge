# MCP Server Support — Design

Status: approved by Joey 2026-08-18, ready for implementation planning.

## Motivation

The bridge already has one first-class client (the React SPA) driving Graph/Partner
Center/Exchange operations through a well-scoped auth + audit layer. The goal here is to give an
LLM the same operational surface — full parity, not a read-only subset — while keeping every
mutating action behind human-in-the-loop approval, and doing it in a way that's genuinely
"first-class": works against both a local dev instance and the live `pcb.spillerstech.us`
deployment, speaks real MCP (Streamable HTTP, proper OAuth 2.1 discovery), and reuses this
codebase's existing authorization/audit machinery rather than growing a second copy of it.

Non-goals for this unit: this spec does not cover the general SPA UX rework (tracked separately —
see the forthcoming UX audit) or the dev-process/Codex tooling (already shipped ahead of this
spec; see `docs/dev-process.md`).

## Architecture overview

```
MCP client (Claude Desktop / Claude Code / Claude.ai remote connector)
        |  Streamable HTTP, bearer token (OAuth-issued or a manual PAT)
        v
PartnerCenterBridge.Api  (same Kestrel pipeline as the REST API, mounted at /mcp)
        |
        |-- ModelContextProtocol SDK server: tool registration, JSON-RPC framing
        |-- same JWT bearer auth middleware the SPA already uses
        |-- same ITenantAccessService.HasRoleAsync checks every controller already calls
        |
        |-- read-only tools --------> call existing services directly, return immediately
        |
        |-- mutating tools ---------> Tenant.McpApprovalMode == Queue (default)?
                                           yes -> stage a PendingAction, return id + preview
                                           no  -> call existing services directly (ClientTrust mode)
```

The MCP layer is intentionally thin: it never talks to Graph, Partner Center, or Exchange
directly, and it never re-implements authorization. Every tool either calls the same service
classes the controllers already call, or (for mutations under the default approval mode) stages a
`PendingAction` that, once a human approves it in the SPA, invokes those same service classes.

## RBAC extension

`Tenant` gains one new column: `McpApprovalMode` (`Queue` default | `ClientTrust`).

- Settable only through a new `IsSystemAdmin`-gated endpoint,
  `PATCH /api/admin/tenants/{id}/mcp-mode`. Tenant Owners cannot change it themselves.
- This is a deliberate, narrow, second use of `IsSystemAdmin`, alongside the existing SAM-admin
  endpoints — and it stays inside the boundary `ITenantAccessService`'s own remarks already draw:
  `IsSystemAdmin` gates *instance-level infrastructure and safety policy*, never *operational*
  access to a tenant's data. Flipping this flag gives a PCB admin zero ability to read or act on
  the tenant themselves — it only changes how *other* users' already-authorized operator actions
  get gated. `HasRoleAsync`'s existing "system admin never bypasses a tenant grant" behavior is
  unchanged and untouched by this addition.
- Drive-by fix in the same unit: `ITenantAccessService.cs`'s XML doc on `HasRoleAsync` (line 13)
  currently claims system admin "bypasses per-tenant grants entirely" — stale, contradicts both
  the implementation and `TenantAccessService`'s own remarks. Correct it while this file is open
  for the `McpApprovalMode` check, so the interface doc doesn't keep asserting the exact bypass
  this design (and the standing CLAUDE.md rule) refuses to build.

## Transport

Host the official `ModelContextProtocol` .NET SDK's ASP.NET Core Streamable HTTP transport
directly inside `PartnerCenterBridge.Api`, mounted at `/mcp` on the same Kestrel pipeline as the
REST API (`app.MapMcp("/mcp")`). No separate process, no separate deploy story — it works against
localhost during dev and against `pcb.spillerstech.us` identically, the moment the image ships.

Tools are registered from a new `PartnerCenterBridge.Api/Mcp/` folder, one file per tool group
mirroring the existing controller boundaries (`WorkflowTools`, `DeploymentTools`,
`ProvisioningTools`, etc.) — each tool method resolves the same injected services its sibling
controller action already uses.

## Auth — forks by the existing `Auth:Mode`

**`Oidc` mode.** The bridge stays a pure OAuth Resource Server, exactly as it already is for the
SPA. Add RFC 9728 Protected Resource Metadata (`/.well-known/oauth-protected-resource`) pointing
MCP clients at the configured external IdP (Authentik or equivalent). Clients complete the OAuth
flow directly against that IdP; the bridge validates the resulting token exactly as it validates
SPA tokens today. No new authorization-server code in this mode.

**`Local` mode.** No external IdP to defer to, so the bridge must act as its own Authorization
Server for MCP purposes: PKCE-based Authorization Code flow, Dynamic Client Registration
(RFC 7591) so Claude Desktop/Claude.ai/Claude Code can self-register without a manual app
registration step, and token issuance/refresh. Build this on **OpenIddict** (new dependency —
mature, ASP.NET Core-native OAuth/OIDC server library) rather than hand-rolling PKCE/DCR/token
endpoints; this is exactly the kind of security-critical protocol surface this project shouldn't
be reimplementing from scratch. Layer it over the *existing* login: the authorize step reuses the
current passkey/password/TOTP login screen plus a new consent prompt, and tokens OpenIddict issues
carry the same claims shape `LocalTokenService` already produces (`UserIdClaim`,
`SystemAdminClaim`) so `TenantAccessService`/`ITenantAccessService` need zero changes to consume
them — an OpenIddict-issued token and a `LocalTokenService`-issued token are interchangeable from
every existing authorization check's point of view.

Also keep a manual **"Create MCP token" PAT action** (new Settings-screen control) for headless/
scripted clients that don't want an interactive browser OAuth dance. Same claims shape, issued
through `LocalTokenService` directly, individually revocable and auditable — a fallback path, not
a competing mechanism.

**`Dev` mode.** Unchanged: `Auth:Enabled=false` means `/mcp` is open too, same as every other
endpoint — consistent with this mode's existing "never use on a deployed instance" warning.

## HITL approval queue

New entity `PendingAction`:

```
Id, TenantId, ActionType, RequestedByUserId, PayloadJson, PreviewSummary,
Status (Pending | Approved | Rejected | Executed | Expired),
CreatedAt, ExpiresAt, DecidedByUserId, DecidedAt, ExecutedAt
```

Flow when `Tenant.McpApprovalMode == Queue` (the default for every tenant):

1. A mutating MCP tool call (e.g. "run the MFA reset fix on tenant X") does not execute anything.
   It calls the same read-side/diagnose logic the direct controller path already exposes to build
   a human-readable `PreviewSummary`, persists a `PendingAction` with that preview and the request
   payload, and returns the id + preview to the LLM.
2. A new **Approvals** tab in the SPA lists pending actions across every tenant the operator has
   `Operator`+ access to. Approving one calls the *same service method* the equivalent controller
   action already calls — `PendingActionsController.Approve` is a thin dispatcher over the
   existing services, not a new orchestration path.
3. Rejecting sets `Status=Rejected`; a background sweep (or a check on read) expires anything past
   `ExpiresAt` (proposed default: 24h) to `Status=Expired` so stale proposals can't be approved
   long after the LLM that requested them lost context on why.
4. An MCP tool (`check_pending_action(id)`) lets the LLM poll status without needing a webhook —
   MCP tool calls are request/response, so there's no server-push mechanism to notify the LLM the
   moment a human approves.

Flow when `Tenant.McpApprovalMode == ClientTrust`: the tool executes immediately through the same
service layer, exactly like the direct controller path does today. Tools are annotated with MCP's
`destructiveHint` either way, so a well-behaved client still prompts locally in this mode — the
queue is what makes approval a real server-side security boundary rather than a UI courtesy.

Read-only tools (find user, tenant dashboard, list tenants, workflow diagnose, config snapshot
diff) always execute immediately regardless of `McpApprovalMode` — there's nothing to approve.

Every `PendingAction` creation, approval, rejection, expiry, and execution is a new
`AuditEventType` entry, following the existing `AuditSaveChangesInterceptor` pattern.

## Tool surface (full parity)

Thin wrappers, one group per existing controller, all reusing that controller's existing service
dependencies:

| Tool group | Mirrors | Mutating? |
|---|---|---|
| Directory / Find User | `SearchController`, `DirectoryController` | No |
| Dashboard | `DashboardController` | No |
| Tenants | `TenantsController` | Create/sync = yes; list/get = no |
| Workflows | `WorkflowsController` | Diagnose = no; Remediate = yes |
| Deployments | `DeploymentsController` | Yes |
| Provisioning (hire/offboard) | `ProvisioningController` | Yes |
| Config Snapshots | `ConfigSnapshotsController` | Capture/import = yes; diff/export = no |
| Exchange | `ExchangeController` | Yes |
| App Templates | `AppTemplatesController` | Yes |
| Provisioning Templates | `ProvisioningTemplatesController` | Yes |
| Contracts | `ContractsController` | Yes |

Deliberately excluded from tool parity: `AuthController`, `PasskeyController`, `TotpController`,
`TenantAccessController`, `AdminController`. These manage the operator's own identity/instance
administration (your own 2FA, your own passkeys, who else has access, SAM refresh tokens) —
out of scope for an LLM to drive by design, not an oversight. `Tenant.McpApprovalMode`'s own admin
endpoint lives in this excluded group too, for the same reason `IsSystemAdmin` guards it.

The `PendingAction` plumbing and the transport/auth scaffolding are the architecturally
significant pieces of this unit. Once that pattern exists and has been through Sol/high review,
the individual tool wrappers above are repetitive enough to be good `codex-luna`/`codex-terra`
candidates per `docs/dev-process.md`'s routing policy — noted here for whoever writes the
implementation plan, not decided in this spec.

## Error handling

- A `PendingAction` whose payload no longer matches reality by the time it's approved (e.g. the
  target tenant lost its GDAP delegation between staging and approval) must re-validate at
  execution time, not just at staging time — approval executes through the same service call a
  live controller action would make, so it gets the same validation for free, but this needs an
  explicit test: stage, invalidate the precondition, approve, confirm a clean error rather than a
  silent no-op or a corrupted `PendingAction` state.
- OpenIddict token issuance failures, expired/replayed authorization codes, and PKCE mismatches
  surface as standard OAuth error responses (`invalid_grant`, etc.) — no bridge-specific wrapping
  needed, this is exactly what OpenIddict already does correctly.
- An MCP tool call against a tenant the caller's token doesn't have sufficient `TenantRole` for
  fails the same way the equivalent REST call already fails (403), before a `PendingAction` is
  ever created — no new authorization-bypass surface introduced by having two entry points
  (REST controller, MCP tool) into the same service layer.

## Testing

Follows this repo's existing convention (WireMock over live tenants, no live GDAP relationship
needed for the test suite):

- `PendingAction` lifecycle: stage -> approve -> executes; stage -> reject -> never executes;
  stage -> expire -> can't be approved late.
- `McpApprovalMode` gate: `Queue` stages instead of executing; `ClientTrust` executes immediately;
  only `IsSystemAdmin` can flip the mode, a tenant Owner attempting to gets 403.
- OAuth flow (Local mode): full PKCE authorization code exchange against OpenIddict, a token
  minted this way passes the exact same `TenantAccessService.HasRoleAsync` checks a
  `LocalTokenService`-minted token does.
- Read-only tools never create a `PendingAction` regardless of `McpApprovalMode`.

## Documentation

This subsystem is genuinely complex (two divergent auth stories by `Auth:Mode`, a staged-approval
state machine, an RBAC extension with a carefully-drawn boundary) — proportional to that, it gets
a dedicated page, not a changelog line, following this repo's own release-checklist rule that new
user-facing capability "gets its own paragraph, not just a changelog mention":

- New `docs/mcp.html` (nav link added to every existing `docs/*.html`'s hardcoded topnav, per the
  existing no-shared-header convention): what MCP support is, the three-auth-mode connection story
  side by side, what `Queue` vs `ClientTrust` mean and how to switch a tenant between them, a
  worked example of staging and approving an action.
- `README.md`'s architecture summary and maturity table gain an MCP row.
- `CLAUDE.md`'s release checklist gains MCP-specific items (regenerate the Approvals-tab
  screenshot, verify `/mcp` docs match the shipped tool list) the same way other features already
  extended that checklist.
- `docs/architecture.html` gets the MCP box added to its component diagram/table.

## Suggested phasing (for the implementation plan, not decided here)

Roughly: (A) `PendingAction` entity + migration + `Tenant.McpApprovalMode` + admin endpoint +
audit wiring, (B) transport scaffolding + read-only tools first, (C) mutating tools + Approvals
SPA screen, (D) Local-mode OAuth (OpenIddict) + PAT fallback + Oidc-mode resource metadata,
(E) remaining tool parity, (F) documentation. The writing-plans skill should own the real
sequencing and task breakdown.
