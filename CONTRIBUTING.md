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
docker compose up --build     # Postgres + API (:5080) + SPA (:8081), auth disabled
dotnet test                   # xUnit + WireMock; no real tenant needed
cd web && npm install && npm run dev
```

Real Microsoft Graph / Exchange Online calls require a partner tenant with GDAP relationships
and a seeded SAM token (see the README). The test suite covers orchestration against WireMock,
so most changes don't need a live tenant.

## Adding a workflow

Implement `IWorkflow` (diagnose -> remediate -> re-diagnose) in the backend project that owns
the API surface you're calling (`Graph` or `Exchange`), register it in that project's
`Add*Workflows()` extension, and add WireMock (or service-stub) tests. The API and UI pick it
up automatically — no controller or frontend changes needed. Findings should surface *why*
something is broken, not just that it is; the operator sees them verbatim.

## Conventions

- .NET 8, nullable enabled; match the existing comment density and style.
- Anything an operator does should leave an audit trail (workflow runs are recorded
  automatically; new side-effectful endpoints should follow suit).
- Secrets never land in logs, run history, or notification payloads. Use
  `WorkflowRunResult.Ephemeral` for show-once values.
