# Roadmap

Things we want to come back to. Not scheduled, not sequenced -- just tracked so they don't get lost.

## Next

- **Onboarding policy.** A contract-defined new-user plan, mirroring offboarding policy v2's
  plan -> apply -> verify loop with structured evidence, instead of the current one-shot
  provisioning templates. Today a new hire isn't recorded as a run anywhere PCB tracks history --
  there's no plan preview, no verification against what Microsoft actually reports back, and
  nothing in Activity or a person's History tab to show for it.
- **Tenant sign-in, Phase 1** (see `docs/specs/tenant-sign-in-review.md`): truthful per-tenant
  connection state instead of one green tenant badge, safe (generation-checked) SAM token
  rotation so a concurrent re-seed can't be overwritten by a stale acquisition, a **Reconnect
  partner account** action in Settings that goes through the system browser instead of the CLI
  device-code flow, and a branded **Sign in with Microsoft** button that follows Microsoft's
  official branding guidelines for that control.
- **"Edit person" planned operation.** Job title, department, manager, and phone number as a
  planned change with a diff preview and post-apply verification, the same shape as other planned
  operations -- not a fire-and-forget PATCH.

## Tenant audits: next steps

The engine, 25 checks, history and CSV/JSON/Markdown export shipped (see `docs/tenant-audits.html`).
Natural follow-ups, roughly in order of value:

- **Estate runs.** Run one selection across every connected tenant (the engine and the
  `BatchId` column already support it) and surface `GET /api/tenant-audits/estate` in the UI.
  Needs a background runner that keeps each tenant's delegated token context; today runs are
  synchronous in the request, one tenant at a time.
- **Scheduled audits** with a "what changed since last run" diff of findings.
- **More checks:** forwarding inbox rules (per-mailbox `Get-InboxRule`, bounded like Full
  Access), mailbox sizes so shared-mailbox license findings can tell when 50 GB requires a
  license, PIM-eligible role assignments (`roleEligibilitySchedules`, Entra ID P2), app-only
  permission grants (app role assignments to third-party service principals), apps with expiring
  or long-lived credentials, Secure Score and Defender recommendations where licensed, SharePoint
  external sharing settings, and Intune OS version currency.
- **Remediation hand-off from more findings** into planned operations (bulk "plan offboarding"
  for selected dormant users; a planned "remove licenses" operation).
- **MCP tools** for running audits and reading findings, gated by the same tenant grants.

## Tenant sign-in: later phases

The v0.9.1 Local Workbench now includes a separate system-browser delegated connection per
operator and tenant, encrypted MSAL caches, silent renewal, and account-specific reconnect.
The broader SAM health/rotation work below remains separate; direct sign-in does not implement
the partner reconnect flow, app-only direct connections, or WAM.

Phases 2-4 of `docs/specs/tenant-sign-in-review.md`'s phased design, after Phase 1 above lands:
GDAP customers arriving with actionable state and one-click remediation (verify access, grant
consent, request a missing role, complete group assignment, configure Exchange); direct-customer
application connections (app-only, certificate-based, independent of the partner SAM connection);
and WAM as a separate desktop token provider for delegated connections, kept apart from the
existing SAM refresh-token extractor and server automation.

The v0.9.2 Desktop Workbench is a thin WebView2 host for the same API. It does not acquire
Microsoft tokens itself. `ITokenProvider` and `DirectTenantConnection` are the current selection
seam: direct delegated connections are selected by tenant and local operator; the partner/SAM
fallback remains separate. Before adding direct app-only or WAM, introduce an explicit connection
catalog with capability metadata and deterministic selection per tenant/operation, including a
clear ambiguity/consent result. Keep WAM in a provider below that catalog, never in the WPF UI.

## Mobile UX testing

The manual half of this landed: `docs/scripts/capture-mobile-media.mjs` screenshots all current
views across five touch device profiles (Galaxy/iPhone/Pixel/folded-foldable/unfolded-foldable) and
asserts no page-level horizontal overflow at each -- see `docs/mobile.md`. CI runs the overflow
check on three of the five profiles for every PR (`.github/workflows/ui-overflow.yml`), and as of
the `feat/ops-workbench` branch `.github/workflows/ci.yml` also runs `dotnet test`, `vitest run`,
and both `dotnet build`/`npm run build` on every PR and push to `main` (plus a Windows job that
publishes and smoke-tests the Local Workbench exe). Still open: the mobile screenshots themselves
are only reviewed by a human, not asserted against a baseline.

## Config Snapshots v2

Current Config Snapshots (section/whole-tenant diff, workbooks, git sync) works but is limited.
No specifics yet -- revisit once there's a concrete pain point driving the next iteration.

## Design language: "quest-driven" playful nudges

The "quest chip" nudge (amber, encouraging, literally the fix-it action rather than just an
explanation) started in the Contracts desired-app editor's disabled-template state and is now a
shared `QuestChip` component also used by the diagnostics list and the settings screens. Generalizing
it as a deliberate mental model for every "this is disabled/incomplete" state across the app is
still not committed to as a system -- revisit with a fuller inventory of where it does and doesn't
fit.

## Kiota-generated Graph client is on a preview package

`PartnerCenterBridge.Graph` depends on `Microsoft.Graph.Beta` `5.77.0-preview` (Kiota-generated).
Preview packages carry their own advisory/breaking-change cadence independent of the rest of the
.NET 8 dependency set pinned in `CLAUDE.md`'s release notes -- watch it for a stable release or a
security advisory the way the other pinned transitive packages are watched.

## Exchange operations for hideFromGal and managerAccess

Offboarding policy v2 has `hideFromGal` and `managerAccess: FullAccess` fields, but both are always
ineligible: there is no Exchange Online operation implemented for hiding a mailbox from the address
list (`Set-Mailbox -HiddenFromAddressListsEnabled`) or granting a manager Full Access
(`Add-MailboxPermission -AccessRights FullAccess`) yet. The plan says so and points at the Exchange
admin center as the manual fallback. Wiring these into `ExchangeOnlineService`/`OffboardingOperation`
is the natural next step once the policy shape has seen real use.

## Delayed-deletion scheduler for offboarding follow-up

`OffboardingPolicy.FollowUpDays` is recorded in evidence and in the plan's warnings only -- PCB
does not schedule anything or come back to delete the account itself. A real implementation needs
a background scheduler (and a decision about what "delete" means: disable further, actually delete
the Entra object, or just surface a due list) that this branch deliberately did not build.

## SharePoint direct-permission parity

Access Parity's stated limitation list includes SharePoint direct (non-group) site and file
permissions, which are not compared or copied at all today -- only group memberships and directory
roles are. Closing this gap needs the SharePoint REST/Graph permissions surface, which is a
meaningfully different API shape from group membership and was explicitly left out of this pass.

## MCP access-parity approval should pin the previewed item set

`plan_access_parity` (MCP, read-only) and the Access Parity apply endpoint both re-plan
server-side, which is correct for "only still-eligible items run." But the MCP apply path goes
through the existing `remediate_workflow` + approval queue, which (like every other workflow
today) applies whatever is eligible *at approval time*, not necessarily the exact item set the
approver saw when they reviewed the pending action. For an additive operation like Access Parity
the blast radius of that gap is small, but a queued approval should arguably pin and re-verify the
specific items it showed rather than silently re-planning wider or narrower before it applies.
