import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ThemeProvider } from "@mui/material/styles";
import { theme } from "../theme";
import { Register } from "./Register";

vi.mock("../api", () => ({ api: { auth: { register: vi.fn() } } }));
vi.mock("../session", () => ({ setLocalToken: vi.fn() }));

import { api } from "../api";

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
});
