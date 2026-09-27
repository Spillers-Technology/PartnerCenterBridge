import { beforeEach, describe, expect, it, vi } from "vitest";
import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, useNavigate, type NavigateFunction } from "react-router";
import { ThemeProvider } from "@mui/material/styles";
import { theme } from "./theme";
import { ConfirmDialogProvider } from "./hooks/useConfirm";
import { ToastProvider } from "./hooks/useToast";
import { LocationProbe, mockMatchMedia } from "./test/renderWithRouter";
import { makeEvidence } from "./test/fixtures";
import type { MeProfile, PersonWorkspace, Tenant, WorkflowRunRecord, WorkflowSummary } from "./types";

vi.mock("./api", async (importOriginal) => {
  const actual = await importOriginal<typeof import("./api")>();
  return {
    ...actual,
    api: {
      auth: { mode: vi.fn(), me: vi.fn(), login: vi.fn(), register: vi.fn(), logout: vi.fn(), launch: vi.fn(), setupNoAccount: vi.fn(), protectOwner: vi.fn() },
      totp: { challenge: vi.fn() },
      passkey: { loginOptions: vi.fn(), loginVerify: vi.fn() },
      system: { status: vi.fn(), diagnostics: vi.fn() },
      sam: { status: vi.fn(), seed: vi.fn() },
      people: { get: vi.fn() },
      dashboard: vi.fn(),
      pendingActions: { list: vi.fn() },
      search: { users: vi.fn() },
      tenants: { list: vi.fn(), setContract: vi.fn() },
      tenantAccess: { list: vi.fn() },
      contracts: { list: vi.fn() },
      templates: { list: vi.fn() },
      deployments: { list: vi.fn() },
      workflows: { list: vi.fn(), runs: vi.fn(), diagnose: vi.fn(), remediate: vi.fn(), evidence: vi.fn(), evidenceMarkdown: vi.fn(), evidenceJson: vi.fn() },
      directory: { users: vi.fn() },
      accessParity: { plan: vi.fn(), apply: vi.fn() },
      configSnapshots: { list: vi.fn(), sections: vi.fn() }
    }
  };
});
vi.mock("./webauthn", () => ({
  passkeysSupported: false,
  getPasskey: vi.fn(),
  conditionalMediationSupported: vi.fn().mockResolvedValue(false)
}));

import { api, ApiError } from "./api";
import { App } from "./App";

const TENANTS: Tenant[] = [
  { id: "t1", tenantId: "aaaa-1111", displayName: "Contoso Ltd", defaultDomain: "contoso.onmicrosoft.com", status: "Active" }
];

const CATALOG: WorkflowSummary[] = [
  {
    id: "mfa-reset", name: "MFA reset", description: "Clears MFA methods for a user.", category: "Identity",
    inputs: [{ key: "userUpn", label: "User UPN", required: true, type: "text" }]
  }
];

const RUNS: WorkflowRunRecord[] = [
  {
    id: "r1", workflowId: "mfa-reset", workflowName: "MFA reset", tenantId: "t1", tenantName: "Contoso Ltd",
    kind: "Remediate", operator: "jspillers", inputs: { userUpn: "ada@contoso.com" }, findings: [],
    steps: [{ name: "Revoke sessions", success: true }], succeeded: true, healthy: true,
    startedAt: "2026-09-01T10:00:00Z", durationMs: 1200
  },
  {
    id: "r2", workflowId: "mfa-reset", workflowName: "MFA reset", tenantId: "t1", tenantName: "Contoso Ltd",
    kind: "Diagnose", operator: "jspillers", inputs: { userUpn: "someone.else@contoso.com" }, findings: [],
    steps: [], succeeded: true, startedAt: "2026-09-01T09:00:00Z", durationMs: 300
  }
];

const ME: MeProfile = {
  id: "me1", email: "tech@example.com", displayName: "Tech", isSystemAdmin: true, totpEnabled: false,
  tenantAccess: [{ tenantId: "t1", tenantName: "Contoso Ltd", role: "Owner" }],
  instancePermissions: ["instance.sam.manage"]
};

const WORKSPACE: PersonWorkspace = {
  tenantId: "t1",
  userId: "u1",
  profile: {
    status: "Ok",
    data: { id: "u1", displayName: "Ada Lovelace", upn: "ada@contoso.com", accountEnabled: true, jobTitle: "Engineer" }
  },
  licenses: { status: "Unavailable", reason: "later" },
  groups: { status: "Unavailable", reason: "later" },
  authMethods: { status: "Unavailable", reason: "later" },
  mailbox: { status: "Unavailable", reason: "Exchange not configured" },
  devices: { status: "Unavailable", reason: "later" },
  recentRuns: { status: "Unavailable", reason: "later" }
};

let navigateRef: NavigateFunction | null = null;
function NavigatorCapture() {
  navigateRef = useNavigate();
  return null;
}

function renderApp(path: string) {
  return render(
    <ThemeProvider theme={theme}>
      <ToastProvider>
        <ConfirmDialogProvider>
          <MemoryRouter initialEntries={[path]}>
            <App />
            <NavigatorCapture />
            <LocationProbe />
          </MemoryRouter>
        </ConfirmDialogProvider>
      </ToastProvider>
    </ThemeProvider>
  );
}

const location = () => screen.getByTestId("location").textContent;
const pageHeading = (name: string | RegExp) => screen.findByRole("heading", { level: 2, name }, { timeout: 5000 });

describe("App routing", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    navigateRef = null;
    mockMatchMedia((q) => q.includes("min-width"));
    vi.mocked(api.auth.mode).mockResolvedValue({ mode: "Dev" });
    vi.mocked(api.system.status).mockResolvedValue({ profile: "Server", version: "0.9.0", authMode: "Dev", needsFirstUser: false });
    vi.mocked(api.system.diagnostics).mockResolvedValue({ checks: [], capabilities: { graph: true, exchange: true, partnerCenter: true } });
    vi.mocked(api.dashboard).mockResolvedValue({
      stats: { tenants: 1, tenantsNoDelegation: 0, deployments: 0, deploymentsFailed: 0, deploymentsUpdateAvailable: 0, runsLast24h: 0, runsFailedLast7d: 0 },
      needsAttention: [],
      recentRuns: []
    });
    vi.mocked(api.pendingActions.list).mockResolvedValue([]);
    vi.mocked(api.tenants.list).mockResolvedValue(TENANTS);
    vi.mocked(api.contracts.list).mockResolvedValue([]);
    vi.mocked(api.templates.list).mockResolvedValue([]);
    vi.mocked(api.deployments.list).mockResolvedValue([]);
    vi.mocked(api.workflows.list).mockResolvedValue(CATALOG);
    vi.mocked(api.workflows.runs).mockResolvedValue(RUNS);
    vi.mocked(api.workflows.evidence).mockRejectedValue(new ApiError(404, "404 Not Found: ", ""));
    vi.mocked(api.directory.users).mockResolvedValue([]);
    vi.mocked(api.people.get).mockResolvedValue(WORKSPACE);
    vi.mocked(api.configSnapshots.list).mockResolvedValue([]);
    vi.mocked(api.search.users).mockResolvedValue({
      hits: [{ tenantId: "t1", tenantName: "Contoso Ltd", id: "u1", displayName: "Ada Lovelace", userPrincipalName: "ada@contoso.com" }],
      errors: [],
      tenantsSearched: 1
    });
  });

  it.each([
    ["/", "Home"],
    ["/people", "People"],
    ["/tenants", "Tenants"],
    ["/operations", "Operations"],
    ["/activity", "Activity"],
    ["/settings", "Settings"]
  ])("renders the %s area", async (path, heading) => {
    renderApp(path);
    expect(await pageHeading(heading)).toBeInTheDocument();
    const nav = screen.getByRole("navigation", { name: "Main navigation" });
    expect(within(nav).getByRole("link", { name: new RegExp(`^${heading}`) })).toHaveAttribute("aria-current", "page");
  });

  it("marks the parent destination active on a nested route", async () => {
    renderApp("/operations/contracts");
    expect(await pageHeading("Contracts")).toBeInTheDocument();
    const nav = screen.getByRole("navigation", { name: "Main navigation" });
    expect(within(nav).getByRole("link", { name: "Operations" })).toHaveAttribute("aria-current", "page");
    expect(within(nav).getByRole("link", { name: "Home" })).not.toHaveAttribute("aria-current");
  });

  it("deep-links into a person workspace with the requested tab", async () => {
    renderApp("/people/t1/u1?tab=history");
    expect(await pageHeading("Ada Lovelace")).toBeInTheDocument();
    expect(api.people.get).toHaveBeenCalledWith("t1", "u1");
    expect(screen.getByRole("tab", { name: "History" })).toHaveAttribute("aria-selected", "true");
    // Only runs whose inputs name this person are shown.
    const table = await screen.findByRole("table", { name: "Runs for this person" });
    expect(within(table).getAllByRole("row")).toHaveLength(2);
    expect(within(table).getByRole("link", { name: "MFA reset" })).toHaveAttribute("href", "/activity/runs/r1");
  });

  it("shows the person summary and contextual actions pre-filled with tenant and user", async () => {
    renderApp("/people/t1/u1");
    expect(await pageHeading("Ada Lovelace")).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: "Summary" })).toHaveAttribute("aria-selected", "true");
    const actions = screen.getByRole("list", { name: "Actions for this person" });
    expect(within(actions).getByRole("link", { name: /Reset MFA/ }))
      .toHaveAttribute("href", "/operations/workflows/mfa-reset?tenant=t1&user=ada%40contoso.com");
    expect(within(actions).getByRole("link", { name: /Offboard/ }))
      .toHaveAttribute("href", "/operations/offboard?tenant=t1&user=ada%40contoso.com");
  });

  it("runs a ?q= people search and links each hit to its workspace", async () => {
    renderApp("/people?q=ada");
    const link = await screen.findByRole("link", { name: "Ada Lovelace" });
    expect(api.search.users).toHaveBeenCalledWith("ada");
    expect(link).toHaveAttribute("href", "/people/t1/u1");
    await userEvent.setup().click(link);
    expect(await pageHeading("Ada Lovelace")).toBeInTheDocument();
    expect(location()).toBe("/people/t1/u1");
  });

  it("selects the tenant workspace tab named in ?tab=", async () => {
    renderApp("/tenants/t1?tab=snapshots");
    expect(await pageHeading("Contoso Ltd")).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: "Snapshots" })).toHaveAttribute("aria-selected", "true");
    await waitFor(() => expect(screen.getByRole("heading", { name: "Config snapshots" })).toBeInTheDocument(), { timeout: 15000 });
    // Scoped to this tenant: no tenant picker, and the snapshots are this tenant's.
    expect(screen.queryByLabelText("Tenant")).not.toBeInTheDocument();
    await waitFor(() => expect(api.configSnapshots.list).toHaveBeenCalledWith("t1"));
  });

  it("switching tenant tabs updates the URL, and back/forward restores them", async () => {
    const user = userEvent.setup();
    renderApp("/tenants/t1");
    expect(await pageHeading("Contoso Ltd")).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: "Overview" })).toHaveAttribute("aria-selected", "true");

    await user.click(screen.getByRole("tab", { name: "History" }));
    expect(location()).toBe("/tenants/t1?tab=history");

    act(() => navigateRef!(-1));
    await waitFor(() => expect(location()).toBe("/tenants/t1"));
    expect(screen.getByRole("tab", { name: "Overview" })).toHaveAttribute("aria-selected", "true");

    act(() => navigateRef!(1));
    await waitFor(() => expect(location()).toBe("/tenants/t1?tab=history"));
    expect(screen.getByRole("tab", { name: "History" })).toHaveAttribute("aria-selected", "true");
  });

  it("back/forward moves between top-level areas", async () => {
    const user = userEvent.setup();
    renderApp("/");
    expect(await pageHeading("Home")).toBeInTheDocument();
    const nav = screen.getByRole("navigation", { name: "Main navigation" });
    await user.click(within(nav).getByRole("link", { name: "Operations" }));
    expect(await pageHeading("Operations")).toBeInTheDocument();

    act(() => navigateRef!(-1));
    expect(await pageHeading("Home")).toBeInTheDocument();
    act(() => navigateRef!(1));
    expect(await pageHeading("Operations")).toBeInTheDocument();
  });

  it("pre-fills the tenant and user on an operations workflow deep link", async () => {
    renderApp("/operations/workflows/mfa-reset?tenant=t1&user=ada%40contoso.com");
    expect(await screen.findByLabelText("User UPN", {}, { timeout: 5000 })).toHaveValue("ada@contoso.com");
    expect(screen.getByRole("button", { name: "MFA reset" })).toHaveClass("Mui-selected");
    expect(screen.getByRole("combobox", { name: "Tenant" })).toHaveTextContent("Contoso Ltd");
  });

  it("opens the command palette from anywhere in the shell and jumps to the chosen page", async () => {
    const user = userEvent.setup();
    renderApp("/");
    expect(await pageHeading("Home")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Search and jump (Ctrl+K)" })).toBeInTheDocument();
    await user.keyboard("{Control>}k{/Control}");
    const dialog = await screen.findByRole("dialog", { name: "Command palette" });
    await user.type(within(dialog).getByRole("combobox"), "mirror{ArrowDown}{Enter}");
    expect(await pageHeading("Mirror access")).toBeInTheDocument();
    expect(location()).toBe("/operations/access-parity");
  });

  it("shows a helpful not-found page for an unknown route", async () => {
    renderApp("/no/such/place");
    expect(await pageHeading("Page not found")).toBeInTheDocument();
    const list = screen.getByRole("list", { name: "Where to go instead" });
    expect(within(list).getAllByRole("link")).toHaveLength(6);
  });

  it("falls back to the run history at /activity/runs/:runId on a server without evidence", async () => {
    renderApp("/activity/runs/r1");
    expect(await pageHeading("MFA reset")).toBeInTheDocument();
    expect(api.workflows.evidence).toHaveBeenCalledWith("r1");
    expect(screen.getByText("Revoke sessions")).toBeInTheDocument();
  });

  it("shows a run's evidence at /activity/runs/:runId", async () => {
    vi.mocked(api.workflows.evidence).mockResolvedValue(makeEvidence({ runId: "r1", outcome: "PartiallySucceeded" }));
    renderApp("/activity/runs/r1");
    expect(await pageHeading("Access parity")).toBeInTheDocument();
    expect(screen.getByTestId("evidence-outcome")).toHaveTextContent("Partially succeeded");
    expect(screen.getByRole("button", { name: "Copy ticket notes" })).toBeInTheDocument();
    expect(api.workflows.runs).not.toHaveBeenCalledWith({ take: 200 });
  });

  it("lists runs with outcome chips, the person they targeted, and a link to each run", async () => {
    vi.mocked(api.workflows.runs).mockResolvedValue([
      { ...RUNS[0], outcome: "VerificationFailed", targetId: "u1", targetDisplayName: "Ada Lovelace" },
      { ...RUNS[1], outcome: "CompletedUnverified" },
      { ...RUNS[1], id: "r3", outcome: "SomethingNew" as never }
    ]);
    renderApp("/activity");
    const table = await screen.findByRole("table", { name: "Workflow runs" }, { timeout: 5000 });
    expect(within(table).getByText("Verification failed")).toBeInTheDocument();
    expect(within(table).getByText("Completed, not verified")).toBeInTheDocument();
    expect(within(table).getByText("SomethingNew")).toBeInTheDocument();
    expect(within(table).getByRole("link", { name: "Ada Lovelace" })).toHaveAttribute("href", "/people/t1/u1");
    expect(within(table).getAllByRole("link", { name: "MFA reset" })[0]).toHaveAttribute("href", "/activity/runs/r1");
  });

  it("keeps Activity filters in the URL", async () => {
    const user = userEvent.setup();
    renderApp("/activity?tenant=t1");
    expect(await pageHeading("Activity")).toBeInTheDocument();
    await waitFor(() => expect(api.workflows.runs).toHaveBeenCalledWith({ tenantId: "t1", take: 50 }));
    await user.click(screen.getByRole("button", { name: "Deployments" }));
    expect(location()).toBe("/activity?tenant=t1&kind=deployments");
  });

  it("explains that diagnostics are unavailable on an older server", async () => {
    vi.mocked(api.system.diagnostics).mockRejectedValue(new ApiError(404, "404 Not Found: "));
    renderApp("/settings/workbench");
    expect(await screen.findByText(/Diagnostics are unavailable on this server version/)).toBeInTheDocument();
  });

  it("renders diagnostics checks with their fixes as quest chips", async () => {
    vi.mocked(api.system.diagnostics).mockResolvedValue({
      checks: [
        { id: "database", label: "Database", status: "Ok", detail: "SQLite", fix: null },
        {
          id: "exchange-module", label: "Exchange Online module", status: "NotConfigured", detail: "pwsh found, module missing",
          fix: { label: "Install module", command: "pwsh -c \"Install-Module ExchangeOnlineManagement\"", route: null }
        },
        { id: "sam", label: "Microsoft connection", status: "Error", detail: "No refresh token", fix: { label: "Connect Microsoft", route: "/settings/microsoft" } }
      ],
      capabilities: { graph: true, exchange: false, partnerCenter: true }
    });
    renderApp("/settings/workbench");
    const list = await screen.findByRole("list", { name: "Diagnostics checks" });
    expect(within(list).getByText("Not configured")).toBeInTheDocument();
    expect(within(list).getByRole("button", { name: "Copy command: Install module" })).toBeInTheDocument();
    expect(within(list).getByRole("link", { name: "Connect Microsoft" })).toHaveAttribute("href", "/settings/microsoft");
  });

  it("shows a setup checklist on Home when checks still need attention", async () => {
    vi.mocked(api.system.diagnostics).mockResolvedValue({
      checks: [
        { id: "database", label: "Database", status: "Ok" },
        { id: "sam", label: "Microsoft connection", status: "NotConfigured", fix: { label: "Connect Microsoft", route: "/settings/microsoft" } }
      ],
      capabilities: { graph: false, exchange: false, partnerCenter: false }
    });
    renderApp("/");
    expect(await screen.findByRole("heading", { name: "Finish setting up" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Connect Microsoft" })).toBeInTheDocument();
    expect(screen.queryByText("Database")).not.toBeInTheDocument();
  });

  it("badges Activity with the pending-approvals count", async () => {
    vi.mocked(api.pendingActions.list).mockResolvedValue([
      { id: "p1", tenantId: "t1", tenantName: "Contoso Ltd", actionType: "workflow.remediate", previewSummary: "x", status: "Pending", createdAt: "", expiresAt: "", executionError: null }
    ]);
    renderApp("/");
    const nav = await screen.findByRole("navigation", { name: "Main navigation" });
    expect(await within(nav).findByRole("link", { name: /Activity.*1 pending approval/ })).toBeInTheDocument();
  });

  describe("Auth:Mode=Local", () => {
    beforeEach(() => {
      vi.mocked(api.auth.mode).mockResolvedValue({ mode: "Local" });
      vi.mocked(api.system.status).mockResolvedValue({ profile: "Local", version: "0.9.0", authMode: "Local", needsFirstUser: false });
    });

    it("sends an unauthenticated deep link to sign-in, then back to the requested page", async () => {
      vi.mocked(api.auth.login).mockResolvedValue({ accessToken: "tok", user: ME });
      const user = userEvent.setup();
      renderApp("/tenants/t1?tab=history");

      expect(await screen.findByRole("button", { name: "Sign in" })).toBeInTheDocument();
      expect(location()).toBe("/login");

      await user.type(screen.getByLabelText("Email"), "tech@example.com");
      await user.type(screen.getByLabelText("Password"), "correct-horse-battery");
      await user.click(screen.getByRole("button", { name: "Sign in" }));

      await waitFor(() => expect(location()).toBe("/tenants/t1?tab=history"));
      expect(await pageHeading("Contoso Ltd")).toBeInTheDocument();
      expect(screen.getByRole("tab", { name: "History" })).toHaveAttribute("aria-selected", "true");
    });

    it("routes a brand-new workbench to first-user setup", async () => {
      vi.mocked(api.system.status).mockResolvedValue({ profile: "Local", version: "0.9.0", authMode: "Local", needsFirstUser: true });
      renderApp("/");
      expect(await screen.findByRole("heading", { name: "Set up this workbench" })).toBeInTheDocument();
      expect(location()).toBe("/register");
    });

    it("explains a missing tenant role instead of showing a broken page", async () => {
      localStorage.setItem("pcb.local.accessToken", "tok");
      vi.mocked(api.auth.me).mockResolvedValue({ ...ME, tenantAccess: [{ tenantId: "t1", tenantName: "Contoso Ltd", role: "Viewer" }] });
      renderApp("/tenants/t1?tab=access");
      expect(await screen.findByText("Only an Owner can manage access")).toBeInTheDocument();
      expect(screen.getByText(/Your role on Contoso Ltd is Viewer/)).toBeInTheDocument();
    });

    it("explains a missing instance permission on Microsoft settings", async () => {
      localStorage.setItem("pcb.local.accessToken", "tok");
      vi.mocked(api.auth.me).mockResolvedValue({ ...ME, instancePermissions: [] });
      renderApp("/settings/microsoft");
      expect(await screen.findByText("Needs the SAM credentials role")).toBeInTheDocument();
      expect(api.sam.status).not.toHaveBeenCalled();
    });

    it("tells a first administrator how to get tenants, and anyone else whom to ask", async () => {
      localStorage.setItem("pcb.local.accessToken", "tok");
      vi.mocked(api.auth.me).mockResolvedValue({ ...ME, tenantAccess: [], instancePermissions: ["instance.tenant-registry.manage"] });
      const first = renderApp("/");
      expect(await screen.findByText(/No tenants in your workbench yet/)).toBeInTheDocument();
      expect(screen.queryByText(/Ask a tenant Owner/)).not.toBeInTheDocument();
      first.unmount();

      vi.mocked(api.auth.me).mockResolvedValue({ ...ME, tenantAccess: [], instancePermissions: [] });
      renderApp("/");
      expect(await screen.findByText(/Ask a tenant Owner/)).toBeInTheDocument();
    });
  });

  describe("Local Workbench without an account", () => {
    const OWNER: MeProfile = {
      ...ME, email: "owner@workbench.local", displayName: "maya (this computer)", isWorkbenchOwner: true, tenantAccess: []
    };

    beforeEach(() => {
      vi.mocked(api.auth.mode).mockResolvedValue({ mode: "Local" });
      vi.mocked(api.system.status).mockResolvedValue({
        profile: "Local", version: "0.9.0", authMode: "Local", needsFirstUser: false, accountless: true, windowsUser: "maya"
      });
      window.history.replaceState(null, "", "/");
    });

    it("offers skipping the account on first run, only after a confirmation, with the setup ticket", async () => {
      window.history.replaceState(null, "", "/#ticket=setup-tkt");
      vi.mocked(api.system.status).mockResolvedValue({
        profile: "Local", version: "0.9.0", authMode: "Local", needsFirstUser: true, canSkipAccount: true, windowsUser: "maya",
        setupTicketRequired: true
      });
      vi.mocked(api.auth.setupNoAccount).mockResolvedValue({ accessToken: "owner-tok", user: OWNER });
      const user = userEvent.setup();
      renderApp("/");

      expect(await screen.findByRole("button", { name: "Create administrator account" })).toBeInTheDocument();
      await user.click(screen.getByRole("button", { name: "Skip -- use without an account on this computer" }));
      const dialog = await screen.findByRole("dialog");
      expect(within(dialog).getByText(/Anyone who can run programs as maya on this computer can use/)).toBeInTheDocument();
      expect(window.location.hash).toBe("");
      expect(within(dialog).getByText(/You can add a password later in Settings/)).toBeInTheDocument();

      await user.click(within(dialog).getByRole("button", { name: "Cancel" }));
      await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
      expect(api.auth.setupNoAccount).not.toHaveBeenCalled();

      await user.click(screen.getByRole("button", { name: "Skip -- use without an account on this computer" }));
      await user.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Use without an account" }));
      await waitFor(() => expect(api.auth.setupNoAccount).toHaveBeenCalledTimes(1));
      expect(api.auth.setupNoAccount).toHaveBeenCalledWith("setup-tkt");
      await waitFor(() => expect(location()).toBe("/"));
      expect(localStorage.getItem("pcb.local.accessToken")).toBe("owner-tok");
    });

    it("shows guidance instead of the first-run form without the exe's setup link", async () => {
      vi.mocked(api.system.status).mockResolvedValue({
        profile: "Local", version: "0.9.0", authMode: "Local", needsFirstUser: true, canSkipAccount: true, windowsUser: "maya",
        setupTicketRequired: true
      });
      renderApp("/");
      expect(await screen.findByText("Open Partner Center Bridge from PartnerCenterBridge.exe to finish setup.")).toBeInTheDocument();
      expect(location()).toBe("/register");
      expect(screen.queryByLabelText("Password (12+ characters)")).not.toBeInTheDocument();
      expect(screen.queryByRole("button", { name: /Skip/ })).not.toBeInTheDocument();
    });

    it("sends the setup ticket with the first account, and never tries it as a sign-in", async () => {
      window.history.replaceState(null, "", "/#ticket=setup-tkt");
      vi.mocked(api.system.status).mockResolvedValue({
        profile: "Local", version: "0.9.0", authMode: "Local", needsFirstUser: true, setupTicketRequired: true
      });
      vi.mocked(api.auth.register).mockResolvedValue({ accessToken: "admin-tok", user: ME });
      const user = userEvent.setup();
      renderApp("/");

      await user.type(await screen.findByLabelText("Display name"), "Maya");
      expect(window.location.hash).toBe("");
      await user.type(screen.getByLabelText("Email"), "maya@contoso.com");
      await user.type(screen.getByLabelText("Password (12+ characters)"), "correct-horse-battery");
      await user.click(screen.getByRole("button", { name: "Create administrator account" }));
      await waitFor(() => expect(api.auth.register).toHaveBeenCalledWith("maya@contoso.com", "correct-horse-battery", "Maya", "setup-tkt"));
      expect(api.auth.launch).not.toHaveBeenCalled();
      await waitFor(() => expect(location()).toBe("/"));
    });

    it("ignores a leftover ticket once the workbench has accounts", async () => {
      window.history.replaceState(null, "", "/#ticket=old");
      vi.mocked(api.system.status).mockResolvedValue({ profile: "Local", version: "0.9.0", authMode: "Local", needsFirstUser: false });
      renderApp("/");
      expect(await screen.findByRole("button", { name: "Sign in" })).toBeInTheDocument();
      expect(window.location.hash).toBe("");
      expect(api.auth.launch).not.toHaveBeenCalled();
    });

    it("hides the skip option when the server does not allow it", async () => {
      vi.mocked(api.system.status).mockResolvedValue({ profile: "Local", version: "0.9.0", authMode: "Local", needsFirstUser: true });
      renderApp("/");
      expect(await screen.findByRole("heading", { name: "Set up this workbench" })).toBeInTheDocument();
      expect(screen.queryByRole("button", { name: /Skip/ })).not.toBeInTheDocument();
    });

    it("takes the sign-in ticket out of the URL and signs in on the requested page", async () => {
      window.history.replaceState(null, "", "/tenants?x=1#ticket=t1ck3t");
      vi.mocked(api.auth.launch).mockResolvedValue({ accessToken: "launch-tok", user: { ...OWNER, tenantAccess: ME.tenantAccess } });
      renderApp("/tenants?x=1");

      expect(window.location.hash).toBe("");
      expect(window.location.pathname + window.location.search).toBe("/tenants?x=1");
      await waitFor(() => expect(api.auth.launch).toHaveBeenCalledWith("t1ck3t"));
      expect(await screen.findByText("Contoso Ltd")).toBeInTheDocument();
      expect(location()).toBe("/tenants?x=1");
      expect(localStorage.getItem("pcb.local.accessToken")).toBe("launch-tok");
    });

    it("shows guidance, not a password form, without a session", async () => {
      renderApp("/tenants");
      expect(await screen.findByText(/This workbench has no account. Open it from PartnerCenterBridge.exe/)).toBeInTheDocument();
      expect(screen.queryByLabelText("Password")).not.toBeInTheDocument();
      expect(screen.queryByRole("button", { name: "Sign in" })).not.toBeInTheDocument();
    });

    it("reports a stale launch link on the guidance page", async () => {
      window.history.replaceState(null, "", "/#ticket=old");
      vi.mocked(api.auth.launch).mockRejectedValue(new ApiError(401, "401 Unauthorized: stale", "This sign-in link is not valid."));
      renderApp("/");
      expect(await screen.findByText("This sign-in link is not valid.")).toBeInTheDocument();
      expect(screen.getByText(/This workbench has no account/)).toBeInTheDocument();
    });

    it("signing out shows the same guidance", async () => {
      localStorage.setItem("pcb.local.accessToken", "tok");
      vi.mocked(api.auth.me).mockResolvedValue(OWNER);
      vi.mocked(api.auth.logout).mockResolvedValue(undefined);
      const user = userEvent.setup();
      renderApp("/settings");
      await user.click(await screen.findByRole("button", { name: "Account menu" }));
      await user.click(await screen.findByRole("menuitem", { name: "Sign out" }));
      expect(await screen.findByRole("heading", { name: "Signed out" })).toBeInTheDocument();
      expect(screen.getByText(/This workbench has no account. Open it from PartnerCenterBridge.exe/)).toBeInTheDocument();
      expect(screen.queryByLabelText("Password")).not.toBeInTheDocument();
    });
  });
});
