import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ThemeProvider } from "@mui/material/styles";
import { theme } from "../theme";
import { ToastProvider } from "../hooks/useToast";
import { ConfigSnapshots } from "./ConfigSnapshots";
import type { ConfigSnapshotRun, MeProfile, SectionDiff, Tenant } from "../types";

vi.mock("../api", () => ({
  api: {
    tenants: { list: vi.fn() },
    configSnapshots: {
      list: vi.fn(),
      capture: vi.fn(),
      diff: vi.fn(),
      exportDiff: vi.fn(),
      exportRun: vi.fn(),
      import: vi.fn()
    }
  }
}));

import { api } from "../api";

const tenant: Tenant = { id: "t1", tenantId: "aad-1", displayName: "Contoso Ltd", status: "Active" as Tenant["status"] };

const run1: ConfigSnapshotRun = {
  id: "r1",
  tenantId: "t1",
  operator: "jspillers",
  startedAt: "2026-08-19T09:00:00Z",
  succeeded: true,
  imported: false,
  gitCommitSha: "abcdef1234567890",
  sections: [{ sectionId: "ca", sectionName: "Conditional Access", itemCount: 3, failed: false }]
};

const run2: ConfigSnapshotRun = {
  id: "r2",
  tenantId: "t1",
  operator: "jspillers",
  startedAt: "2026-08-19T10:00:00Z",
  succeeded: false,
  imported: false,
  sections: [{ sectionId: "ca", sectionName: "Conditional Access", itemCount: 0, failed: true, error: "graph timeout" }]
};

const me: MeProfile = {
  id: "u1",
  email: "j@example.com",
  displayName: "Joey",
  isSystemAdmin: false,
  totpEnabled: false,
  tenantAccess: [{ tenantId: "t1", tenantName: "Contoso Ltd", role: "Operator" }]
};

function renderComponent(meProfile: MeProfile | null = me) {
  return render(
    <ThemeProvider theme={theme}>
      <ToastProvider>
        <ConfigSnapshots me={meProfile} />
      </ToastProvider>
    </ThemeProvider>
  );
}

async function selectMuiOptionByIndex(user: ReturnType<typeof userEvent.setup>, comboboxName: string, index: number) {
  await user.click(screen.getByRole("combobox", { name: comboboxName }));
  const options = await screen.findAllByRole("option");
  await user.click(options[index]);
}

describe("ConfigSnapshots", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.tenants.list).mockResolvedValue([tenant]);
    vi.mocked(api.configSnapshots.list).mockResolvedValue([run1, run2]);
  });

  it("loads tenants and runs, and renders the runs table", async () => {
    renderComponent();

    expect(await screen.findByText(/Conditional Access \(3\)/, {}, { timeout: 5000 })).toBeInTheDocument();
    expect(screen.getByText("ok")).toBeInTheDocument();
    expect(screen.getByText("partial failure")).toBeInTheDocument();
    expect(screen.getByText("abcdef12")).toBeInTheDocument();
    expect(api.tenants.list).toHaveBeenCalledTimes(1);
    expect(api.configSnapshots.list).toHaveBeenCalledWith("t1");
  });

  it("shows the empty state message when a tenant has no runs", async () => {
    vi.mocked(api.configSnapshots.list).mockResolvedValue([]);
    renderComponent();

    expect(await screen.findByText("No snapshots yet for this tenant.")).toBeInTheDocument();
  });

  it("captures a snapshot, reloads the runs list, and shows a success toast", async () => {
    vi.mocked(api.configSnapshots.capture).mockResolvedValue(run1);
    const user = userEvent.setup();
    renderComponent();

    await screen.findByText(/Conditional Access \(3\)/);
    vi.mocked(api.configSnapshots.list).mockResolvedValue([run1, run2, run1]);

    await user.click(screen.getByRole("button", { name: "Take Snapshot" }));

    await waitFor(() => expect(api.configSnapshots.capture).toHaveBeenCalledWith("t1"));
    expect(api.configSnapshots.list).toHaveBeenCalledTimes(2);
    expect(await screen.findByText("Snapshot captured")).toBeInTheDocument();
  });

  it("views a diff between two runs and renders field changes", async () => {
    const diffs: SectionDiff[] = [
      {
        sectionId: "ca",
        sectionName: "Conditional Access",
        changes: [
          {
            kind: "Modified",
            itemId: "policy-1",
            label: "Require MFA for admins",
            fieldChanges: [{ field: "state", before: "enabledForReportingButNotEnforced", after: "enabled" }]
          }
        ]
      }
    ];
    vi.mocked(api.configSnapshots.diff).mockResolvedValue(diffs);
    const user = userEvent.setup();
    renderComponent();

    await screen.findByText(/Conditional Access \(3\)/);

    await selectMuiOptionByIndex(user, "Before", 1);
    await selectMuiOptionByIndex(user, "After", 2);

    await user.click(screen.getByRole("button", { name: "View diff" }));

    await waitFor(() => expect(api.configSnapshots.diff).toHaveBeenCalledWith("t1", "r1", "r2"));
    expect(await screen.findByText("Modified")).toBeInTheDocument();
    expect(screen.getByText("Require MFA for admins")).toBeInTheDocument();
    expect(screen.getByText(/state: enabledForReportingButNotEnforced -> enabled/)).toBeInTheDocument();
  });

  it("shows the bare error message on a failed action (not the String(e)-wrapped 'Error: ...' form)", async () => {
    vi.mocked(api.configSnapshots.capture).mockRejectedValue(new Error("500 Internal Server Error"));
    const user = userEvent.setup();
    renderComponent();

    await screen.findByText(/Conditional Access \(3\)/);
    await user.click(screen.getByRole("button", { name: "Take Snapshot" }));

    expect(await screen.findByText("500 Internal Server Error")).toBeInTheDocument();
    expect(screen.queryByText("Error: 500 Internal Server Error")).not.toBeInTheDocument();
  });

  it("clears a stale error from an earlier failed action once a later, unrelated action succeeds", async () => {
    vi.mocked(api.configSnapshots.diff).mockRejectedValue(new Error("diff endpoint down"));
    vi.mocked(api.configSnapshots.capture).mockResolvedValue(run1);
    const user = userEvent.setup();
    renderComponent();

    await screen.findByText(/Conditional Access \(3\)/);

    // Fail the diff action first -- its error should show.
    await selectMuiOptionByIndex(user, "Before", 1);
    await selectMuiOptionByIndex(user, "After", 2);
    await user.click(screen.getByRole("button", { name: "View diff" }));
    expect(await screen.findByText("diff endpoint down")).toBeInTheDocument();

    // A later, unrelated action (capture) succeeds -- the stale diff error must not linger.
    vi.mocked(api.configSnapshots.list).mockResolvedValue([run1, run2, run1]);
    await user.click(screen.getByRole("button", { name: "Take Snapshot" }));

    expect(await screen.findByText("Snapshot captured")).toBeInTheDocument();
    expect(screen.queryByText("diff endpoint down")).not.toBeInTheDocument();
  });

  it("imports a workbook, reloads the runs list, and shows a success toast", async () => {
    vi.mocked(api.configSnapshots.import).mockResolvedValue(run1);
    const user = userEvent.setup();
    renderComponent();

    await screen.findByText(/Conditional Access \(3\)/);
    vi.mocked(api.configSnapshots.list).mockResolvedValue([run1, run2, run1]);

    const sections = [{ sectionId: "ca", sectionName: "Conditional Access", contentJson: "{}" }];
    const file = new File([JSON.stringify({ sections })], "workbook.json", { type: "application/json" });
    await user.upload(screen.getByLabelText("Workbook file"), file);

    await user.click(screen.getByRole("button", { name: "Import" }));

    await waitFor(() => expect(api.configSnapshots.import).toHaveBeenCalledWith("t1", sections));
    expect(api.configSnapshots.list).toHaveBeenCalledTimes(2);
    expect(await screen.findByText("Workbook imported")).toBeInTheDocument();
  });

  it("clears run selections and diffs when the tenant changes, instead of pairing them with the new tenant", async () => {
    const tenant2: Tenant = { id: "t2", tenantId: "aad-2", displayName: "Fabrikam", status: "Active" as Tenant["status"] };
    vi.mocked(api.tenants.list).mockResolvedValue([tenant, tenant2]);
    const diffs: SectionDiff[] = [
      { sectionId: "ca", sectionName: "Conditional Access", changes: [{ kind: "Modified", itemId: "policy-1", label: "Require MFA", fieldChanges: [] }] }
    ];
    vi.mocked(api.configSnapshots.diff).mockResolvedValue(diffs);
    vi.mocked(api.configSnapshots.list).mockImplementation((id: string) => Promise.resolve(id === "t1" ? [run1, run2] : []));
    const user = userEvent.setup();
    renderComponent();

    await screen.findByText(/Conditional Access \(3\)/);
    await selectMuiOptionByIndex(user, "Before", 1);
    await selectMuiOptionByIndex(user, "After", 2);
    await user.click(screen.getByRole("button", { name: "View diff" }));
    expect(await screen.findByText("Require MFA")).toBeInTheDocument();

    await selectMuiOptionByIndex(user, "Tenant", 1); // switch to Fabrikam
    await waitFor(() => expect(api.configSnapshots.list).toHaveBeenCalledWith("t2"));

    expect(screen.queryByText("Require MFA")).not.toBeInTheDocument();
    // The old run IDs must no longer be usable -- both actions require a selected Before and
    // After, so a cleared selection re-disables them (the actual behavior that matters here; the
    // combobox's own rendered text for an empty MUI Select value is a separate, unrelated quirk).
    expect(screen.getByRole("button", { name: "View diff" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Export as patch" })).toBeDisabled();
  });

  it("shows a warning (not a plain success) when the snapshot captures but the list fails to refresh", async () => {
    vi.mocked(api.configSnapshots.capture).mockResolvedValue(run1);
    const user = userEvent.setup();
    renderComponent();

    await screen.findByText(/Conditional Access \(3\)/);
    vi.mocked(api.configSnapshots.list).mockRejectedValueOnce(new Error("refresh failed"));

    await user.click(screen.getByRole("button", { name: "Take Snapshot" }));

    await waitFor(() => expect(api.configSnapshots.capture).toHaveBeenCalledWith("t1"));
    expect(await screen.findByText(/Snapshot captured, but the list couldn't refresh/)).toBeInTheDocument();
    expect(screen.queryByText("Snapshot captured")).not.toBeInTheDocument();
  });

  it("hides Take Snapshot and the import panel for a Viewer", async () => {
    const viewerMe: MeProfile = {
      ...me,
      tenantAccess: [{ tenantId: "t1", tenantName: "Contoso Ltd", role: "Viewer" }]
    };
    renderComponent(viewerMe);

    await screen.findByText(/Conditional Access \(3\)/);

    expect(screen.queryByRole("button", { name: "Take Snapshot" })).not.toBeInTheDocument();
    expect(screen.queryByText("Import a workbook")).not.toBeInTheDocument();
  });
});
