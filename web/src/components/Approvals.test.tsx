import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ThemeProvider } from "@mui/material/styles";
import { theme } from "../theme";
import { ConfirmDialogProvider } from "../hooks/useConfirm";
import { ToastProvider } from "../hooks/useToast";
import { Approvals } from "./Approvals";
import type { PendingAction } from "../types";

vi.mock("../api", () => ({
  api: {
    pendingActions: {
      list: vi.fn(),
      approve: vi.fn(),
      reject: vi.fn(),
      retry: vi.fn()
    }
  }
}));

import { api } from "../api";

const PENDING: PendingAction = {
  id: "pa1",
  tenantId: "t1",
  tenantName: "Contoso Ltd",
  actionType: "Reset MFA",
  previewSummary: "Reset MFA for ada@contoso.com",
  status: "Pending",
  createdAt: "2026-08-19T09:00:00Z",
  expiresAt: "2026-08-20T09:00:00Z",
  executionError: null
};

const PENDING2: PendingAction = {
  id: "pa3",
  tenantId: "t2",
  tenantName: "Northwind Traders",
  actionType: "Password reset",
  previewSummary: "Reset password for carol@northwind.com",
  status: "Pending",
  createdAt: "2026-08-19T10:00:00Z",
  expiresAt: "2026-08-20T10:00:00Z",
  executionError: null
};

const FAILED: PendingAction = {
  id: "pa2",
  tenantId: "t1",
  tenantName: "Fabrikam Inc",
  actionType: "Offboard user",
  previewSummary: "Offboard bob@fabrikam.com",
  status: "Approved",
  createdAt: "2026-08-19T09:00:00Z",
  expiresAt: "2026-08-20T09:00:00Z",
  executionError: "Graph API 403"
};

function renderApprovals() {
  return render(
    <ThemeProvider theme={theme}>
      <ToastProvider>
        <ConfirmDialogProvider>
          <Approvals />
        </ConfirmDialogProvider>
      </ToastProvider>
    </ThemeProvider>
  );
}

describe("Approvals", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("renders a pending row with its tenant/action/status/preview/requested/expires", async () => {
    vi.mocked(api.pendingActions.list).mockResolvedValue([PENDING]);
    renderApprovals();

    expect(await screen.findByText("Contoso Ltd")).toBeInTheDocument();
    expect(screen.getByText("Reset MFA")).toBeInTheDocument();
    expect(screen.getByText("Pending")).toBeInTheDocument();
    expect(screen.getByText("Reset MFA for ada@contoso.com")).toBeInTheDocument();
    expect(screen.getByText(new Date(PENDING.createdAt).toLocaleString())).toBeInTheDocument();
    expect(screen.getByText(new Date(PENDING.expiresAt).toLocaleString())).toBeInTheDocument();
  });

  it("gates Approve behind useConfirm: cancel does not call approve", async () => {
    vi.mocked(api.pendingActions.list).mockResolvedValue([PENDING]);
    const user = userEvent.setup();
    renderApprovals();

    await user.click(await screen.findByRole("button", { name: "Approve" }));
    expect(await screen.findByText("Approve this action?")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Cancel" }));

    expect(api.pendingActions.approve).not.toHaveBeenCalled();
  });

  it("gates Approve behind useConfirm: confirm calls approve, reloads, and toasts", async () => {
    vi.mocked(api.pendingActions.list).mockResolvedValue([PENDING]);
    const user = userEvent.setup();
    renderApprovals();

    await user.click(await screen.findByRole("button", { name: "Approve" }));
    await user.click(screen.getByRole("button", { name: "Confirm" }));

    expect(api.pendingActions.approve).toHaveBeenCalledWith("pa1");
    expect(await screen.findByText("Approved.")).toBeInTheDocument();
    expect(api.pendingActions.list).toHaveBeenCalledTimes(2);
  });

  it("gates Reject behind useConfirm: cancel does not call reject", async () => {
    vi.mocked(api.pendingActions.list).mockResolvedValue([PENDING]);
    const user = userEvent.setup();
    renderApprovals();

    await user.click(await screen.findByRole("button", { name: "Reject" }));
    expect(await screen.findByText("Reject this action?")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Cancel" }));

    expect(api.pendingActions.reject).not.toHaveBeenCalled();
  });

  it("gates Reject behind useConfirm: confirm calls reject, reloads, and toasts", async () => {
    vi.mocked(api.pendingActions.list).mockResolvedValue([PENDING]);
    const user = userEvent.setup();
    renderApprovals();

    await user.click(await screen.findByRole("button", { name: "Reject" }));
    await user.click(screen.getByRole("button", { name: "Confirm" }));

    expect(api.pendingActions.reject).toHaveBeenCalledWith("pa1");
    expect(await screen.findByText("Rejected.")).toBeInTheDocument();
    expect(api.pendingActions.list).toHaveBeenCalledTimes(2);
  });

  it("shows a Failed row with only a Retry button, gated behind useConfirm", async () => {
    vi.mocked(api.pendingActions.list).mockResolvedValue([FAILED]);
    const user = userEvent.setup();
    renderApprovals();

    expect(await screen.findByText("Failed")).toBeInTheDocument();
    expect(screen.getByText("Graph API 403")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Approve" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Reject" })).not.toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Retry" }));
    expect(await screen.findByText("Retry this action?")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Confirm" }));

    expect(api.pendingActions.retry).toHaveBeenCalledWith("pa2");
    expect(await screen.findByText("Retried.")).toBeInTheDocument();
  });

  it("shows an inline error on the row when the mutating action fails", async () => {
    vi.mocked(api.pendingActions.list).mockResolvedValue([PENDING]);
    vi.mocked(api.pendingActions.approve).mockRejectedValue(new Error("409 Conflict"));
    const user = userEvent.setup();
    renderApprovals();

    await user.click(await screen.findByRole("button", { name: "Approve" }));
    await user.click(screen.getByRole("button", { name: "Confirm" }));

    expect(await screen.findByText("409 Conflict")).toBeInTheDocument();
  });

  it("decides two rows independently: one row's in-flight action does not disable or block another row's", async () => {
    vi.mocked(api.pendingActions.list).mockResolvedValue([PENDING, PENDING2]);
    let resolveFirstApprove!: () => void;
    vi.mocked(api.pendingActions.approve).mockImplementation((id) => {
      if (id === "pa1") return new Promise((resolve) => { resolveFirstApprove = () => resolve(undefined); });
      return Promise.resolve(undefined);
    });
    const user = userEvent.setup();
    renderApprovals();

    await screen.findByText("Contoso Ltd");
    const approveButtons = screen.getAllByRole("button", { name: "Approve" });
    expect(approveButtons).toHaveLength(2);

    await user.click(approveButtons[0]);
    await user.click(screen.getByRole("button", { name: "Confirm" }));

    // Row 1's approve() is still pending (controlled promise), so its button should now read
    // "Approving..." and be disabled -- but row 2's own useAsyncAction instance is untouched.
    expect(await screen.findByRole("button", { name: "Approving..." })).toBeDisabled();
    const row2Approve = screen.getByRole("button", { name: "Approve" });
    expect(row2Approve).not.toBeDisabled();

    await user.click(row2Approve);
    await user.click(screen.getByRole("button", { name: "Confirm" }));

    expect(api.pendingActions.approve).toHaveBeenCalledWith("pa3");
    expect(await screen.findByText("Approved.")).toBeInTheDocument();

    resolveFirstApprove();
    expect(api.pendingActions.approve).toHaveBeenCalledWith("pa1");
  });

  it("keeps a decided row disabled until its refresh actually lands, not just until the mutation call returns", async () => {
    // Regression test: onDecided() used to fire without being awaited, so decideAction's busy
    // flag (and the row's disabled buttons, and the success toast) cleared as soon as approve()
    // returned -- before the refreshed list had loaded -- inviting a second click on an action
    // no longer actually pending.
    // vi.clearAllMocks() (in beforeEach) clears call history but not a previously set
    // mockImplementation -- reassert approve()'s behavior explicitly rather than relying on
    // whatever an earlier test in this file left configured for the same mock.
    vi.mocked(api.pendingActions.approve).mockResolvedValue(undefined);
    let listCallCount = 0;
    let resolveRefresh!: (items: PendingAction[]) => void;
    vi.mocked(api.pendingActions.list).mockImplementation(() => {
      listCallCount += 1;
      if (listCallCount === 1) return Promise.resolve([PENDING]);
      return new Promise((resolve) => { resolveRefresh = resolve; });
    });
    const user = userEvent.setup();
    renderApprovals();

    await user.click(await screen.findByRole("button", { name: "Approve" }));
    await user.click(screen.getByRole("button", { name: "Confirm" }));

    // approve() itself resolves immediately, but the post-decision refresh is deliberately left
    // pending -- the row must stay busy/disabled and the toast must not fire through that gap.
    await screen.findByRole("button", { name: "Approving..." });
    await new Promise((r) => setTimeout(r, 0));
    expect(screen.getByRole("button", { name: "Approving..." })).toBeDisabled();
    expect(screen.queryByText("Approved.")).not.toBeInTheDocument();

    resolveRefresh([{ ...PENDING, status: "Approved", executionError: null }]);
    expect(await screen.findByText("Approved.")).toBeInTheDocument();
  });

  it("shows an error alert when the initial load fails", async () => {
    vi.mocked(api.pendingActions.list).mockRejectedValue(new Error("503 Service Unavailable"));
    renderApprovals();

    expect(await screen.findByText("503 Service Unavailable")).toBeInTheDocument();
  });

  it("shows the empty-state message and no table when there are no pending approvals", async () => {
    vi.mocked(api.pendingActions.list).mockResolvedValue([]);
    renderApprovals();

    expect(await screen.findByText("No pending approvals.")).toBeInTheDocument();
    expect(screen.queryByRole("table")).not.toBeInTheDocument();
  });
});
