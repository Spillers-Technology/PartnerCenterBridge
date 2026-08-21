import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ThemeProvider } from "@mui/material/styles";
import { theme } from "../theme";
import { ToastProvider } from "../hooks/useToast";
import { ConfirmDialogProvider } from "../hooks/useConfirm";
import { Security } from "./Security";
import type { McpTokenInfo, MeProfile, PasskeyInfo } from "../types";

vi.mock("../api", () => ({
  api: {
    passkey: {
      list: vi.fn(),
      remove: vi.fn(),
      registerOptions: vi.fn(),
      registerVerify: vi.fn()
    },
    totp: {
      enroll: vi.fn(),
      verifyEnroll: vi.fn(),
      disable: vi.fn()
    },
    mcpTokens: {
      list: vi.fn(),
      create: vi.fn(),
      revoke: vi.fn()
    }
  }
}));
vi.mock("../webauthn", () => ({ createPasskey: vi.fn() }));

import { api } from "../api";
import { createPasskey } from "../webauthn";

const me: MeProfile = {
  id: "u1",
  email: "j@example.com",
  displayName: "Joey",
  isSystemAdmin: false,
  totpEnabled: false,
  tenantAccess: []
};

const passkey: PasskeyInfo = {
  id: "p1",
  nickname: "YubiKey",
  createdAt: "2026-08-01T00:00:00Z",
  lastUsedAt: "2026-08-10T00:00:00Z"
};

const mcpToken: McpTokenInfo = {
  id: "t1",
  name: "Claude Desktop",
  createdAt: "2026-08-01T00:00:00Z",
  expiresAt: null,
  lastUsedAt: null
};

function renderSecurity(meProfile: MeProfile = me, onProfileChanged = vi.fn()) {
  return { onProfileChanged, ...render(
    <ThemeProvider theme={theme}>
      <ToastProvider>
        <ConfirmDialogProvider>
          <Security me={meProfile} onProfileChanged={onProfileChanged} />
        </ConfirmDialogProvider>
      </ToastProvider>
    </ThemeProvider>
  ) };
}

describe("Security", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.passkey.list).mockResolvedValue([]);
    vi.mocked(api.mcpTokens.list).mockResolvedValue([]);
  });

  it("loads and renders passkeys and MCP tokens", async () => {
    vi.mocked(api.passkey.list).mockResolvedValue([passkey]);
    vi.mocked(api.mcpTokens.list).mockResolvedValue([mcpToken]);
    renderSecurity();

    expect(await screen.findByText("YubiKey")).toBeInTheDocument();
    expect(screen.getByText("Claude Desktop")).toBeInTheDocument();
  });

  it("shows empty-state messages when both lists are empty", async () => {
    renderSecurity();

    expect(await screen.findByText("No passkeys registered yet.")).toBeInTheDocument();
    expect(screen.getByText("No MCP tokens yet.")).toBeInTheDocument();
  });

  it("shows the bare error message on a failed load (not the String(e)-wrapped 'Error: ...' form)", async () => {
    vi.mocked(api.passkey.list).mockRejectedValue(new Error("boom"));
    renderSecurity();

    expect(await screen.findByText("boom")).toBeInTheDocument();
    expect(screen.queryByText("Error: boom")).not.toBeInTheDocument();
  });

  it("requires confirming before removing a passkey -- Cancel does not call the API", async () => {
    vi.mocked(api.passkey.list).mockResolvedValue([passkey]);
    const user = userEvent.setup();
    renderSecurity();

    await screen.findByText("YubiKey");
    await user.click(screen.getByRole("button", { name: "Remove" }));

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText("Remove this passkey?")).toBeInTheDocument();
    expect(within(dialog).getByText(/YubiKey/)).toBeInTheDocument();

    await user.click(within(dialog).getByRole("button", { name: "Cancel" }));

    expect(api.passkey.remove).not.toHaveBeenCalled();
    expect(api.passkey.list).toHaveBeenCalledTimes(1);
  });

  it("removes a passkey and reloads the list once the confirm dialog is accepted", async () => {
    vi.mocked(api.passkey.list).mockResolvedValue([passkey]);
    vi.mocked(api.passkey.remove).mockResolvedValue(undefined);
    const user = userEvent.setup();
    renderSecurity();

    await screen.findByText("YubiKey");
    await user.click(screen.getByRole("button", { name: "Remove" }));

    const dialog = await screen.findByRole("dialog");
    vi.mocked(api.passkey.list).mockResolvedValue([]);
    await user.click(within(dialog).getByRole("button", { name: "Remove" }));

    expect(await screen.findByText("Passkey removed")).toBeInTheDocument();
    expect(api.passkey.remove).toHaveBeenCalledWith("p1");
    expect(api.passkey.list).toHaveBeenCalledTimes(2);
  });

  it("keeps a passkey error visible when an unrelated MCP token action starts afterward", async () => {
    // Each panel tracks its own "last action" independently -- one panel's error must not be
    // knocked off screen just because a different, unrelated panel started an action.
    vi.mocked(api.passkey.list).mockResolvedValue([passkey]);
    vi.mocked(api.passkey.remove).mockRejectedValue(new Error("cannot remove your last passkey"));
    vi.mocked(api.mcpTokens.create).mockResolvedValue({ id: "t2", name: "New Token", jwt: "jwt-value" });
    const user = userEvent.setup();
    renderSecurity();

    await screen.findByText("YubiKey");
    await user.click(screen.getByRole("button", { name: "Remove" }));
    const dialog = await screen.findByRole("dialog");
    await user.click(within(dialog).getByRole("button", { name: "Remove" }));

    expect(await screen.findByText("cannot remove your last passkey")).toBeInTheDocument();

    await user.type(screen.getByLabelText('Name this token (e.g. "Claude Desktop")'), "New Token");
    await user.click(screen.getByRole("button", { name: "Create token" }));

    // The passkey error must still be visible even though an MCP token action has since started.
    expect(screen.getByText("cannot remove your last passkey")).toBeInTheDocument();
  });

  it("requires confirming before revoking an MCP token -- Cancel does not call the API", async () => {
    vi.mocked(api.mcpTokens.list).mockResolvedValue([mcpToken]);
    const user = userEvent.setup();
    renderSecurity();

    await screen.findByText("Claude Desktop");
    await user.click(screen.getByRole("button", { name: "Revoke" }));

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText("Revoke this token?")).toBeInTheDocument();
    expect(within(dialog).getByText(/Claude Desktop/)).toBeInTheDocument();

    await user.click(within(dialog).getByRole("button", { name: "Cancel" }));

    expect(api.mcpTokens.revoke).not.toHaveBeenCalled();
    expect(api.mcpTokens.list).toHaveBeenCalledTimes(1);
  });

  it("revokes an MCP token and reloads the list once the confirm dialog is accepted", async () => {
    vi.mocked(api.mcpTokens.list).mockResolvedValue([mcpToken]);
    vi.mocked(api.mcpTokens.revoke).mockResolvedValue(undefined);
    const user = userEvent.setup();
    renderSecurity();

    await screen.findByText("Claude Desktop");
    await user.click(screen.getByRole("button", { name: "Revoke" }));

    const dialog = await screen.findByRole("dialog");
    vi.mocked(api.mcpTokens.list).mockResolvedValue([]);
    await user.click(within(dialog).getByRole("button", { name: "Revoke" }));

    expect(await screen.findByText("Token revoked")).toBeInTheDocument();
    expect(api.mcpTokens.revoke).toHaveBeenCalledWith("t1");
    expect(api.mcpTokens.list).toHaveBeenCalledTimes(2);
  });

  it("requires confirming before disabling 2FA -- Cancel does not call the API", async () => {
    const user = userEvent.setup();
    renderSecurity({ ...me, totpEnabled: true });

    await user.type(screen.getByLabelText("Confirm your password to disable 2FA"), "hunter2222222");
    await user.click(screen.getByRole("button", { name: "Disable 2FA" }));

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText("Disable two-factor authentication?")).toBeInTheDocument();

    await user.click(within(dialog).getByRole("button", { name: "Cancel" }));

    expect(api.totp.disable).not.toHaveBeenCalled();
  });

  it("disables 2FA with the entered password once the confirm dialog is accepted", async () => {
    vi.mocked(api.totp.disable).mockResolvedValue(undefined);
    const user = userEvent.setup();
    const { onProfileChanged } = renderSecurity({ ...me, totpEnabled: true });

    await user.type(screen.getByLabelText("Confirm your password to disable 2FA"), "hunter2222222");
    await user.click(screen.getByRole("button", { name: "Disable 2FA" }));

    const dialog = await screen.findByRole("dialog");
    await user.click(within(dialog).getByRole("button", { name: "Disable 2FA" }));

    expect(await screen.findByText("2FA disabled")).toBeInTheDocument();
    expect(api.totp.disable).toHaveBeenCalledWith("hunter2222222");
    expect(onProfileChanged).toHaveBeenCalled();
  });

  it("adds a passkey via the nickname dialog instead of window.prompt", async () => {
    vi.mocked(api.passkey.registerOptions).mockResolvedValue({ challengeKey: "ck1", options: {} });
    vi.mocked(createPasskey).mockResolvedValue({ attestation: "resp" } as never);
    vi.mocked(api.passkey.registerVerify).mockResolvedValue(undefined);
    const user = userEvent.setup();
    renderSecurity();

    await screen.findByText("No passkeys registered yet.");
    await user.click(screen.getByRole("button", { name: "Add a passkey" }));

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText("Name this passkey")).toBeInTheDocument();

    await user.type(within(dialog).getByLabelText("Nickname"), "Work Laptop");
    vi.mocked(api.passkey.list).mockResolvedValue([passkey]);
    await user.click(within(dialog).getByRole("button", { name: "Add" }));

    expect(await screen.findByText("Passkey added")).toBeInTheDocument();
    expect(api.passkey.registerVerify).toHaveBeenCalledWith("ck1", { attestation: "resp" }, "Work Laptop");
    expect(api.passkey.list).toHaveBeenCalledTimes(2);
  });

  it("cancelling the nickname dialog never starts the WebAuthn ceremony (no orphaned credential)", async () => {
    vi.mocked(api.passkey.registerOptions).mockResolvedValue({ challengeKey: "ck1", options: {} });
    vi.mocked(createPasskey).mockResolvedValue({ attestation: "resp" } as never);
    const user = userEvent.setup();
    renderSecurity();

    await screen.findByText("No passkeys registered yet.");
    await user.click(screen.getByRole("button", { name: "Add a passkey" }));

    const dialog = await screen.findByRole("dialog");
    await user.click(within(dialog).getByRole("button", { name: "Cancel" }));

    // The nickname is collected before the WebAuthn ceremony starts, so Cancel here must be a true
    // no-op: no credential should ever be created on the authenticator for the server to not know
    // about.
    expect(api.passkey.registerOptions).not.toHaveBeenCalled();
    expect(createPasskey).not.toHaveBeenCalled();
    expect(api.passkey.registerVerify).not.toHaveBeenCalled();
  });

  it("defaults an empty nickname to undefined when adding a passkey", async () => {
    vi.mocked(api.passkey.registerOptions).mockResolvedValue({ challengeKey: "ck1", options: {} });
    vi.mocked(createPasskey).mockResolvedValue({ attestation: "resp" } as never);
    vi.mocked(api.passkey.registerVerify).mockResolvedValue(undefined);
    const user = userEvent.setup();
    renderSecurity();

    await screen.findByText("No passkeys registered yet.");
    await user.click(screen.getByRole("button", { name: "Add a passkey" }));

    const dialog = await screen.findByRole("dialog");
    vi.mocked(api.passkey.list).mockResolvedValue([passkey]);
    await user.click(within(dialog).getByRole("button", { name: "Add" }));

    expect(await screen.findByText("Passkey added")).toBeInTheDocument();
    expect(api.passkey.registerVerify).toHaveBeenCalledWith("ck1", { attestation: "resp" }, undefined);
  });

  it("shows the issued JWT before the post-create token-list refresh resolves, and clears it on dismiss", async () => {
    vi.mocked(api.mcpTokens.create).mockResolvedValue({ id: "t2", name: "New Token", jwt: "eyJhbGciOiJIUzI1NiJ9.secret.sig" });
    // The initial mount load resolves immediately (empty list); the refresh triggered by Create
    // token is held open deliberately so we can prove the JWT is already on screen before it
    // settles, not withheld behind it.
    let listCalls = 0;
    let resolveRefresh!: (tokens: McpTokenInfo[]) => void;
    vi.mocked(api.mcpTokens.list).mockImplementation(() => {
      listCalls += 1;
      if (listCalls === 1) return Promise.resolve([]);
      return new Promise((resolve) => { resolveRefresh = resolve; });
    });
    const user = userEvent.setup();
    renderSecurity();

    await screen.findByText("No MCP tokens yet.");
    await user.type(screen.getByLabelText('Name this token (e.g. "Claude Desktop")'), "New Token");
    await user.click(screen.getByRole("button", { name: "Create token" }));

    // The refresh call is still pending here -- the JWT must already be visible.
    expect(await screen.findByText("eyJhbGciOiJIUzI1NiJ9.secret.sig")).toBeInTheDocument();
    expect(screen.queryByText("Token created")).not.toBeInTheDocument();

    resolveRefresh([mcpToken]);
    expect(await screen.findByText("Token created")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "I've copied it" }));

    expect(screen.queryByText("eyJhbGciOiJIUzI1NiJ9.secret.sig")).not.toBeInTheDocument();
  });

  it("shows an error without blocking the MCP token form when token creation fails", async () => {
    vi.mocked(api.mcpTokens.create).mockRejectedValue(new Error("token limit reached"));
    const user = userEvent.setup();
    renderSecurity();

    await screen.findByText("No MCP tokens yet.");
    await user.type(screen.getByLabelText('Name this token (e.g. "Claude Desktop")'), "New Token");
    await user.click(screen.getByRole("button", { name: "Create token" }));

    expect(await screen.findByText("token limit reached")).toBeInTheDocument();
    expect(screen.queryByText(/it will not be shown again/)).not.toBeInTheDocument();
  });

  it("enrolls in 2FA end-to-end and clears the recovery codes on dismiss", async () => {
    vi.mocked(api.totp.enroll).mockResolvedValue({
      pendingKey: "pk1",
      secret: "JBSWY3DPEHPK3PXP",
      otpAuthUri: "otpauth://totp/PartnerCenterBridge:j%40example.com"
    });
    vi.mocked(api.totp.verifyEnroll).mockResolvedValue({ recoveryCodes: ["code-1", "code-2"] });
    const user = userEvent.setup();
    const { onProfileChanged } = renderSecurity();

    await user.click(screen.getByRole("button", { name: "Enable 2FA" }));

    expect(await screen.findByText("JBSWY3DPEHPK3PXP")).toBeInTheDocument();

    await user.type(screen.getByLabelText("Enter the 6-digit code it shows"), "123456");
    await user.click(screen.getByRole("button", { name: "Confirm and enable" }));

    expect(await screen.findByText("2FA enabled")).toBeInTheDocument();
    expect(api.totp.verifyEnroll).toHaveBeenCalledWith("pk1", "123456");
    expect(onProfileChanged).toHaveBeenCalled();
    // The secret must not still be on screen once enrollment has moved on to showing the
    // one-time recovery codes.
    expect(screen.queryByText("JBSWY3DPEHPK3PXP")).not.toBeInTheDocument();
    expect(screen.getByText(/code-1\s+code-2/)).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "I've saved these codes" }));

    expect(screen.queryByText(/code-1\s+code-2/)).not.toBeInTheDocument();
  });
});
