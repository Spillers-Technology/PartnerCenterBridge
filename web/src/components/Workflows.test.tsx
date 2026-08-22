import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ThemeProvider } from "@mui/material/styles";
import { theme } from "../theme";
import { ConfirmDialogProvider } from "../hooks/useConfirm";
import { ToastProvider } from "../hooks/useToast";
import { Workflows } from "./Workflows";
import type { Tenant, WorkflowSummary } from "../types";

vi.mock("../api", () => ({
  api: {
    workflows: { list: vi.fn(), runs: vi.fn(), diagnose: vi.fn(), remediate: vi.fn() },
    tenants: { list: vi.fn() }
  }
}));

import { api } from "../api";

const CATALOG: WorkflowSummary[] = [
  {
    id: "mfa-reset",
    name: "MFA reset",
    description: "Clears MFA methods for a user.",
    category: "Identity",
    inputs: [{ key: "userUpn", label: "User UPN", required: true, type: "text" }]
  }
];

const TENANTS: Tenant[] = [
  { id: "t1", tenantId: "aaaa", displayName: "Contoso Ltd", status: "Active" }
];

function renderWorkflows() {
  return render(
    <ThemeProvider theme={theme}>
      <ToastProvider>
        <ConfirmDialogProvider>
          <Workflows />
        </ConfirmDialogProvider>
      </ToastProvider>
    </ThemeProvider>
  );
}

async function pickWorkflowAndFillForm(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByRole("button", { name: "MFA reset" }));
  await user.click(screen.getByLabelText("Tenant"));
  await user.click(await screen.findByRole("option", { name: "Contoso Ltd" }));
  await user.type(screen.getByLabelText("User UPN"), "ada@contoso.com");
}

describe("Workflows", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.workflows.list).mockResolvedValue(CATALOG);
    vi.mocked(api.tenants.list).mockResolvedValue(TENANTS);
    vi.mocked(api.workflows.runs).mockResolvedValue([]);
  });

  it("diagnoses a workflow and shows findings", async () => {
    vi.mocked(api.workflows.diagnose).mockResolvedValue({
      findings: [{ name: "MFA methods", status: "Ok", detail: "none registered" }],
      healthy: true
    });
    const user = userEvent.setup();
    renderWorkflows();

    await pickWorkflowAndFillForm(user);
    await user.click(screen.getByRole("button", { name: "Diagnose" }));

    expect(await screen.findByText("MFA methods")).toBeInTheDocument();
    expect(screen.getByText("healthy")).toBeInTheDocument();
    expect(api.workflows.diagnose).toHaveBeenCalledWith("mfa-reset", "t1", { userUpn: "ada@contoso.com" });
  });

  it("shows an error alert when diagnose fails", async () => {
    vi.mocked(api.workflows.diagnose).mockRejectedValue(new Error("502 Bad Gateway"));
    const user = userEvent.setup();
    renderWorkflows();

    await pickWorkflowAndFillForm(user);
    await user.click(screen.getByRole("button", { name: "Diagnose" }));

    expect(await screen.findByText("502 Bad Gateway")).toBeInTheDocument();
  });

  it("gates Apply fix behind useConfirm: cancel does not call remediate", async () => {
    const user = userEvent.setup();
    renderWorkflows();

    await pickWorkflowAndFillForm(user);
    await user.click(screen.getByRole("button", { name: "Apply fix" }));

    expect(await screen.findByText("Apply this fix?")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Cancel" }));

    expect(api.workflows.remediate).not.toHaveBeenCalled();
  });

  it("gates Apply fix behind useConfirm: confirm calls remediate", async () => {
    vi.mocked(api.workflows.remediate).mockResolvedValue({
      steps: [{ name: "Clear methods", success: true }],
      succeeded: true
    });
    const user = userEvent.setup();
    renderWorkflows();

    await pickWorkflowAndFillForm(user);
    await user.click(screen.getByRole("button", { name: "Apply fix" }));
    await user.click(screen.getByRole("button", { name: "Confirm" }));

    expect(api.workflows.remediate).toHaveBeenCalledWith("mfa-reset", "t1", { userUpn: "ada@contoso.com" });
    expect(await screen.findByText("Clear methods")).toBeInTheDocument();
    expect(await screen.findByText("Fix applied successfully.")).toBeInTheDocument();
  });

  it("clears a stale diagnose error once Apply fix is confirmed and succeeds", async () => {
    vi.mocked(api.workflows.diagnose).mockRejectedValue(new Error("502 Bad Gateway"));
    vi.mocked(api.workflows.remediate).mockResolvedValue({
      steps: [{ name: "Clear methods", success: true }],
      succeeded: true
    });
    const user = userEvent.setup();
    renderWorkflows();

    await pickWorkflowAndFillForm(user);
    await user.click(screen.getByRole("button", { name: "Diagnose" }));
    expect(await screen.findByText("502 Bad Gateway")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Apply fix" }));
    await user.click(screen.getByRole("button", { name: "Confirm" }));

    expect(await screen.findByText("Clear methods")).toBeInTheDocument();
    expect(screen.queryByText("502 Bad Gateway")).not.toBeInTheDocument();
  });

  it("does not apply a diagnose response that resolves after the tenant has since changed", async () => {
    const tenant2: Tenant = { id: "t2", tenantId: "bbbb", displayName: "Fabrikam", status: "Active" };
    vi.mocked(api.tenants.list).mockResolvedValue([...TENANTS, tenant2]);
    let resolveDiagnose!: (v: unknown) => void;
    vi.mocked(api.workflows.diagnose).mockReturnValue(new Promise((res) => { resolveDiagnose = res; }) as never);

    const user = userEvent.setup();
    renderWorkflows();

    await pickWorkflowAndFillForm(user); // selects Contoso Ltd (t1)
    await user.click(screen.getByRole("button", { name: "Diagnose" }));

    // Switch tenant before the in-flight diagnose response resolves.
    await user.click(screen.getByLabelText("Tenant"));
    await user.click(await screen.findByRole("option", { name: "Fabrikam" }));

    resolveDiagnose({ findings: [{ name: "MFA methods", status: "Ok", detail: "none registered" }], healthy: true });
    await new Promise((r) => setTimeout(r, 0));

    expect(screen.queryByText("MFA methods")).not.toBeInTheDocument();
  });

  it("clears a shown diagnosis when the tenant changes, instead of leaving it displayed under the new tenant", async () => {
    vi.mocked(api.workflows.diagnose).mockResolvedValue({
      findings: [{ name: "MFA methods", status: "Ok", detail: "none registered" }],
      healthy: true
    });
    const tenant2: Tenant = { id: "t2", tenantId: "bbbb", displayName: "Fabrikam", status: "Active" };
    vi.mocked(api.tenants.list).mockResolvedValue([...TENANTS, tenant2]);
    const user = userEvent.setup();
    renderWorkflows();

    await pickWorkflowAndFillForm(user);
    await user.click(screen.getByRole("button", { name: "Diagnose" }));
    expect(await screen.findByText("MFA methods")).toBeInTheDocument();

    await user.click(screen.getByLabelText("Tenant"));
    await user.click(await screen.findByRole("option", { name: "Fabrikam" }));

    expect(screen.queryByText("MFA methods")).not.toBeInTheDocument();
  });

  it("shows recent runs once loaded", async () => {
    vi.mocked(api.workflows.runs).mockResolvedValue([
      {
        id: "r1", workflowId: "mfa-reset", workflowName: "MFA reset", tenantId: "t1", tenantName: "Contoso Ltd",
        kind: "Diagnose", operator: "jspillers", inputs: {}, findings: [], steps: [], succeeded: true,
        startedAt: "2026-08-19T09:00:00Z", durationMs: 100
      }
    ]);
    renderWorkflows();

    expect(await screen.findByRole("cell", { name: "MFA reset" })).toBeInTheDocument();
    expect(screen.getByText("jspillers")).toBeInTheDocument();
    expect(screen.getByText("ok")).toBeInTheDocument();
  });
});
