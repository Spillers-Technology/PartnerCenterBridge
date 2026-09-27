import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import { ConfirmDialogProvider } from "../hooks/useConfirm";
import userEvent from "@testing-library/user-event";
import { ThemeProvider } from "@mui/material/styles";
import { theme } from "../theme";
import { Register } from "./Register";

vi.mock("../api", () => ({ api: { auth: { register: vi.fn(), setupNoAccount: vi.fn() } } }));
vi.mock("../session", () => ({ setLocalToken: vi.fn() }));

import { api } from "../api";
import { setLocalToken } from "../session";

function renderRegister() {
  const onAuthenticated = vi.fn();
  render(
    <ThemeProvider theme={theme}>
      <Register onAuthenticated={onAuthenticated} onGoLogin={vi.fn()} />
    </ThemeProvider>
  );
  return onAuthenticated;
}

describe("Register", () => {
  beforeEach(() => vi.clearAllMocks());

  it("registers and calls onAuthenticated", async () => {
    vi.mocked(api.auth.register).mockResolvedValue({ accessToken: "tok", user: { id: "u1" } as never });
    const user = userEvent.setup();
    const onAuthenticated = renderRegister();

    await user.type(screen.getByLabelText("Display name"), "Maya Chen");
    await user.type(screen.getByLabelText("Email"), "maya@contoso.com");
    await user.type(screen.getByLabelText("Password (12+ characters)"), "correct-horse-battery");
    await user.click(screen.getByRole("button", { name: "Create account" }));

    expect(api.auth.register).toHaveBeenCalledWith("maya@contoso.com", "correct-horse-battery", "Maya Chen");
    expect(setLocalToken).toHaveBeenCalledWith("tok");
    expect(onAuthenticated).toHaveBeenCalledWith({ accessToken: "tok", user: { id: "u1" } });
  });

  it("shows an error alert when registration fails", async () => {
    vi.mocked(api.auth.register).mockRejectedValue(new Error("email already registered"));
    const user = userEvent.setup();
    renderRegister();

    await user.type(screen.getByLabelText("Display name"), "Maya Chen");
    await user.type(screen.getByLabelText("Email"), "maya@contoso.com");
    await user.type(screen.getByLabelText("Password (12+ characters)"), "correct-horse-battery");
    await user.click(screen.getByRole("button", { name: "Create account" }));

    expect(await screen.findByText("email already registered")).toBeInTheDocument();
  });

  it("first run offers both choices; skipping needs a confirmation", async () => {
    vi.mocked(api.auth.setupNoAccount).mockResolvedValue({ accessToken: "owner", user: { id: "o1", isWorkbenchOwner: true } as never });
    const user = userEvent.setup();
    const onAuthenticated = vi.fn();
    render(
      <ThemeProvider theme={theme}>
        <ConfirmDialogProvider>
          <Register setup skipAccount={{ windowsUser: "maya" }} onAuthenticated={onAuthenticated} onGoLogin={vi.fn()} />
        </ConfirmDialogProvider>
      </ThemeProvider>
    );

    expect(screen.getByRole("button", { name: "Create administrator account" })).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Skip -- use without an account on this computer" }));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText(/Anyone who can run programs as maya on this computer/)).toBeInTheDocument();
    // Destructive-style: Cancel is focused, so a stray Enter does not choose it.
    expect(within(dialog).getByRole("button", { name: "Cancel" })).toHaveFocus();
    expect(api.auth.setupNoAccount).not.toHaveBeenCalled();
    await user.click(within(dialog).getByRole("button", { name: "Use without an account" }));

    await waitFor(() => expect(api.auth.setupNoAccount).toHaveBeenCalledTimes(1));
    expect(setLocalToken).toHaveBeenCalledWith("owner");
    expect(onAuthenticated).toHaveBeenCalled();
  });

  it("does not offer skipping outside first run", () => {
    renderRegister();
    expect(screen.queryByRole("button", { name: /Skip/ })).not.toBeInTheDocument();
  });
});

describe("Register on a Local Workbench first run", () => {
  beforeEach(() => vi.clearAllMocks());

  it("shows guidance instead of the form without the exe's setup ticket", () => {
    render(
      <ThemeProvider theme={theme}>
        <Register setup setupTicketRequired skipAccount={{ windowsUser: "maya" }} onAuthenticated={vi.fn()} onGoLogin={vi.fn()} />
      </ThemeProvider>
    );
    expect(screen.getByText("Open Partner Center Bridge from PartnerCenterBridge.exe to finish setup.")).toBeInTheDocument();
    expect(screen.queryByLabelText("Email")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Skip/ })).not.toBeInTheDocument();
  });

  it("sends the setup ticket with the first account and with the no-account choice", async () => {
    vi.mocked(api.auth.register).mockResolvedValue({ accessToken: "tok", user: { id: "u1" } as never });
    vi.mocked(api.auth.setupNoAccount).mockResolvedValue({ accessToken: "owner", user: { id: "o1" } as never });
    const user = userEvent.setup();
    render(
      <ThemeProvider theme={theme}>
        <ConfirmDialogProvider>
          <Register setup setupTicketRequired setupTicket="tkt" skipAccount={{ windowsUser: "maya" }} onAuthenticated={vi.fn()} onGoLogin={vi.fn()} />
        </ConfirmDialogProvider>
      </ThemeProvider>
    );

    await user.type(screen.getByLabelText("Display name"), "Maya Chen");
    await user.type(screen.getByLabelText("Email"), "maya@contoso.com");
    await user.type(screen.getByLabelText("Password (12+ characters)"), "correct-horse-battery");
    await user.click(screen.getByRole("button", { name: "Create administrator account" }));
    expect(api.auth.register).toHaveBeenCalledWith("maya@contoso.com", "correct-horse-battery", "Maya Chen", "tkt");

    await user.click(screen.getByRole("button", { name: "Skip -- use without an account on this computer" }));
    await user.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Use without an account" }));
    await waitFor(() => expect(api.auth.setupNoAccount).toHaveBeenCalledWith("tkt"));
  });
});
