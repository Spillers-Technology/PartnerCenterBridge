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
vi.mock("../webauthn", () => ({
  passkeysSupported: true,
  getPasskey: vi.fn(),
  conditionalMediationSupported: vi.fn().mockResolvedValue(true)
}));
vi.mock("../session", () => ({ setLocalToken: vi.fn() }));

import { api } from "../api";
import { getPasskey } from "../webauthn";

function deferred<T>() {
  let resolve!: (v: T) => void;
  const promise = new Promise<T>((res) => {
    resolve = res;
  });
  return { promise, resolve };
}

function flushMicrotasks() {
  return new Promise((r) => setTimeout(r, 0));
}

describe("Login conditional passkey autofill", () => {
  beforeEach(() => vi.clearAllMocks());

  it("marks the email field for WebAuthn autofill and signs in silently when the browser offers a saved passkey", async () => {
    vi.mocked(api.passkey.loginOptions).mockResolvedValue({
      challengeKey: "ck1",
      options: { challenge: "chal" }
    } as never);
    vi.mocked(getPasskey).mockResolvedValue({ id: "cred1" } as never);
    vi.mocked(api.passkey.loginVerify).mockResolvedValue({ accessToken: "tok", user: { id: "u1" } as never });

    const onAuthenticated = vi.fn();
    render(
      <ThemeProvider theme={theme}>
        <Login onAuthenticated={onAuthenticated} onGoRegister={vi.fn()} />
      </ThemeProvider>
    );

    expect(screen.getByLabelText("Email")).toHaveAttribute("autocomplete", "username webauthn");

    await vi.waitFor(() => expect(onAuthenticated).toHaveBeenCalledWith({ accessToken: "tok", user: { id: "u1" } }));

    expect(getPasskey).toHaveBeenCalledWith(
      { challenge: "chal" },
      expect.objectContaining({ mediation: "conditional" })
    );
  });

  it("does not surface an error if no passkey is selected from the autofill dropdown", async () => {
    vi.mocked(api.passkey.loginOptions).mockResolvedValue({
      challengeKey: "ck1",
      options: { challenge: "chal" }
    } as never);
    vi.mocked(getPasskey).mockRejectedValue(new Error("No passkey was selected."));

    render(
      <ThemeProvider theme={theme}>
        <Login onAuthenticated={vi.fn()} onGoRegister={vi.fn()} />
      </ThemeProvider>
    );

    await vi.waitFor(() => expect(getPasskey).toHaveBeenCalled());
    expect(screen.queryByText("No passkey was selected.")).not.toBeInTheDocument();
  });

  it("abandons a still-pending conditional passkey attempt once the user submits the password form, instead of racing it", async () => {
    const optionsDeferred = deferred<{ challengeKey: string; options: unknown }>();
    vi.mocked(api.passkey.loginOptions).mockReturnValue(optionsDeferred.promise as never);
    vi.mocked(api.auth.login).mockResolvedValue({ accessToken: "pwtok", user: { id: "u2" } as never });

    const onAuthenticated = vi.fn();
    const user = userEvent.setup();
    render(
      <ThemeProvider theme={theme}>
        <Login onAuthenticated={onAuthenticated} onGoRegister={vi.fn()} />
      </ThemeProvider>
    );

    // The conditional effect is now parked awaiting loginOptions() -- submit the password form
    // before it resolves, the same shape of race the passkey-conditional-UI review flagged.
    await user.type(screen.getByLabelText("Email"), "a@b.com");
    await user.type(screen.getByLabelText("Password"), "hunter2222222");
    await user.click(screen.getByRole("button", { name: "Sign in" }));

    await vi.waitFor(() => expect(onAuthenticated).toHaveBeenCalledWith({ accessToken: "pwtok", user: { id: "u2" } }));

    // Now let the parked conditional flow proceed -- it must recognize it was aborted and never
    // reach getPasskey, rather than completing sign-in a second time for a different outcome.
    optionsDeferred.resolve({ challengeKey: "ck1", options: { challenge: "chal" } });
    await flushMicrotasks();

    expect(getPasskey).not.toHaveBeenCalled();
    expect(onAuthenticated).toHaveBeenCalledTimes(1);
  });

  it("does not authenticate from a stale conditional response that resolves after the component unmounts", async () => {
    vi.mocked(api.passkey.loginOptions).mockResolvedValue({
      challengeKey: "ck1",
      options: { challenge: "chal" }
    } as never);
    vi.mocked(getPasskey).mockResolvedValue({ id: "cred1" } as never);
    const verifyDeferred = deferred<{ accessToken: string; user: unknown }>();
    vi.mocked(api.passkey.loginVerify).mockReturnValue(verifyDeferred.promise as never);

    const onAuthenticated = vi.fn();
    const { unmount } = render(
      <ThemeProvider theme={theme}>
        <Login onAuthenticated={onAuthenticated} onGoRegister={vi.fn()} />
      </ThemeProvider>
    );

    await vi.waitFor(() => expect(api.passkey.loginVerify).toHaveBeenCalled());
    unmount();

    // The verify call was already in flight when the user navigated away -- its late completion
    // must not write a token or call onAuthenticated for a page nobody is looking at anymore.
    verifyDeferred.resolve({ accessToken: "tok", user: { id: "u1" } });
    await flushMicrotasks();

    expect(onAuthenticated).not.toHaveBeenCalled();
  });
});
