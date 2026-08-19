import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ThemeProvider } from "@mui/material/styles";
import { theme } from "../theme";
import { Login } from "./Login";

vi.mock("../api", () => ({
  api: {
    auth: { login: vi.fn() },
    totp: { challenge: vi.fn() },
    passkey: { loginOptions: vi.fn(), loginVerify: vi.fn() }
  }
}));
vi.mock("../webauthn", () => ({ passkeysSupported: false, getPasskey: vi.fn() }));
vi.mock("../session", () => ({ setLocalToken: vi.fn() }));

import { api } from "../api";

function renderLogin() {
  const onAuthenticated = vi.fn();
  render(
    <ThemeProvider theme={theme}>
      <Login onAuthenticated={onAuthenticated} onGoRegister={vi.fn()} />
    </ThemeProvider>
  );
  return onAuthenticated;
}

describe("Login", () => {
  beforeEach(() => vi.clearAllMocks());

  it("signs in with email/password and calls onAuthenticated", async () => {
    vi.mocked(api.auth.login).mockResolvedValue({ accessToken: "tok", user: { id: "u1" } as never });
    const user = userEvent.setup();
    const onAuthenticated = renderLogin();

    await user.type(screen.getByLabelText("Email"), "a@b.com");
    await user.type(screen.getByLabelText("Password"), "hunter2222222");
    await user.click(screen.getByRole("button", { name: "Sign in" }));

    expect(onAuthenticated).toHaveBeenCalledWith({ accessToken: "tok", user: { id: "u1" } });
  });

  it("shows an error alert when sign-in fails", async () => {
    vi.mocked(api.auth.login).mockRejectedValue(new Error("bad credentials"));
    const user = userEvent.setup();
    renderLogin();

    await user.type(screen.getByLabelText("Email"), "a@b.com");
    await user.type(screen.getByLabelText("Password"), "wrong");
    await user.click(screen.getByRole("button", { name: "Sign in" }));

    expect(await screen.findByText("bad credentials")).toBeInTheDocument();
  });

  it("moves to the MFA step on a challenge response", async () => {
    vi.mocked(api.auth.login).mockResolvedValue({ mfaTicket: "ticket-1" });
    const user = userEvent.setup();
    renderLogin();

    await user.type(screen.getByLabelText("Email"), "a@b.com");
    await user.type(screen.getByLabelText("Password"), "hunter2222222");
    await user.click(screen.getByRole("button", { name: "Sign in" }));

    expect(await screen.findByLabelText("6-digit code (or a recovery code)")).toBeInTheDocument();
  });

  it("completes MFA and calls onAuthenticated", async () => {
    vi.mocked(api.auth.login).mockResolvedValue({ mfaTicket: "ticket-1" });
    vi.mocked(api.totp.challenge).mockResolvedValue({ accessToken: "tok2", user: { id: "u1" } as never });
    const user = userEvent.setup();
    const onAuthenticated = renderLogin();

    await user.type(screen.getByLabelText("Email"), "a@b.com");
    await user.type(screen.getByLabelText("Password"), "hunter2222222");
    await user.click(screen.getByRole("button", { name: "Sign in" }));
    await user.type(await screen.findByLabelText("6-digit code (or a recovery code)"), "123456");
    await user.click(screen.getByRole("button", { name: "Verify" }));

    expect(onAuthenticated).toHaveBeenCalledWith({ accessToken: "tok2", user: { id: "u1" } });
    expect(api.totp.challenge).toHaveBeenCalledWith("ticket-1", "123456");
  });
});
