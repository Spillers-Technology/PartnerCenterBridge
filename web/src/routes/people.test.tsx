import { beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { localMe, mockMatchMedia, renderWithRouter, testSession } from "../test/renderWithRouter";
import { PersonWorkspacePage } from "./people";
import type { PersonWorkspace, Tenant, WorkflowSummary } from "../types";

vi.mock("../api", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api")>();
  return {
    ...actual,
    api: {
      tenants: { list: vi.fn() },
      people: { get: vi.fn() },
      workflows: { list: vi.fn(), runs: vi.fn() }
    }
  };
});

import { api } from "../api";

const TENANTS: Tenant[] = [{ id: "t1", tenantId: "aaaa", displayName: "Contoso Ltd", status: "Active" }];
const CATALOG: WorkflowSummary[] = ["mfa-reset", "password-reset", "compromised-lockdown", "license-repair", "mailbox-archive"]
  .map((id) => ({ id, name: id, description: "", category: "Identity", inputs: [] }));

function workspace(overrides: Partial<PersonWorkspace> = {}): PersonWorkspace {
  return {
    tenantId: "t1",
    userId: "u1",
    profile: {
      status: "Ok",
      data: {
        id: "u1", displayName: "Ada Lovelace", upn: "ada@contoso.com", mail: "ada@contoso.com", accountEnabled: true,
        jobTitle: "Engineer", department: "R&D", onPremisesSyncEnabled: true, createdDateTime: "2024-01-01T00:00:00Z", lastSignIn: null
      }
    },
    licenses: { status: "Ok", data: [{ skuPartNumber: "SPE_E3", skuId: "sku-e3" }, { skuPartNumber: "VISIOCLIENT", skuId: "sku-visio" }] },
    groups: {
      status: "Ok",
      data: [
        { id: "g1", displayName: "Finance Team", category: "Security" },
        { id: "g2", displayName: "All Finance (dynamic)", category: "Dynamic" },
        { id: "g3", displayName: "finance-dl", category: "Distribution" }
      ]
    },
    authMethods: { status: "Ok", data: ["password", "microsoftAuthenticator"] },
    mailbox: {
      status: "Ok",
      data: { userPrincipalName: "ada@contoso.com", displayName: "Ada Lovelace", recipientTypeDetails: "UserMailbox", forwardingSmtpAddress: null, deliverToMailboxAndForward: false }
    },
    devices: {
      status: "Ok",
      data: [{ id: "d1", deviceName: "ADA-LAPTOP", operatingSystem: "Windows", osVersion: "10.0.26100", complianceState: "noncompliant", lastSyncDateTime: "2026-09-01T10:00:00Z", managementAgent: "mdm" }]
    },
    recentRuns: {
      status: "Ok",
      data: [{
        id: "r9", workflowId: "access-parity", workflowName: "Access parity", tenantId: "t1", tenantName: "Contoso Ltd",
        kind: "Apply", operator: "tech", inputs: {}, findings: [], steps: [], succeeded: false,
        startedAt: "2026-09-01T10:00:00Z", durationMs: 100, outcome: "PartiallySucceeded", targetId: "u1"
      }]
    },
    ...overrides
  };
}

function renderPerson(path = "/people/t1/u1", session = testSession()) {
  return renderWithRouter(<PersonWorkspacePage />, { path, route: "/people/:tenantId/:userId", session });
}

describe("PersonWorkspacePage", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockMatchMedia((q) => q.includes("min-width"));
    vi.mocked(api.tenants.list).mockResolvedValue(TENANTS);
    vi.mocked(api.workflows.list).mockResolvedValue(CATALOG);
    vi.mocked(api.workflows.runs).mockResolvedValue([]);
    vi.mocked(api.people.get).mockResolvedValue(workspace());
  });

  it("summarises the person at a glance, including the hybrid guardrail", async () => {
    renderPerson();
    const glance = await screen.findByRole("group", { name: "At a glance" });
    await waitFor(() => expect(within(glance).getByText("2 licenses")).toBeInTheDocument());
    expect(within(glance).getByText("3 groups")).toBeInTheDocument();
    expect(within(glance).getByText("1 method: Microsoft Authenticator")).toBeInTheDocument();
    expect(within(glance).getByText("User mailbox")).toBeInTheDocument();
    expect(within(glance).getByText("1 managed device")).toBeInTheDocument();
    expect(within(glance).getByText("Partially succeeded")).toBeInTheDocument();
    expect(within(glance).getByText("Synced from on-prem")).toBeInTheDocument();
    expect(screen.getByText("Hybrid account")).toBeInTheDocument();
  });

  it("never shows a zero for a section it could not read", async () => {
    vi.mocked(api.people.get).mockResolvedValue(workspace({
      licenses: { status: "Unavailable", reason: "The PCB app registration lacks Organization.Read.All in this tenant (Graph 403)." },
      devices: { status: "Error", reason: "Graph timed out" }
    }));
    renderPerson();
    const glance = await screen.findByRole("group", { name: "At a glance" });
    await waitFor(() => expect(within(glance).getByText("Unavailable")).toBeInTheDocument());
    expect(within(glance).getByText("Couldn't read")).toBeInTheDocument();
    expect(within(glance).queryByText("0 licenses")).not.toBeInTheDocument();
  });

  it("explains an unavailable mailbox and links to the workbench setup that fixes it", async () => {
    vi.mocked(api.people.get).mockResolvedValue(workspace({
      mailbox: { status: "Unavailable", reason: "Exchange Online module (ExchangeOnlineManagement) is not installed." }
    }));
    renderPerson("/people/t1/u1?tab=mailbox");
    expect(await screen.findByText("Mailbox unavailable")).toBeInTheDocument();
    expect(screen.getByText(/ExchangeOnlineManagement/)).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Check workbench setup" })).toHaveAttribute("href", "/settings/workbench");
  });

  it("shows a section error with a retry that reloads the workspace", async () => {
    vi.mocked(api.people.get).mockResolvedValueOnce(workspace({ groups: { status: "Error", reason: "Graph 503" } }));
    const user = userEvent.setup();
    renderPerson("/people/t1/u1?tab=access");
    expect(await screen.findByText("Couldn't read group memberships")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Retry" }));
    const list = await screen.findByRole("list", { name: "Group memberships" });
    expect(api.people.get).toHaveBeenCalledTimes(2);
    expect(within(list).getByText("Dynamic")).toBeInTheDocument();
    expect(within(list).getByText("Distribution list")).toBeInTheDocument();
  });

  it("offers contextual actions pre-filled for this person, flagging the disruptive ones", async () => {
    renderPerson();
    const actions = await screen.findByRole("list", { name: "Actions for this person" });
    await waitFor(() => expect(within(actions).getByRole("link", { name: /Reset MFA/ })).toBeInTheDocument());
    expect(within(actions).getByRole("link", { name: /Mirror access from another user/ }))
      .toHaveAttribute("href", "/operations/access-parity?tenant=t1&target=ada%40contoso.com");
    expect(within(actions).getByRole("link", { name: /Offboard/ }))
      .toHaveAttribute("href", "/operations/offboard?tenant=t1&user=ada%40contoso.com");
    expect(within(actions).getByRole("link", { name: /Mailbox archive fix/ }))
      .toHaveAttribute("href", "/operations/workflows/mailbox-archive?tenant=t1&user=ada%40contoso.com");
    expect(within(within(actions).getByRole("link", { name: /Offboard/ })).getByText("Disruptive")).toBeInTheDocument();
    expect(within(within(actions).getByRole("link", { name: /License repair/ })).queryByText("Disruptive")).not.toBeInTheDocument();
    expect(screen.queryByText(/Preview only/)).not.toBeInTheDocument();
  });

  it("hides known fixes the server doesn't have", async () => {
    vi.mocked(api.workflows.list).mockResolvedValue(CATALOG.filter((w) => w.id !== "mailbox-archive"));
    renderPerson();
    const actions = await screen.findByRole("list", { name: "Actions for this person" });
    await waitFor(() => expect(within(actions).queryByRole("link", { name: /Mailbox archive fix/ })).not.toBeInTheDocument());
  });

  it("tells a Viewer that actions are preview-only and why", async () => {
    renderPerson("/people/t1/u1", testSession(localMe({ t1: "Viewer" })));
    expect(await screen.findByText("You have Viewer access to Contoso Ltd")).toBeInTheDocument();
    const actions = screen.getByRole("list", { name: "Actions for this person" });
    expect(within(actions).getAllByText(/Preview only: applying needs Operator/).length).toBeGreaterThan(0);
  });

  it("does not show a person in a tenant that isn't shared", async () => {
    vi.mocked(api.tenants.list).mockResolvedValue([]);
    renderPerson("/people/t1/u1", testSession(localMe({})));
    expect(await screen.findByText("This tenant isn't shared with you")).toBeInTheDocument();
    expect(screen.queryByRole("list", { name: "Actions for this person" })).not.toBeInTheDocument();
  });

  it("lists authentication method types and warns when there is no second factor", async () => {
    vi.mocked(api.people.get).mockResolvedValue(workspace({ authMethods: { status: "Ok", data: ["password"] } }));
    renderPerson("/people/t1/u1?tab=auth");
    expect(await screen.findByText(/No second factor is registered/)).toBeInTheDocument();
    expect(within(screen.getByRole("list", { name: "Authentication methods" })).getByText("Password")).toBeInTheDocument();
  });

  it("shows managed devices with their compliance", async () => {
    renderPerson("/people/t1/u1?tab=devices");
    const list = await screen.findByRole("list", { name: "Managed devices" });
    expect(within(list).getByText("ADA-LAPTOP")).toBeInTheDocument();
    expect(within(list).getByText("Noncompliant")).toBeInTheDocument();
    expect(within(list).getByText("Windows 10.0.26100")).toBeInTheDocument();
  });
});
