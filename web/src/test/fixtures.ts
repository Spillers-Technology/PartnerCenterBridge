// Shared operation/evidence fixtures for component tests.
import type { OperationEvidence, OperationPlan } from "../types";

export function makeEvidence(overrides: Partial<OperationEvidence> = {}): OperationEvidence {
  return {
    runId: "run-1",
    operationId: "access-parity",
    operationName: "Access parity",
    tenant: { id: "t1", displayName: "Contoso Ltd", tenantId: "aaaa-1111" },
    target: { kind: "user", id: "u2", displayName: "Grace Hopper" },
    operator: "tech@example.com",
    startedAt: "2026-09-01T10:00:00Z",
    completedAt: "2026-09-01T10:00:05Z",
    outcome: "Succeeded",
    preflight: [{ name: "Source user", status: "Ok", detail: "Ada Lovelace" }],
    plan: [{ id: "group:g1", action: "AddMember", objectType: "group", objectId: "g1", objectName: "Finance Team", destructive: false, eligible: true, category: "Security" }],
    changes: [
      { planItemId: "group:g1", action: "AddMember", objectName: "Finance Team", attempted: true, succeeded: true, detail: "Added" },
      { planItemId: "group:g2", action: "AddMember", objectName: "VPN Users", attempted: true, succeeded: false, detail: "403 Forbidden" },
      { planItemId: "group:g3", action: "AddMember", objectName: "All Staff", attempted: false, succeeded: true, detail: "Already a member" }
    ],
    verification: [{ name: "Member of Finance Team", passed: true }],
    warnings: ["Target is synced from on-premises AD."],
    limitations: ["SharePoint direct permissions are not compared."],
    failures: [],
    ticketNotes: "Access parity for Grace Hopper\n- Added to Finance Team (verified)",
    ...overrides
  };
}

const item = (id: string, name: string, category: string, eligible: boolean, reason: string | null = null) => ({
  id: `group:${id}`, action: "AddMember", objectType: "group", objectId: id, objectName: name,
  destructive: false, eligible, category, reason
});

export function makeParityPlan(overrides: Partial<OperationPlan> = {}): OperationPlan {
  return {
    operationId: "access-parity",
    operationName: "Access parity",
    tenantId: "t1",
    target: { kind: "user", id: "u2", displayName: "Grace Hopper" },
    preflight: [
      { name: "Source user", status: "Ok", detail: "Ada Lovelace (ada@contoso.com)" },
      { name: "Target user", status: "Ok", detail: "Grace Hopper (grace@contoso.com)" }
    ],
    items: [
      item("g1", "Finance Team", "Security", true),
      item("g2", "Project Apollo", "Microsoft365", true),
      item("g3", "All Finance (dynamic)", "Dynamic", false, "Dynamic membership rule; membership follows the user's attributes."),
      item("g4", "Helpdesk Admins", "RoleAssignable", false, "Role-assignable group; PCB does not grant privileged access."),
      item("g5", "finance-dl@contoso.com", "Distribution", false, "Distribution list; manage it in Exchange."),
      item("g6", "All Staff", "AlreadyMember", false, "Target is already a member."),
      { id: "role:r1", action: "AssignRole", objectType: "directoryRole", objectId: "r1", objectName: "Billing Administrator", destructive: false, eligible: false, category: "DirectoryRole", reason: "Directory roles are never copied." }
    ],
    warnings: ["Ada Lovelace has 1 directory role; it is not copied."],
    limitations: [
      "SharePoint direct permissions, app role assignments, Exchange mailbox/calendar permissions and Teams-only private channels are not compared."
    ],
    ...overrides
  };
}
