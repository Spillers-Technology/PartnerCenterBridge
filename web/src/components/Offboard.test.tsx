import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ThemeProvider } from "@mui/material/styles";
import { theme } from "../theme";
import { ConfirmDialogProvider } from "../hooks/useConfirm";
import { ToastProvider } from "../hooks/useToast";
import { Offboard } from "./Offboard";
import type { DirectoryObject, ProvisioningResult, Tenant } from "../types";

vi.mock("../api", () => ({
  api: {
    tenants: { list: vi.fn() },
    directory: { users: vi.fn() },
    provisioning: { terminate: vi.fn() }
  }
}));

import { api } from "../api";

const tenant: Tenant = { id: "t1", tenantId: "guid-1", displayName: "Contoso", defaultDomain: "contoso.com", status: "Active" };
const directoryUser: DirectoryObject = { id: "u1", displayName: "Ada Lovelace", userPrincipalName: "ada@contoso.com" };
const result: ProvisioningResult = {
  userId: "u1",
  userPrincipalName: "ada@contoso.com",
  steps: [{ name: "Block sign-in", success: true, detail: "Done" }],
  succeeded: true
};

function renderOffboard() {
  render(
    <ThemeProvider theme={theme}>
      <ToastProvider>
        <ConfirmDialogProvider>
          <Offboard />
        </ConfirmDialogProvider>
      </ToastProvider>
    </ThemeProvider>
  );
}

async function selectTenantAndUser(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByLabelText("Tenant"));
  await user.click(screen.getByRole("option", { name: "Contoso" }));
  await user.type(screen.getByLabelText("Search name or UPN"), "Ada");
  await user.click(screen.getByRole("button", { name: "Search users" }));
  await user.click(await screen.findByLabelText("User"));
  await user.click(screen.getByRole("option", { name: "Ada Lovelace (ada@contoso.com)" }));
}

describe("Offboard", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.tenants.list).mockResolvedValue([tenant]);
    vi.mocked(api.directory.users).mockResolvedValue([directoryUser]);
    vi.mocked(api.provisioning.terminate).mockResolvedValue(result);
  });

  it("searches users after selecting a tenant", async () => {
    const user = userEvent.setup();
    renderOffboard();

    await user.click(await screen.findByLabelText("Tenant"));
    await user.click(screen.getByRole("option", { name: "Contoso" }));
    await user.type(screen.getByLabelText("Search name or UPN"), "Ada");
    await user.click(screen.getByRole("button", { name: "Search users" }));

    expect(await screen.findByLabelText("User")).toBeInTheDocument();
    expect(api.directory.users).toHaveBeenCalledWith("t1", "Ada");
  });

  it("gates offboarding behind confirm: cancel does not call the API, confirm does", async () => {
    const user = userEvent.setup();
    renderOffboard();
    await selectTenantAndUser(user);

    await user.click(screen.getByRole("button", { name: "Offboard user" }));
    const cancelDialog = await screen.findByRole("dialog");
    expect(within(cancelDialog).getByText(/Ada Lovelace will be offboarded/)).toBeInTheDocument();
    await user.click(within(cancelDialog).getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(api.provisioning.terminate).not.toHaveBeenCalled();

    await user.click(screen.getByRole("button", { name: "Offboard user" }));
    const confirmDialog = await screen.findByRole("dialog");
    await user.click(within(confirmDialog).getByRole("button", { name: "Offboard" }));

    await waitFor(() => expect(api.provisioning.terminate).toHaveBeenCalledWith("t1", {
      userId: "u1",
      blockSignIn: true,
      revokeSessions: true,
      removeLicenses: true,
      removeFromGroups: true,
      convertMailboxToShared: false,
      forwardingSmtpAddress: undefined
    }));
    expect(await screen.findByText("Ada Lovelace offboarded")).toBeInTheDocument();
    expect(await screen.findByText("Done")).toBeInTheDocument();
  });

  it("shows a search error", async () => {
    vi.mocked(api.directory.users).mockRejectedValue(new Error("search failed"));
    const user = userEvent.setup();
    renderOffboard();

    await user.click(await screen.findByLabelText("Tenant"));
    await user.click(screen.getByRole("option", { name: "Contoso" }));
    await user.click(screen.getByRole("button", { name: "Search users" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("search failed");
  });

  it("includes the forwarding address when converting the mailbox to shared", async () => {
    const user = userEvent.setup();
    renderOffboard();
    await selectTenantAndUser(user);

    await user.click(screen.getByLabelText("Convert mailbox to shared (Exchange Online)"));
    await user.type(screen.getByLabelText("Forward mailbox to (optional SMTP)"), "manager@contoso.com");

    await user.click(screen.getByRole("button", { name: "Offboard user" }));
    const dialog = await screen.findByRole("dialog");
    await user.click(within(dialog).getByRole("button", { name: "Offboard" }));

    await waitFor(() => expect(api.provisioning.terminate).toHaveBeenCalledWith("t1", {
      userId: "u1",
      blockSignIn: true,
      revokeSessions: true,
      removeLicenses: true,
      removeFromGroups: true,
      convertMailboxToShared: true,
      forwardingSmtpAddress: "manager@contoso.com"
    }));
  });

  it("shows an error alert when the terminate call fails", async () => {
    vi.mocked(api.provisioning.terminate).mockRejectedValue(new Error("terminate failed"));
    const user = userEvent.setup();
    renderOffboard();
    await selectTenantAndUser(user);

    await user.click(screen.getByRole("button", { name: "Offboard user" }));
    const dialog = await screen.findByRole("dialog");
    await user.click(within(dialog).getByRole("button", { name: "Offboard" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("terminate failed");
  });

  it("disables the offboard button while a confirm is pending, before the API call itself starts", async () => {
    // Regression guard: useAsyncAction's busy flag only turns on once the terminate call starts,
    // which used to leave a window open (while confirm() is awaited) where a second click could
    // queue a second confirm request. The button must disable as soon as the first click starts
    // the confirm flow, not just once the destructive call itself begins.
    const user = userEvent.setup();
    renderOffboard();
    await selectTenantAndUser(user);

    const offboardButton = screen.getByRole("button", { name: "Offboard user" });
    await user.click(offboardButton);
    await screen.findByRole("dialog");

    expect(offboardButton).toBeDisabled();
  });
});

