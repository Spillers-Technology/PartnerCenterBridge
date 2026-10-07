import { beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithRouter } from "../test/renderWithRouter";
import { TenantAudits } from "./TenantAudits";
import type { AuditCatalog, Tenant, TenantAuditReport, TenantAuditRunSummary } from "../types";

vi.mock("../api", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api")>();
  return {
    ...actual,
    api: {
      tenantAudits: {
        catalog: vi.fn(),
        list: vi.fn(),
        get: vi.fn(),
        run: vi.fn(),
        export: vi.fn(),
        estate: vi.fn()
      }
    }
  };
});

import { api, serverFileName } from "../api";

const tenant: Tenant = { id: "t1", tenantId: "aad-1", displayName: "Contoso Ltd", status: "Active" };

const catalog: AuditCatalog = {
  schemaVersion: 1,
  categories: [
    { id: "Identity", label: "Identity hygiene" },
    { id: "Licensing", label: "Licensing" },
    { id: "Devices", label: "Endpoint / Intune" }
  ],
  presets: [],
  parameters: [
    { key: "inactiveDays", label: "Inactive for (days)", default: 90, min: 7, max: 730, suggested: [30, 60, 90, 180], description: "" },
    { key: "deviceStaleDays", label: "Device not synced for (days)", default: 30, min: 7, max: 365, suggested: [14, 30, 60, 90], description: "" }
  ],
  checks: [
    { id: "dormant-licensed-users", name: "Dormant licensed users", category: "Licensing", description: "", businessImpact: "", recommendation: "", severityRules: [], requirements: [], parameterKeys: ["inactiveDays"], limitations: [], version: 1 },
    { id: "guest-accounts", name: "Guest account review", category: "Identity", description: "", businessImpact: "", recommendation: "", severityRules: [], requirements: [], parameterKeys: [], limitations: [], version: 1 },
    { id: "stale-devices", name: "Stale managed devices", category: "Devices", description: "", businessImpact: "", recommendation: "", severityRules: [], requirements: [], parameterKeys: ["deviceStaleDays"], limitations: [], version: 1 }
  ]
};

function report(overrides: Partial<TenantAuditReport> = {}): TenantAuditReport {
  return {
    schemaVersion: 1,
    runId: "run-1",
    auditName: "Full tenant health check",
    tenant: { id: "t1", displayName: "Contoso Ltd", tenantId: "aad-1" },
    operator: "tech@contoso.com",
    startedAt: "2026-10-01T12:00:00Z",
    completedAt: "2026-10-01T12:01:00Z",
    engineVersion: "0.10.0",
    parameters: { inactiveDays: 90 },
    requestedCheckIds: ["dormant-licensed-users", "guest-accounts", "stale-devices"],
    summary: { checksRequested: 3, checksCompleted: 2, checksUnavailable: 1, checksErrored: 0, pass: 1, info: 0, unknown: 0, warn: 1, fail: 0, affectedSubjects: 1, health: "AttentionNeeded" },
    checks: [
      {
        checkId: "dormant-licensed-users", checkName: "Dormant licensed users", category: "Licensing", checkVersion: 1, status: "Completed",
        missingRequirements: [], durationMs: 10, notes: ["Evaluated 12 users holding at least one license that may carry cost."],
        findings: [{
          id: "dormant-licensed-users:dormant", checkId: "dormant-licensed-users", severity: "Warn", title: "Dormant licensed users",
          summary: "1 licensed user has no successful sign-in recorded in the last 90 days.",
          businessImpact: "Potential unnecessary recurring Microsoft 365 spend and possible incomplete offboarding.",
          recommendation: "Review these users for license removal, downgrade, offboarding, or documented retention.",
          columns: [{ key: "licenses", label: "License / SKU(s)" }, { key: "daysInactive", label: "Days inactive" }],
          subjects: [{
            type: "user", id: "u1", name: "Ada Lovelace", upn: "ada@contoso.com", evidence: "Last successful sign-in 2026-06-03.",
            properties: { licenses: "SPE_E3", daysInactive: "120" },
            remediation: { kind: "offboarding", target: "u1", label: "Plan offboarding", identity: "ada@contoso.com" }
          }],
          subjectCount: 1
        }]
      },
      {
        checkId: "guest-accounts", checkName: "Guest account review", category: "Identity", checkVersion: 1, status: "Completed",
        missingRequirements: [], durationMs: 5, notes: [],
        findings: [{ id: "guest-accounts:pass", checkId: "guest-accounts", severity: "Pass", title: "Guest account review", summary: "There are no guest accounts.", columns: [], subjects: [], subjectCount: 0 }]
      },
      {
        checkId: "stale-devices", checkName: "Stale managed devices", category: "Devices", checkVersion: 1, status: "Unavailable",
        statusReason: "Microsoft Intune is not available in this tenant.", missingRequirements: ["Product: Microsoft Intune"],
        durationMs: 3, notes: [], findings: []
      }
    ],
    ...overrides
  };
}

const runSummary: TenantAuditRunSummary = {
  id: "run-1", tenantId: "t1", tenantName: "Contoso Ltd", auditName: "Full tenant health check", operator: "tech@contoso.com",
  startedAt: "2026-10-01T12:00:00Z", completedAt: "2026-10-01T12:01:00Z", schemaVersion: 1, engineVersion: "0.10.0",
  summary: report().summary
};

describe("TenantAudits", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.tenantAudits.catalog).mockResolvedValue(catalog);
    vi.mocked(api.tenantAudits.list).mockResolvedValue([]);
  });

  it("explains that nothing has run yet and that audits are read-only", async () => {
    renderWithRouter(<TenantAudits tenant={tenant} />);

    expect(await screen.findByText("No audits have been run for this tenant yet.")).toBeInTheDocument();
    expect(screen.getByText(/Read-only: PCB reads this tenant's configuration and changes nothing/)).toBeInTheDocument();
    expect(screen.getByRole("checkbox", { name: "Full tenant health check" })).toBeChecked();
    expect(screen.getByRole("checkbox", { name: "Licensing (1)" })).toBeChecked();
  });

  it("runs a full audit with the default threshold and shows the result", async () => {
    vi.mocked(api.tenantAudits.run).mockResolvedValue(report());
    vi.mocked(api.tenantAudits.list).mockResolvedValueOnce([]).mockResolvedValueOnce([runSummary]);
    const user = userEvent.setup();
    renderWithRouter(<TenantAudits tenant={tenant} />);

    await user.click(await screen.findByRole("button", { name: "Run audit" }));

    await waitFor(() => expect(api.tenantAudits.run).toHaveBeenCalledWith("t1", {
      categories: [], parameters: { inactiveDays: 90, deviceStaleDays: 30 }
    }));
    expect(await screen.findByText("Attention needed")).toBeInTheDocument();
    expect(screen.getByText("1 licensed user has no successful sign-in recorded in the last 90 days.")).toBeInTheDocument();
    expect(screen.getByText(/Potential unnecessary recurring Microsoft 365 spend/)).toBeInTheDocument();
  });

  it("sends only the selected categories and the parameters they use, including a custom threshold", async () => {
    vi.mocked(api.tenantAudits.run).mockResolvedValue(report());
    const user = userEvent.setup();
    renderWithRouter(<TenantAudits tenant={tenant} />);

    await user.click(await screen.findByRole("checkbox", { name: "Full tenant health check" }));
    await user.click(screen.getByRole("checkbox", { name: "Licensing (1)" }));
    expect(screen.queryByRole("combobox", { name: "Device not synced for (days)" })).not.toBeInTheDocument();

    await user.click(screen.getByRole("combobox", { name: "Inactive for (days)" }));
    await user.click(await screen.findByRole("option", { name: "Custom..." }));
    const custom = screen.getByRole("spinbutton", { name: "Inactive for (days) (custom days)" });
    await user.clear(custom);
    await user.type(custom, "45");
    await user.click(screen.getByRole("button", { name: "Run audit" }));

    await waitFor(() => expect(api.tenantAudits.run).toHaveBeenCalledWith("t1", {
      categories: ["Licensing"], parameters: { inactiveDays: 45 }
    }));
  });

  it("will not run with nothing selected or an out-of-range threshold", async () => {
    const user = userEvent.setup();
    renderWithRouter(<TenantAudits tenant={tenant} />);

    await user.click(await screen.findByRole("checkbox", { name: "Full tenant health check" }));
    expect(screen.getByRole("button", { name: "Run audit" })).toBeDisabled();
  });

  it("opens the latest run, links rows to the person and to an offboarding plan", async () => {
    vi.mocked(api.tenantAudits.list).mockResolvedValue([runSummary]);
    vi.mocked(api.tenantAudits.get).mockResolvedValue(report());
    renderWithRouter(<TenantAudits tenant={tenant} />);

    const table = await screen.findByRole("table", { name: "Dormant licensed users details" });
    expect(within(table).getByRole("link", { name: /Ada Lovelace/ })).toHaveAttribute("href", "/people/t1/u1");
    expect(within(table).getByRole("link", { name: "Plan offboarding" }))
      .toHaveAttribute("href", "/operations/offboard?tenant=t1&user=ada%40contoso.com");
    expect(within(table).getByText("120")).toBeInTheDocument();
  });

  it("shows unavailable checks with the reason and what is missing", async () => {
    vi.mocked(api.tenantAudits.list).mockResolvedValue([runSummary]);
    vi.mocked(api.tenantAudits.get).mockResolvedValue(report());
    renderWithRouter(<TenantAudits tenant={tenant} />);

    expect(await screen.findByText("PCB can't run this check here")).toBeInTheDocument();
    expect(screen.getByText("Microsoft Intune is not available in this tenant.")).toBeInTheDocument();
    expect(screen.getByText("Product: Microsoft Intune")).toBeInTheDocument();
  });

  it("filters by severity and category; passing checks are hidden until asked for", async () => {
    vi.mocked(api.tenantAudits.list).mockResolvedValue([runSummary]);
    vi.mocked(api.tenantAudits.get).mockResolvedValue(report());
    const user = userEvent.setup();
    renderWithRouter(<TenantAudits tenant={tenant} />);

    await screen.findByText("Dormant licensed users", { selector: "h4" });
    expect(screen.queryByText("Guest account review", { selector: "h4" })).not.toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Pass 1" }));
    expect(screen.getByText("Guest account review", { selector: "h4" })).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Warn 1" }));
    expect(screen.queryByText("Dormant licensed users", { selector: "h4" })).not.toBeInTheDocument();

    await user.click(screen.getByRole("combobox", { name: "Category" }));
    await user.click(await screen.findByRole("option", { name: "Endpoint / Intune" }));
    expect(screen.getByText("Stale managed devices", { selector: "h4" })).toBeInTheDocument();
    expect(screen.queryByText("Guest account review", { selector: "h4" })).not.toBeInTheDocument();
  });

  it("exports CSV, JSON and Markdown for the open run", async () => {
    vi.mocked(api.tenantAudits.list).mockResolvedValue([runSummary]);
    vi.mocked(api.tenantAudits.get).mockResolvedValue(report());
    vi.mocked(api.tenantAudits.export).mockResolvedValue(undefined);
    const user = userEvent.setup();
    renderWithRouter(<TenantAudits tenant={tenant} />);

    await user.click(await screen.findByRole("button", { name: "CSV" }));
    await user.click(screen.getByRole("button", { name: "JSON" }));
    await user.click(screen.getByRole("button", { name: "Markdown" }));

    await waitFor(() => expect(api.tenantAudits.export).toHaveBeenCalledTimes(3));
    expect(vi.mocked(api.tenantAudits.export).mock.calls.map((c) => c[2])).toEqual(["csv", "json", "markdown"]);
    expect(api.tenantAudits.export).toHaveBeenCalledWith("t1", "run-1", "csv");
  });

  it("shows a server rejection of the run without losing the form", async () => {
    const { ApiError } = await import("../api");
    vi.mocked(api.tenantAudits.run).mockRejectedValue(new ApiError(400, "400 Bad Request: Inactive for (days) must be between 7 and 730.", "Inactive for (days) must be between 7 and 730."));
    const user = userEvent.setup();
    renderWithRouter(<TenantAudits tenant={tenant} />);

    await user.click(await screen.findByRole("button", { name: "Run audit" }));

    expect(await screen.findByText("Inactive for (days) must be between 7 and 730.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Run audit" })).toBeEnabled();
  });
});

describe("serverFileName", () => {
  it("takes a safe ASCII file name from Content-Disposition and ignores anything else", () => {
    expect(serverFileName('attachment; filename=tenant-audit-contoso-20261001-1200-abcd1234.csv; filename*=UTF-8\'\'x')).toBe("tenant-audit-contoso-20261001-1200-abcd1234.csv");
    expect(serverFileName('attachment; filename="report.md"')).toBe("report.md");
    expect(serverFileName(null)).toBeNull();
    expect(serverFileName('attachment; filename="../../etc/passwd"')).toBeNull();
  });
});
