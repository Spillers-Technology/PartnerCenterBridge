# Partner Center Bridge
<img width="1448" height="1086" alt="image" src="https://github.com/user-attachments/assets/523a6fca-c896-452a-83e3-9ef560b8f17a" />

**Docs:** <https://spillers-technology.github.io/PartnerCenterBridge/>

A two-part MSP bridge — an ASP.NET Core Web API plus a React SPA — that fronts Microsoft Graph
and the Partner Center REST API to make cross-tenant Intune + identity work repeatable. Each
**contract** declares a desired state (starting with Win32 app templates) and the bridge
reconciles every tenant on the contract to it.

> **Maturity (v0.1.0), feature by feature:**
>
> | Capability | Status |
> |---|---|
> | Templated Win32 `.intunewin` deploy across tenants, with updates | **Stable** |
> | Contract-driven new-hire provisioning + offboarding via Graph | **Stable** |
> | Cross-tenant Find User with per-person fix shortcuts | **Stable** |
> | Known-fix workflow library (MFA reset, password reset, compromised lockdown, license repair) | **Beta** |
> | Exchange Online mailbox ops via EXO PowerShell V3 (mailbox archive repair) | **Beta** |
> | Two-way LDAP sync (Phase 4) | **Planned** — scaffolded, not implemented |
>
> **Known-fix workflows** run one **Diagnose → Fix → Verify** loop: the diagnosis is shown
> verbatim before anything changes, the fix is applied step by step, and a fresh diagnosis proves
> the result. Every run is persisted with the operator's identity. The **mailbox archive** fix
> ("full / not archiving") enables the archive + auto-expand, ensures a retention policy, clears the
> hidden blockers (retention hold, `ElcProcessingDisabled`), and triggers the Managed Folder
> Assistant — re-running doubles as the nudge the asynchronous move routinely needs. Replaces the
> ~10-cmdlet dance.

## Architecture

Two independent auth planes:

- **Operator plane** — the SPA + API authenticate *you* via Authentik OIDC (JWT bearer).
- **Microsoft plane** — a multi-tenant Entra app under the **Secure Application Model** with a
  GDAP relationship per customer. `SamTokenService` exchanges the stored (encrypted, auto-rotated)
  SAM refresh token for a per-tenant Graph token on demand.

```
web/ (React+Vite+TS)  ──►  src/PartnerCenterBridge.Api  ──►  Core (contracts / desired state / reconcile / workflows)
                                     ├─ Graph (GraphTenantClientFactory, IntuneWin32Service, .intunewin reader, Identity workflows)
                                     ├─ Exchange (ExchangeOnlineService via EXO PowerShell V3, Mailbox workflows)
                                     ├─ PartnerCenter (SamTokenService, PartnerCenterClient)
                                     └─ Data (EF Core + Postgres, Data-Protection-encrypted secrets, run history)
```

| Project | Responsibility |
|---|---|
| `PartnerCenterBridge.Core` | Domain entities, reconcile engine, the `IWorkflow` contract + catalog, cross-project abstractions. No external SDK deps. |
| `PartnerCenterBridge.Data` | EF Core `BridgeDbContext`, migrations, `ProtectedSamTokenStore`, workflow run history. |
| `PartnerCenterBridge.PartnerCenter` | `SamTokenService` (MSAL SAM flow), `PartnerCenterClient` (REST v3). |
| `PartnerCenterBridge.Graph` | `IntuneWin32Service` (full beta upload state machine), `GraphUserService` (hire/offboard), `.intunewin` reader, tenant client factory, Identity workflows (MFA/password reset, lockdown, license repair). |
| `PartnerCenterBridge.Exchange` | `ExchangeOnlineService` — mailbox config via EXO PowerShell V3 (app-only cert), run out-of-process through `PwshRunner`; the mailbox-archive workflow. |
| `PartnerCenterBridge.Api` | Controllers, OIDC auth, DI wiring, deploy + provisioning orchestration, workflow dispatch + run recording. |
| `web/` | React SPA: Dashboard, Find User, Tenants, Contracts, App Templates, Deploy wizard, History, New Hire, Offboard, Workflows. |

## Run locally (docker-compose)

Auth is disabled in compose so you can click through the UI without an IdP.

```bash
# Fastest: published images, no clone or build needed.
curl -LO https://raw.githubusercontent.com/Spillers-Technology/PartnerCenterBridge/main/docker-compose.ghcr.yml
docker compose -f docker-compose.ghcr.yml up

# Or build from source (from a clone):
docker compose up --build

# Either way:
# SPA:     http://localhost:8082
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
can act in a given tenant. The delegated permissions the multi-tenant app registration needs
are listed in the [getting-started guide](https://spillers-technology.github.io/PartnerCenterBridge/getting-started.html).

## License

[MIT](LICENSE). See [CONTRIBUTING.md](CONTRIBUTING.md) for how to get involved and
[SECURITY.md](SECURITY.md) for reporting vulnerabilities.
