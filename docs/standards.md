# Company standards status

Reconciled 2026-08-25 against `corporate-strategy/standards/STD-001` through
`STD-008` and this branch. Re-verify these claims against the live repo when the
standards or implementation changes.

| Standard | Status | Evidence / reason |
|---|---|---|
| STD-001 adversarial review | adopted | `docs/dev-process.md` contains the repo-local routing policy and a mature review log. |
| STD-002 PR template | adopted | `.github/PULL_REQUEST_TEMPLATE.md` now carries the required problem, checks, evidence, and synthetic-data clauses. |
| STD-003 doc freshness | not-yet (fix drafted) | The release checklist requires docs reconciliation, but `main` still has the stale README version claim; draft PR #35 fixes it and is deliberately unmerged. |
| STD-004 identity architecture | adopted | Local/OIDC modes, instance and tenant authorization planes, and attributable machine tokens are implemented. |
| STD-005 agent surface | adopted | MCP uses the product's identity plane and supports a per-tenant human approval queue for consequential actions. |
| STD-006 secret handling | adopted by stated deviation | `deploy/` is explicitly a placeholder-only template; live GitOps secrets belong SOPS-encrypted in `homelab_ac`, and real values must never enter this repo. |
| STD-007 repo metadata | not-yet | LICENSE exists, but no CHANGELOG exists and company-wide rights-holder/AI-attribution conventions remain unsettled. This repo explicitly forbids AI attribution. |
| STD-008 UI capture validation | adopted | Every capture uses `assertNoOverflow()` and CI now executes all views at three representative device widths. |
