import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
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

  it("does not let an older refresh response overwrite a newer one when two rows decide close together", async () => {
    // Regression test: each row's decision triggers its own load() call (there's no single
    // shared in-flight guard) -- if two rows decide close together and their list() responses
    // arrive out of order, the slower/older one must not overwrite the newer, more accurate one.
    vi.mocked(api.pendingActions.approve).mockResolvedValue(undefined);
    let refreshCallCount = 0;
    let resolveOlderRefresh!: (items: PendingAction[]) => void;
    let resolveNewerRefresh!: (items: PendingAction[]) => void;
    vi.mocked(api.pendingActions.list)
      .mockResolvedValueOnce([PENDING, PENDING2]) // initial load
      .mockImplementation(() => {
        refreshCallCount += 1;
        if (refreshCallCount === 1) return new Promise((resolve) => { resolveOlderRefresh = resolve; });
        return new Promise((resolve) => { resolveNewerRefresh = resolve; });
      });

    const user = userEvent.setup();
    renderApprovals();
    await screen.findByText("Contoso Ltd");

    await user.click(screen.getAllByRole("button", { name: "Approve" })[0]);
    await user.click(screen.getByRole("button", { name: "Confirm" })); // row1 decided -> older refresh issued
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());

    await user.click(screen.getByRole("button", { name: "Approve" })); // row2's own button
    await user.click(screen.getByRole("button", { name: "Confirm" })); // row2 decided -> newer refresh issued
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());

    // The newer refresh resolves first, correctly reflecting both decisions (both items are gone
    // -- an approved/rejected/retried item drops out of the pending list entirely).
    resolveNewerRefresh([]);
    await screen.findByText("No pending approvals.");

    // The older, stale refresh (which only reflects row1's decision) resolves after -- it must not
    // resurrect row2 as pending again.
    resolveOlderRefresh([PENDING2]);
    await new Promise((r) => setTimeout(r, 0));
    expect(screen.getByText("No pending approvals.")).toBeInTheDocument();
    expect(screen.queryByText("Northwind Traders")).not.toBeInTheDocument();
  });

  it("keeps a decided row's buttons disabled even if the post-decision refresh fails", async () => {
    // Regression test: the row's disabled state used to be driven purely by decideAction.busy,
    // which cleared once the whole action settled regardless of outcome -- if the background list
    // refresh failed, the row reverted to its old, seemingly-still-pending status with its buttons
    // re-enabled, inviting a second decision on an action that had already gone through
    // server-side. The row must stay permanently disabled once its own mutation succeeds,
    // independent of whether the refresh afterward succeeds, fails, or races another row's.
    vi.mocked(api.pendingActions.approve).mockResolvedValue(undefined);
    vi.mocked(api.pendingActions.list).mockResolvedValueOnce([PENDING]);
    vi.mocked(api.pendingActions.list).mockRejectedValueOnce(new Error("refresh failed"));
    const user = userEvent.setup();
    renderApprovals();

    await user.click(await screen.findByRole("button", { name: "Approve" }));
    await user.click(screen.getByRole("button", { name: "Confirm" }));

    expect(await screen.findByText("Approved.")).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole("button", { name: "Approve" })).toBeDisabled());
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
