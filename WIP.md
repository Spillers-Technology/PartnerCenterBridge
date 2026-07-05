# WIP — Workflow Library (feat/workflow-library)

Stopped mid-task. This branch adds a generic "known-fix" workflow library
(Diagnose -> Fix -> re-Diagnose) plus the first two Graph-backed workflows.

## Branch
`feat/workflow-library` (off `main`, which has PRs #1-#4 merged). NOT committed/pushed yet.

## Done (all builds green, 34/34 tests pass as of last run)
- **Core** `src/PartnerCenterBridge.Core/Workflows/`
  - `IWorkflow.cs` — `IWorkflow`, `Finding`/`FindingStatus`, `DiagnosisResult`, `WorkflowRunResult`, `WorkflowInput`
  - `WorkflowCatalog.cs` — registry over `IEnumerable<IWorkflow>`
- **Graph** `src/PartnerCenterBridge.Graph/`
  - `TenantGraphRest.cs` (internal) — per-tenant `GraphRestClient` factory
  - `Workflows/WorkflowSteps.cs` — best-effort step helper
  - `Workflows/LicenseRepairWorkflow.cs` — usage location + reprocess stuck SKUs
  - `Workflows/MfaResetWorkflow.cs` — revoke sessions + delete registered auth methods
  - `Workflows/GraphWorkflowRegistration.cs` — `AddGraphWorkflows()` DI extension
- **Api**
  - `Controllers/WorkflowsController.cs` — GET `/api/workflows`, POST `/api/workflows/{id}/diagnose|remediate`
  - `Program.cs` — registered `WorkflowCatalog` + `AddGraphWorkflows()` (+ using `PartnerCenterBridge.Graph.Workflows`)
- **Web** `web/src/`
  - `types.ts` — Finding/DiagnosisResult/WorkflowRunResult/WorkflowSummary/WorkflowInput
  - `api.ts` — `api.workflows.{list,diagnose,remediate}`
  - `components/Workflows.tsx` — generic catalog + dynamic input form + findings/steps
  - `App.tsx` — "Workflows" tab wired; `styles.css` — `.wf-layout` etc.
- **Tests** (WireMock) — `LicenseRepairWorkflowTests.cs`, `MfaResetWorkflowTests.cs`
- **Live-verified** GET `/api/workflows` returns both workflows with input schemas.

## Last action (the reason I stopped)
Fixed em-dash mojibake: two user-facing string literals in
`src/PartnerCenterBridge.Graph/Workflows/LicenseRepairWorkflow.cs` (the `Description`
and the "Usage location" Blocker detail) had a U+2014 em-dash that the compiler
mis-decoded, so the catalog JSON showed `â€"`. Replaced both with ASCII "-".
**These two edits are done but NOT yet rebuilt/re-verified/committed.**

## Remaining steps to finish
1. `dotnet build PartnerCenterBridge.sln` + `dotnet test` (expect 34/34).
2. Rebuild API container and re-fetch `GET http://localhost:5080/api/workflows`;
   confirm the description now reads "won't apply - sets ..." (no mojibake).
3. Sanity: `git add -A` then confirm no `bin/`, `obj/`, `node_modules/`, `dist/` staged.
   Also `WIP.md` (this file) should NOT be committed — delete or leave untracked.
4. Commit with **no AI attribution** (per user's standing instruction: no `Co-Authored-By`),
   push `feat/workflow-library`, open PR against `main` with a clean body (no robot footer).
5. Merge workflow: user has been merging each PR via `gh pr merge <n> --merge --delete-branch`.

## Notes / conventions in this repo
- Every feature = own `feat/*` branch -> PR -> merge commit -> delete branch. No AI attribution anywhere.
- Local Docker is disposable; compose stack runs API on :5080, web :8082, db :5433. Auth disabled in compose.
- Integration boundary: real Graph/EXO calls need a real GDAP tenant + SAM bootstrap; WireMock covers orchestration.
- **Keep source ASCII-only** — non-ASCII string literals get mis-decoded by the compiler here (see em-dash issue above).

## Teed-up next (not started)
More workflows in the same pattern: shared mailbox / calendar permissions (EXO),
stuck mail flow / connectors, etc. Phase 4 (two-way LDAP) still tabled.
