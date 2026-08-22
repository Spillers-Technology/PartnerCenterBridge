import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
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

  it("copies a shown-once ephemeral secret to the clipboard", async () => {
    // Regression test: an ephemeral secret (e.g. a temp password) used to render as plain text
    // with no copy action -- it's shown exactly once, so retyping it by hand is the only fallback.
    vi.mocked(api.workflows.remediate).mockResolvedValue({
      steps: [{ name: "Reset password", success: true }],
      succeeded: true,
      ephemeral: { tempPassword: "Sup3r$ecret!" }
    });
    const user = userEvent.setup();
    renderWorkflows();

    await pickWorkflowAndFillForm(user);
    await user.click(screen.getByRole("button", { name: "Apply fix" }));
    await user.click(screen.getByRole("button", { name: "Confirm" }));
    expect(await screen.findByText("Sup3r$ecret!")).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());

    // The toast queue shows one at a time -- dismiss the "Fix applied successfully." toast
    // already showing so the "Copied" toast (below) isn't just queued invisibly behind it.
    await user.click(await screen.findByRole("button", { name: "Close" }));
    await waitFor(() => expect(screen.queryByText("Fix applied successfully.")).not.toBeInTheDocument());

    // user-event's own setup() installs its own navigator.clipboard stub -- define after setup()
    // so this mock isn't the one that gets clobbered.
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, "clipboard", { value: { writeText }, configurable: true });

    await user.click(screen.getByRole("button", { name: "Copy" }));

    expect(writeText).toHaveBeenCalledWith("Sup3r$ecret!");
    expect(await screen.findByText("Copied")).toBeInTheDocument();
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

  it("tells the user about a fix that finished after they'd already switched tenants, instead of silently discarding it", async () => {
    // Regression test: a discarded stale fix result used to leave the user with no idea a real,
    // already-applied mutation had happened -- the toast claimed "see the step detail" for detail
    // that was never shown.
    const tenant2: Tenant = { id: "t2", tenantId: "bbbb", displayName: "Fabrikam", status: "Active" };
    vi.mocked(api.tenants.list).mockResolvedValue([...TENANTS, tenant2]);
    let resolveRemediate!: (v: unknown) => void;
    vi.mocked(api.workflows.remediate).mockReturnValue(new Promise((res) => { resolveRemediate = res; }) as never);

    const user = userEvent.setup();
    renderWorkflows();

    await pickWorkflowAndFillForm(user);
    await user.click(screen.getByRole("button", { name: "Apply fix" }));
    await user.click(screen.getByRole("button", { name: "Confirm" }));

    await user.click(screen.getByLabelText("Tenant"));
    await user.click(await screen.findByRole("option", { name: "Fabrikam" }));

    resolveRemediate({ steps: [{ name: "Clear methods", success: true }], succeeded: true });

    expect(await screen.findByText(/A fix you started earlier finished successfully/)).toBeInTheDocument();
    expect(screen.queryByText("Fix applied successfully.")).not.toBeInTheDocument();
    expect(screen.queryByText("Clear methods")).not.toBeInTheDocument();
  });

  it("does not surface a diagnose error under a tenant the user has since switched to", async () => {
    // Regression test: clearOutput() only cleared displayed findings, not the action error state
    // -- a diagnose call that rejected after a tenant switch could still show its stale error
    // under the newly selected tenant.
    const tenant2: Tenant = { id: "t2", tenantId: "bbbb", displayName: "Fabrikam", status: "Active" };
    vi.mocked(api.tenants.list).mockResolvedValue([...TENANTS, tenant2]);
    let rejectDiagnose!: (e: Error) => void;
    vi.mocked(api.workflows.diagnose).mockReturnValue(new Promise((_res, rej) => { rejectDiagnose = rej; }) as never);

    const user = userEvent.setup();
    renderWorkflows();

    await pickWorkflowAndFillForm(user);
    await user.click(screen.getByRole("button", { name: "Diagnose" }));

    await user.click(screen.getByLabelText("Tenant"));
    await user.click(await screen.findByRole("option", { name: "Fabrikam" }));

    rejectDiagnose(new Error("502 Bad Gateway"));
    await new Promise((r) => setTimeout(r, 0));

    expect(screen.queryByText("502 Bad Gateway")).not.toBeInTheDocument();
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
