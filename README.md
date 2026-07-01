# Partner Center Bridge

A two-part MSP bridge — an ASP.NET Core Web API plus a React SPA — that fronts Microsoft Graph
and the Partner Center REST API to make cross-tenant Intune + identity work repeatable. Each
**contract** declares a desired state (starting with Win32 app templates) and the bridge
reconciles every tenant on the contract to it.

> **Status:** Phase 1 (templated Win32 `.intunewin` deploy across tenants, with updates),
> Phase 2 (new-hire provisioning + offboarding via Graph, contract-driven), and Phase 3
> (Exchange Online mailbox ops via EXO PowerShell V3) are in. Phase 4 (two-way LDAP) is
> scaffolded for but not implemented.
>
> **Mailbox archive workflow** ("full / not archiving"): one **Diagnose → Fix → Nudge** flow
> that enables the archive + auto-expand, ensures a retention policy, clears the hidden blockers
> (retention hold, `ElcProcessingDisabled`), and triggers the Managed Folder Assistant — with the
> full mailbox state surfaced to the operator, since the move runs asynchronously and often needs
> re-nudging. Replaces the ~10-cmdlet dance.

## Architecture

Two independent auth planes:

- **Operator plane** — the SPA + API authenticate *you* via Authentik OIDC (JWT bearer).
- **Microsoft plane** — a multi-tenant Entra app under the **Secure Application Model** with a
  GDAP relationship per customer. `SamTokenService` exchanges the stored (encrypted, auto-rotated)
  SAM refresh token for a per-tenant Graph token on demand.

```
web/ (React+Vite+TS)  ──►  src/PartnerCenterBridge.Api  ──►  Core (contracts / desired state / reconcile)
                                     ├─ Graph (GraphTenantClientFactory, IntuneWin32Service, .intunewin reader)
                                     ├─ PartnerCenter (SamTokenService, PartnerCenterClient)
                                     └─ Data (EF Core + Postgres, Data-Protection-encrypted secrets)
```

| Project | Responsibility |
|---|---|
| `PartnerCenterBridge.Core` | Domain entities, reconcile engine, cross-project abstractions. No external SDK deps. |
| `PartnerCenterBridge.Data` | EF Core `BridgeDbContext`, migrations, `ProtectedSamTokenStore`. |
| `PartnerCenterBridge.PartnerCenter` | `SamTokenService` (MSAL SAM flow), `PartnerCenterClient` (REST v3). |
| `PartnerCenterBridge.Graph` | `IntuneWin32Service` (full beta upload state machine), `GraphUserService` (hire/offboard), `.intunewin` reader, tenant client factory. |
| `PartnerCenterBridge.Exchange` | `ExchangeOnlineService` — mailbox config via EXO PowerShell V3 (app-only cert), run out-of-process through `PwshRunner`. |
| `PartnerCenterBridge.Api` | Controllers, OIDC auth, DI wiring, deploy + provisioning orchestration. |
| `web/` | React SPA: Tenants, Contracts, App Templates, Deploy wizard, History. |

## Run locally (docker-compose)

Auth is disabled in compose so you can click through the UI without an IdP.

```bash
docker compose up --build
# SPA:     http://localhost:8081
# API:     http://localhost:5080  (Swagger at /swagger)
```

Or run the pieces directly:

```bash
# API (needs a Postgres; connection string in appsettings.json)
dotnet run --project src/PartnerCenterBridge.Api
# SPA (proxies /api to http://localhost:5080)
cd web && npm install && npm run dev
```

## Tests

```bash
dotnet test          # reconcile logic, .intunewin parsing, SAM token rotation
```

## The Win32 deploy flow

`IntuneWin32Service.DeployAsync` runs the documented Graph **beta** sequence end to end:
create `win32LobApp` → content version → file → poll for the Azure Blob SAS → chunked
block-blob upload → `commit` (with the encryption info parsed from `Detection.xml`) → poll →
set `committedContentVersion` → `assign`. Passing an existing `Deployment` pushes a *new*
content version to an app that already exists, which is how "update every tenant" works.

> `.intunewin` packages must be produced by the Microsoft **Win32 Content Prep Tool**
> (`IntuneWinAppUtil.exe`) — the bridge consumes them, it doesn't repackage.

## Deploy (Kustomize / Flux)

`deploy/base` + `deploy/overlays/production` follow the homelab base/overlay convention.
Non-secret config is a `configMapGenerator`; secrets (`Postgres`, Entra client secret, seed
refresh token) live in `secrets.sops.yaml` — **encrypt with SOPS before committing**.

```bash
kubectl kustomize deploy/overlays/production   # render to verify
```

## Bootstrapping the Secure Application Model

The SAM refresh token must be seeded once by an interactive, MFA'd admin (MFA on App+User
Partner Center calls has been enforced since April 2026). Three ways to seed it, in order of
preference:

```bash
# 1. Interactive device-code bootstrap (recommended). Prints a URL + code to sign in with an
#    MFA'd admin agent; stores the encrypted, auto-rotating refresh token.
dotnet run --project src/PartnerCenterBridge.Api -- bootstrap-sam

# 2. Paste a refresh token captured out-of-band.
curl -X POST /api/admin/sam/seed -H 'content-type: application/json' -d '{"refreshToken":"..."}'

# 3. Set Partner:SeedRefreshToken in config; the bridge persists + rotates it on first use.
```

Check status any time: `GET /api/admin/sam/status` → `{ "bootstrapped": true|false }`.
After the token is stored it is rotated automatically on every use (well inside the 90-day
window). Per-customer admin consent + a GDAP relationship are still required before the bridge
can act in a given tenant.
