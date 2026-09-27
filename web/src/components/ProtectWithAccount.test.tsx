import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ThemeProvider } from "@mui/material/styles";
import { theme } from "../theme";
import { ToastProvider } from "../hooks/useToast";
import { ConfirmDialogProvider } from "../hooks/useConfirm";
import { Security } from "./Security";
import type { MeProfile } from "../types";

vi.mock("../api", () => ({
  api: {
    auth: { protectOwner: vi.fn() },
    passkey: { list: vi.fn(), remove: vi.fn(), registerOptions: vi.fn(), registerVerify: vi.fn() },
    totp: { enroll: vi.fn(), verifyEnroll: vi.fn(), disable: vi.fn() },
    mcpTokens: { list: vi.fn(), create: vi.fn(), revoke: vi.fn() }
  }
}));
vi.mock("../session", () => ({ setLocalToken: vi.fn() }));
vi.mock("../webauthn", () => ({ createPasskey: vi.fn() }));

import { api } from "../api";
import { setLocalToken } from "../session";

const owner: MeProfile = {
  id: "o1", email: "owner@workbench.local", displayName: "maya (this computer)", isSystemAdmin: true,
  totpEnabled: false, tenantAccess: [], instanceRoles: ["Administrator"], isWorkbenchOwner: true
};

function renderSecurity(me: MeProfile, onAccountProtected = vi.fn()) {
  render(
    <ThemeProvider theme={theme}>
      <ToastProvider>
        <ConfirmDialogProvider>
          <Security me={me} onProfileChanged={vi.fn()} onAccountProtected={onAccountProtected} />
        </ConfirmDialogProvider>
      </ToastProvider>
    </ThemeProvider>
  );
  return onAccountProtected;
}

describe("Protect with an account (workbench without an account)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.passkey.list).mockResolvedValue([]);
    vi.mocked(api.mcpTokens.list).mockResolvedValue([]);
  });

  it("replaces passkeys and two-factor with the conversion form for the owner", async () => {
    renderSecurity(owner);
    expect(await screen.findByRole("heading", { name: "Protect with an account" })).toBeInTheDocument();
    expect(screen.getByText(/Signed in as maya \(this computer\), without an account/)).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Passkeys" })).not.toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: /Two-factor/ })).not.toBeInTheDocument();
    // MCP tokens keep working for the owner.
    expect(screen.getByRole("heading", { name: "MCP access tokens" })).toBeInTheDocument();
    expect(screen.getByLabelText("Display name")).toHaveValue("maya");
  });

  it("sets the email and password after a confirmation and stores the new session", async () => {
    vi.mocked(api.auth.protectOwner).mockResolvedValue({
      accessToken: "new-tok", user: { ...owner, email: "maya@contoso.com", displayName: "Maya", isWorkbenchOwner: false }
    });
    const user = userEvent.setup();
    const onAccountProtected = renderSecurity(owner);

    await user.clear(await screen.findByLabelText("Display name"));
    await user.type(screen.getByLabelText("Display name"), "Maya");
    await user.type(screen.getByLabelText("Email"), "maya@contoso.com");
    await user.type(screen.getByLabelText("Password (12+ characters)"), "correct-horse-battery");
    await user.click(screen.getByRole("button", { name: "Protect with an account" }));

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText(/Launch links, including the one PartnerCenterBridge.exe opens, stop working/)).toBeInTheDocument();
    expect(api.auth.protectOwner).not.toHaveBeenCalled();
    await user.click(within(dialog).getByRole("button", { name: "Protect with an account" }));

    await waitFor(() => expect(api.auth.protectOwner).toHaveBeenCalledWith("maya@contoso.com", "correct-horse-battery", "Maya"));
    expect(setLocalToken).toHaveBeenCalledWith("new-tok");
    expect(onAccountProtected).toHaveBeenCalled();
  });

  it("shows the reason when the server refuses the password", async () => {
    vi.mocked(api.auth.protectOwner).mockRejectedValue(new Error("Password must be at least 12 characters."));
    const user = userEvent.setup();
    renderSecurity(owner);
    await user.type(await screen.findByLabelText("Email"), "maya@contoso.com");
    await user.type(screen.getByLabelText("Password (12+ characters)"), "short");
    await user.click(screen.getByRole("button", { name: "Protect with an account" }));
    await user.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Protect with an account" }));
    expect(await screen.findByText("Password must be at least 12 characters.")).toBeInTheDocument();
  });

  it("is not shown to an ordinary account", async () => {
    renderSecurity({ ...owner, email: "maya@contoso.com", isWorkbenchOwner: false });
    expect(await screen.findByRole("heading", { name: "Passkeys" })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Protect with an account" })).not.toBeInTheDocument();
  });
});
