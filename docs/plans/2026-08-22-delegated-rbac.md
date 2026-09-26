# Delegated RBAC Release-Gate Implementation Plan

**Goal:** Replace Local mode's stale, all-or-nothing system-admin claim with dynamically resolved
fixed instance roles, close every confirmed tenant-authorization bypass, and ship the next release
only after the REST/MCP/UI boundaries are independently reviewed and verified.

**Design:** `docs/superpowers/specs/2026-08-22-delegated-rbac-design.md`

## Constraints

- Keep instance permissions and tenant roles structurally separate.
- Preserve OIDC/Dev trusted-operator behavior.
- Keep C# string literals ASCII-only.
- No custom roles, OIDC mapping, invitations, registrar delegation, or user deactivation in v1.
- Use a feature branch, independent Sol/high security review, fix/re-review loop, PR, merge commit,
  and branch deletion before release work.

## Task 1: Authorization primitives and migration

- Add fixed `InstanceRole` and `InstancePermission` definitions plus the single role-to-permission
  mapping.
- Replace `AppUser.IsSystemAdmin` with role flags and `AuthorizationVersion`.
- Add/seed the singleton authorization-state row and a lock helper for bootstrap/role mutations.
- Add a data-preserving EF migration and inspect its generated SQL.
- Make Local token validation reject inactive/missing users, bind PATs to their owner, and ignore
  the legacy admin claim.
- Add `IInstanceAccessService`; keep `ITenantAccessService` tenant-only and add reusable scoped-id
  queries.
- Cover role mapping, dynamic demotion, inactive tokens, PAT ownership, and migration model shape.

## Task 2: Delegation API and invariants

- Add Administrator-only user/role list and exact-replacement endpoints.
- Re-read actor authority inside the locked transaction.
- Reject self-role mutation, unknown/invalid combinations, inactive targets, stale versions, and
  last-Administrator removal.
- Record bootstrap/role-change audit events atomically.
- Prevent tenant Owner changes that would remove the last active, permanent Owner.
- Cover happy paths, role isolation, last-admin/last-owner, self-escalation, and stale versions.

## Task 3: Instance endpoint conversion

- Gate SAM status/seed, MCP policy, app-template mutations, contract create/desired state,
  provisioning-template writes, and tenant create/sync with their exact instance permissions.
- Add explicit redacted SAM and MCP-policy audit events.
- Replace all frontend `isSystemAdmin` mutation checks with permission helpers while retaining the
  compatibility field in the DTO.
- Cover each role against each permission family and prove denied side effects do not execute.

## Task 4: Tenant authorization sweep

- Scope dashboard, search, deployment history, workflow history, contract counts/plans, and MCP
  dashboard/list tools to active grants.
- Add Viewer gates to directory and Exchange reads.
- Add Operator gates to Exchange remediation/nudge and hire/offboard.
- Preserve the existing role checks on deploy, workflows, pending actions, snapshots, sharing, and
  assignment; tighten opaque-ID visibility where needed.
- Cover tenant A/tenant B negative cases, including Administrator-with-zero-grants.

## Task 5: Instance-access UI

- Extend profile types/API with roles, permissions, and authorization version.
- Add permission helpers and use them across App, Contracts, App Templates, Tenants, and Security.
- Add an Instance access card with fixed delegated-role controls and disabled self-management.
- Refresh the profile after relevant changes and surface 409/412 messages.
- Cover permission visibility and role-editor behavior.

## Task 6: Verification and review

- Run targeted backend/frontend tests throughout.
- Run PostgreSQL migration and lock/concurrency checks.
- Run full backend build/tests, frontend tests/build, and mobile captures for touched views.
- Dispatch independent Sol/high security review over the whole branch, verify every claim against
  code, implement a fix wave, and obtain a clean scoped re-review.
- Log the unit in `docs/dev-process.md`, remove the completed ROADMAP item, update user docs, open
  and merge the PR, then begin the manual release checklist.
