# Delegated Instance RBAC and Tenant Authorization Design

**Status:** Approved for implementation by the release-gate security audit. The safe v1 default
keeps tenant onboarding Administrator-only; no custom roles, OIDC group mapping, or instance-role
tenant bypass is introduced.

## Problem

Local authentication currently has two authorization mechanisms:

- `TenantAccessGrant` with Viewer, Operator, and Owner for one tenant.
- `AppUser.IsSystemAdmin`, an all-or-nothing instance flag embedded in issued JWTs.

The separation is conceptually correct, but the instance flag cannot be delegated and becomes
stale until a token expires. A controller sweep also found tenant endpoints that never consult
`TenantAccessGrant`. Because Local registration is open, a new zero-grant account can currently
reach cross-tenant reads, Exchange and provisioning mutations, and tenant onboarding.

This is a release gate: delegated instance RBAC and the tenant-authorization sweep ship together.

## Security invariants

1. Instance authority and tenant authority remain separate services and data models.
2. No instance role, including Administrator, satisfies a tenant role check.
3. No tenant role, including Owner, satisfies an instance permission check.
4. Local authorization is resolved from the database for every request. Issued-token claims never
   preserve a demoted role.
5. OIDC and Dev retain their existing trusted-operator all-access behavior in v1.
6. UI capability checks are presentation only; every API and MCP path enforces the server gate.
7. A denied request performs no Graph, Exchange, SAM, package, or deployment side effect.

## Fixed roles and permissions

Persist a flags enum of fixed roles on `AppUser`; map roles to permissions in one code-defined
table. Do not add custom role definitions, inheritance, groups, invitations, or deny rules.

| Role | Permissions |
| --- | --- |
| Administrator | Every instance permission |
| CatalogManager | `instance.catalog.manage` |
| CredentialManager | `instance.sam.manage` |
| AutomationPolicyManager | `instance.mcp-policy.manage` |

Administrator is mutually exclusive with delegated roles. A user can combine delegated roles.
Tenant registry onboarding remains Administrator-only through
`instance.tenant-registry.manage`; a future registrar role would require its own product decision
about initial tenant ownership.

`instance.roles.manage` is Administrator-only. It allows listing Local users and replacing their
fixed instance-role assignments.

## Data model and migration

Replace `AppUser.IsSystemAdmin` with:

- `InstanceRoles` (integer flags, non-null, default None)
- `AuthorizationVersion` (long, non-null, default 1; optimistic UI concurrency token)

Add a singleton `InstanceAuthorizationState` row. Registration bootstrap and all role mutations
lock this row within a transaction so concurrent first registrations cannot both become
Administrator and concurrent demotions cannot remove the last active Administrator.

The migration maps every existing `IsSystemAdmin=true` user to Administrator, all other users to
None, creates/seeds the singleton row, then drops `IsSystemAdmin`. Tenant grants are unchanged.
Local startup fails closed if users exist but no active Administrator remains after migration.

## Dynamic Local-token authorization

New JWTs and MCP PATs carry identity only, not instance roles. Local token validation resolves the
`pcb:userid` user, rejects missing/inactive users, and validates that an MCP token belongs to the
same user and is unrevoked. The legacy `pcb:sysadmin` claim on already-issued tokens is ignored.

`IInstanceAccessService.HasPermissionAsync` resolves current roles from the database and treats
OIDC/Dev callers as unrestricted for backward compatibility. `ITenantAccessService` remains
responsible only for tenant grants and gains a reusable authorized-tenant-id query for collection
filtering.

An operation whose authorization decision began before a concurrent demotion may complete. Every
decision begun after the demotion commits observes the new roles.

## Instance endpoint matrix

| Surface | Permission |
| --- | --- |
| List/replace Local user roles | `instance.roles.manage` |
| SAM status and seed/rotation | `instance.sam.manage` |
| Tenant MCP approval-mode change | `instance.mcp-policy.manage` |
| App-template authoring/package mutation | `instance.catalog.manage` |
| Contract create and desired-app mutation | `instance.catalog.manage` |
| Provisioning-template upsert | `instance.catalog.manage` |
| Tenant create and Partner Center sync | `instance.tenant-registry.manage` |

Role replacement cannot target the current caller, cannot bind unknown bits/names, requires the
target to be active, rejects removal of the last active Administrator with 409, and rejects a
stale `AuthorizationVersion` with 412. Role change and audit event commit atomically.

## Tenant endpoint matrix

- Viewer: tenant list; dashboard/history scoped rows; global search within visible tenants;
  directory and Exchange reads; workflow diagnosis; config snapshot reads.
- Operator: Viewer plus Exchange remediation/nudge, hire/offboard, deployment, workflow
  remediation, pending-action handling, and config snapshot capture/import.
- Owner: Operator plus tenant access sharing and contract assignment.

Collection endpoints always filter by active, non-expired grants in Local mode. Administrator is
never an unfiltered shortcut. Pending-action and other opaque IDs should be treated as not found
when outside the caller's tenant set so existence is not disclosed.

Global workflow/config catalogs remain authenticated reads. Contract/app-template catalog reads
remain available where the existing tenant UI needs them, but tenant identities/counts/plan rows
are scoped to Viewer grants unless the caller is non-Local. CatalogManager can author catalog data
without acquiring tenant visibility.

MCP tools use the same scoped services as REST. PATs remain confined to `/mcp` and resolve current
database roles/grants on every request.

## API and UI

`MeDto` adds `instanceRoles`, `instancePermissions`, and `authorizationVersion`.
`isSystemAdmin` remains for one compatibility release as a derived Administrator value.
Expired tenant grants are excluded.

The frontend uses permission helpers for instance controls. Security gains an Instance access
card for Administrators with fixed-role checkboxes, a disabled current-user row, and direct 409/412
messages. OIDC/Dev (`me === null`) retain all controls.

## Audit

Add explicit durable events for bootstrap Administrator assignment, instance role changes, SAM
credential rotation, and MCP approval-mode change. Role-change details contain actor, target,
before/after role names, and authorization version. SAM audit details never include token material.
Audit writes share the mutation transaction.

## Verification

Tests must prove:

- each fixed role succeeds only for its instance permission;
- Administrator does not bypass tenant roles and Owner does not gain instance permissions;
- cross-tenant rows are absent across REST and MCP collection/query paths;
- denied Graph/Exchange/SAM/package/deploy calls have zero side effects;
- demotion and deactivation invalidate already-issued interactive JWTs and PATs on the next request;
- legacy `pcb:sysadmin=true` is ignored;
- first-admin, last-admin, stale-editor, and last-owner concurrency invariants;
- migration preservation and audit atomicity/redaction;
- frontend controls follow resolved permissions without being treated as authorization evidence.

PostgreSQL-specific lock and migration behavior must be verified against PostgreSQL, not inferred
from the SQLite unit fixture.
