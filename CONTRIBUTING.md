# Contributing

Thanks for your interest! This project aims to make cross-tenant Microsoft 365 operations
repeatable and transparent for MSPs. Contributions that add known-fix workflows, improve
diagnosis transparency, or harden the auth planes are especially welcome.

## Workflow

- Every change is a feature branch (`feat/...`, `fix/...`) off `main`, merged via PR with a
  merge commit. No direct pushes to `main`.
- Keep PRs focused; describe what was verified (tests, compose stack) in the body.
- Source stays ASCII-only in string literals (non-ASCII literals have been mis-decoded by
  builds on some machines).

## Local development

```bash
docker compose up --build     # Postgres + API (:5080) + SPA (:8082), auth disabled
dotnet test                   # xUnit + WireMock; no real tenant needed
cd web && npm install && npm run dev
```

The test suite covers orchestration against WireMock, so most changes don't need a live tenant.
For live calls, the Windows workbench supports direct Microsoft tenant sign-in; server deployments
use the partner SAM/GDAP connection. Exchange Online has separate certificate setup. See the
[connection guide](https://spillerstech.us/PartnerCenterBridge/getting-started.html#connect-microsoft).

## Adding a workflow

Implement `IWorkflow` (diagnose -> remediate -> re-diagnose) in the backend project that owns
the API surface you're calling (`Graph` or `Exchange`), register it in that project's
`Add*Workflows()` extension, and add WireMock (or service-stub) tests. The API and UI pick it
up automatically — no controller or frontend changes needed. Findings should surface *why*
something is broken, not just that it is; the operator sees them verbatim.

## Adding a tenant audit check

Implement `ITenantAuditCheck` in `src/PartnerCenterBridge.Core/TenantAudits/Checks/<Category>/`.
Checks are discovered by type, so there is nothing to register and no controller, UI or export
change. Read tenant data only through the audit data providers (`IAuditDirectoryData` and
friends); they fetch each dataset once per run and turn "PCB can't read this here" into an
Unavailable result. Grade only what the data proves (use an Unknown finding for the rest), keep
business impact to a sentence or two, and test the check with the in-memory fakes in
`tests/PartnerCenterBridge.Tests/TenantAudits/TenantAuditTestSupport.cs`. The full guide is
[Adding a check](https://spillerstech.us/PartnerCenterBridge/tenant-audits.html#adding-a-check).

## Conventions

- .NET 8, nullable enabled; match the existing comment density and style.
- Anything an operator does should leave an audit trail (workflow runs are recorded
  automatically; new side-effectful endpoints should follow suit).
- Secrets never land in logs, run history, or notification payloads. Use
  `WorkflowRunResult.Ephemeral` for show-once values.
