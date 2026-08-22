# Contracts desired-app editor -- design

## Context

`Contract.DesiredApps` already exists in the domain model and is consumed read-only by
`ContractsController.Plan` (the dry-run reconcile preview). The architectural review found that
the navigation was actually backed by the single nullable `AppTemplate.ContractId` foreign key,
despite the desired-state model needing reusable templates across contracts. The editor therefore
also corrects that relationship to a real many-to-many join. `Contracts.tsx` already displays a
`desiredAppCount` per contract, but there is currently no API or UI for changing the membership.
The gap came from the usability workstream's friction survey and was logged to `ROADMAP.md`.

## Goals

- Let a system admin add/remove app templates from a contract's desired-app list, from the
  Contracts screen, without navigating away.
- Keep templates that can't actually be deployed (no package attached) out of the way by default,
  while making it easy and inviting to notice and fix that gap when the admin does look for them.
- Correct desired-app membership to a many-to-many join without losing existing assignments.

## Non-goals

- Broader desired-state facets (groups, license SKUs, policies) mentioned as future phases in
  `Contract.cs`'s own doc comment -- out of scope here.
- A general RBAC/roles overhaul -- this uses the existing `IsSystemAdmin` flag, matching
  `AppTemplatesController`'s existing gate exactly. A real roles-with-delegation model is flagged
  as its own `ROADMAP.md` item, to happen before the next version release, not as part of this spec.
- The "quest-driven" playful nudge treatment used here for the disabled-template state is not
  being retrofitted anywhere else in the app as part of this spec -- also a `ROADMAP.md` idea to
  revisit as a possible app-wide design language later.

## Backend

### Relationship correction and migration

`Contract.DesiredApps` and a new `AppTemplate.DesiredByContracts` navigation use the
`ContractDesiredApps` join table with a composite `(ContractId, AppTemplateId)` key. The migration
copies every existing non-null `AppTemplates.ContractId` pair into the join before the new editor is
used. The legacy optional owner field remains for API/data compatibility, but it no longer backs
desired-state membership. Creating a new package-less template with that legacy field preserves the
owner metadata but does not bypass the package-readiness invariant by adding a desired membership.

### `ContractDto` gains one field

```csharp
public record ContractDto(Guid Id, string Name, string? Notes, int TenantCount, int DesiredAppCount,
    IReadOnlyList<Guid> DesiredAppIds)
{
    public static ContractDto From(Contract c) => new(
        c.Id, c.Name, c.Notes, c.Tenants.Count, c.DesiredApps.Count,
        c.DesiredApps.Select(a => a.Id).ToList());
}
```

The frontend checklist needs to know *which* templates are already desired, not just a count.

### Two new endpoints on `ContractsController`

```csharp
[HttpPost("{id:guid}/desired-apps/{templateId:guid}")]
public async Task<ActionResult<ContractDto>> AddDesiredApp(Guid id, Guid templateId, CancellationToken ct)
{
    if (!_access.IsSystemAdmin) return Forbid();
    var contract = await _db.Contracts.Include(c => c.Tenants).Include(c => c.DesiredApps)
        .FirstOrDefaultAsync(c => c.Id == id, ct);
    if (contract is null) return NotFound();
    var template = await _db.AppTemplates.FindAsync([templateId], ct);
    if (template is null) return NotFound();
    var alreadyDesired = contract.DesiredApps.Any(a => a.Id == templateId);
    if (!alreadyDesired && template.Content is null)
        return Conflict("Attach a package before adding this template to desired state.");
    if (!alreadyDesired) contract.DesiredApps.Add(template);
    await _db.SaveChangesAsync(ct);
    return ContractDto.From(contract);
}

[HttpDelete("{id:guid}/desired-apps/{templateId:guid}")]
public async Task<ActionResult<ContractDto>> RemoveDesiredApp(Guid id, Guid templateId, CancellationToken ct)
{
    if (!_access.IsSystemAdmin) return Forbid();
    var contract = await _db.Contracts.Include(c => c.Tenants).Include(c => c.DesiredApps)
        .FirstOrDefaultAsync(c => c.Id == id, ct);
    if (contract is null) return NotFound();
    var existing = contract.DesiredApps.FirstOrDefault(a => a.Id == templateId);
    if (existing is not null) contract.DesiredApps.Remove(existing);
    await _db.SaveChangesAsync(ct);
    return ContractDto.From(contract);
}
```

Both endpoints are idempotent (adding an already-desired app, or removing one that isn't there,
is a harmless no-op success) and return the fresh `ContractDto` so the frontend updates from one
response instead of a separate list refetch. `ContractsController` needs `ITenantAccessService
_access` injected (constructor addition) -- `AppTemplatesController` already does exactly this;
follow that same pattern. A new membership for a package-less template returns `409 Conflict`; an
already-desired legacy package-less template remains an idempotent success so it can be repaired by
the quest-chip upload flow. A unique-key/concurrency collision is reloaded and accepted only when
the database already reflects the requested state, preserving idempotence across overlapping calls.

The review also found that `AppTemplatesController.Create` and `UploadPackage` were missing the
system-admin gate already used by its Update/Delete mutations. Both are gated as part of this work,
because the editor directly exposes package replacement and UI visibility is not authorization.

`ITenantAccessService.IsSystemAdmin` gating a **contract-level** (not tenant-scoped) mutation is
not the tenant-bypass anti-pattern `CLAUDE.md` warns about -- that warning is specifically about
`IsSystemAdmin` bypassing a per-tenant `TenantAccessGrant` role check on a tenant-scoped resource.
A contract's desired-app list isn't tenant-scoped; this mirrors `AppTemplatesController`'s existing,
already-accepted use of the same flag for the same reason (blast radius: changes what deploys
across every tenant on the contract).

## Frontend

### `api.ts`

```typescript
contracts: {
  list: () => request<Contract[]>("/api/contracts"),
  create: (name: string, notes?: string) => ...,
  plan: (id: string) => ...,
  addDesiredApp: (contractId: string, templateId: string) =>
    request<Contract>(`/api/contracts/${contractId}/desired-apps/${templateId}`, { method: "POST" }),
  removeDesiredApp: (contractId: string, templateId: string) =>
    request<Contract>(`/api/contracts/${contractId}/desired-apps/${templateId}`, { method: "DELETE" })
}
```

`types.ts`'s `Contract` interface gains `desiredAppIds: string[]`.

### `Contracts.tsx`

- Gains a `me: MeProfile | null` prop (currently has none) -- `App.tsx` passes `<Contracts me={me} />`,
  matching `AppTemplates`' exact call shape. `canManage = !me || me.isSystemAdmin`, same expression
  `AppTemplates.tsx` already uses.
- Also needs the full `AppTemplate[]` list loaded (for the checklist) -- add `api.templates.list()`
  to the existing tenant/contract load, alongside `contracts`.
- A "Manage apps" button per contract row (visible only when `canManage`), next to the existing
  "Preview plan" button. Clicking it expands a checklist inline below that row -- the same place
  `plan` already expands its own table. Uses its own `managingId: string | null` state, independent
  of `plan`'s existing single-panel state: opening "Manage apps" for one contract closes any other
  contract's open checklist (mirroring `plan`'s existing one-at-a-time behavior), but does not
  affect or get affected by a "Preview plan" panel open elsewhere -- they're different concerns
  (editing desired state vs. previewing its dry-run diff) and a contract's own plan preview and its
  desired-app checklist can reasonably be open side by side.
- **Checklist rendering:**
  - By default, only `AppTemplate`s with `hasPackage: true` are listed, each as a `Checkbox` +
    label, checked if `contract.desiredAppIds.includes(template.id)`.
  - A `Switch` labeled "Show templates without a package" (off by default) reveals the rest.
  - A revealed no-package template renders **disabled** with the "actionable quest chip" instead of
    a plain checkbox: an amber `Chip` reading "So close! Attach a package to unlock →", itself
    a keyboard-activatable button that triggers a hidden `<input type="file">`, wired to
    `api.templates.uploadPackage(template.id, file)`. Its successful response updates that template
    immediately, so a secondary refresh failure cannot misreport a committed upload as failed. A
    per-template pending set disables repeated uploads until completion. A checked legacy
    package-less membership remains removable even though new package-less memberships are blocked.
- **Toggling a checkbox:**
  - `pendingIds: Set<string>` (component state, not `useAsyncAction`) tracks which template IDs
    currently have an add/remove call in flight. A checkbox is `disabled={pendingIds.has(template.id)}`.
  - `toggle(contractId, templateId, checked)`: adds `templateId` to `pendingIds` (functional
    `setPendingIds(prev => new Set(prev).add(templateId))`, so concurrent toggles of different
    templates never clobber each other's pending-set entry), calls
    `api.contracts.addDesiredApp`/`removeDesiredApp`, and merges only that template's membership from
    the response. Applying a whole DTO snapshot would let an older sibling response erase another
    completed toggle. An ambiguous failure performs a targeted membership reconciliation from a
    fresh contract list before showing the toast. Generation guards prevent older create/list or
    plan responses from overwriting a completed toggle. The template id is removed from
    `pendingIds` in a `finally`.
  - This is deliberately **not** a per-row extracted sub-component with its own `useAsyncAction`
    (unlike this session's `ApprovalRow`/`DeploymentRow` precedent) -- a boolean checkbox toggle is
    simple enough that a `Set`-based pending tracker gives the same "one item's in-flight call can't
    block or drop another's" guarantee without a full component extraction per template row, and a
    contract's checklist can have many more rows than a deployments/approvals table typically does.

## Error handling

- A failed add/remove: reconcile that membership from a fresh contract list, then toast with the
  bare error message (matching the "show `e.message`, never `Error: ...`" convention). This handles
  a connection failure that occurs after the server commits.
- 404 (contract or template deleted concurrently by someone else): same toast treatment; the admin
  can refresh the page to see current state. Not worth a special-cased recovery flow for this rare
  a race.
- A failed package upload from the quest chip: reuses `AppTemplates.tsx`'s own existing upload
  error handling/toast wording exactly (`showToast` on the shared `uploadAction`-equivalent path) --
  do not invent new copy for this specific trigger point.

## Testing

**Backend** (WireMock-free, matching existing `ContractsController` test conventions):
- `AddDesiredApp`/`RemoveDesiredApp` succeed and return the updated `ContractDto` with the correct
  `DesiredAppIds`.
- Both are idempotent: adding an already-present template, or removing an absent one, succeeds
  without error and leaves the list unchanged.
- Both `Forbid()` for a non-system-admin caller.
- Add returns `NotFound()` for a missing contract/template and `Conflict()` for a package-less
  template; remove returns `NotFound()` for a missing contract and otherwise treats absence as its
  idempotent no-op.
- One reusable template can remain desired by two contracts (regression coverage for the corrected
  many-to-many model), and existing ownership assignments are copied by the migration.
- App-template create/package upload reject non-system-admin callers.
- `Plan`'s existing reconcile behavior is unaffected (regression check, not new coverage).

**Frontend** (`Contracts.test.tsx`):
- "Manage apps" button only renders when `me.isSystemAdmin` (or `me === null`, matching
  `AppTemplates`' own `canManage` semantics).
- Expanding the checklist shows only `hasPackage: true` templates by default; toggling "Show
  templates without a package" reveals the rest, disabled, each showing the quest chip.
- Checking a box calls `addDesiredApp` with the right contract/template ids; the box then reflects
  the response's `desiredAppIds`.
- Unchecking calls `removeDesiredApp` the same way.
- **Two different templates toggled close together resolve independently** -- neither's pending
  state nor its API call blocks or drops the other's (the concurrency shape this session's own
  `ApprovalRow`/`DeploymentRow` fixes exist to guard against, verified here via the simpler
  `pendingIds`-Set mechanism instead of a per-row component).
- Older sibling-toggle, full-list, and plan responses cannot overwrite newer desired state.
- A failed toggle shows the bare error and reconciles ambiguous server state when possible.
- Clicking the quest chip opens the upload picker and, on a successful upload, the template moves
  out of the disabled/hidden state into the normal checked-or-unchecked list. The action is keyboard
  activatable and disabled while its template's upload is pending.
