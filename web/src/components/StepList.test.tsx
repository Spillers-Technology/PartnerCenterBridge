import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ThemeProvider } from "@mui/material/styles";
import { theme } from "../theme";
import { ToastProvider } from "../hooks/useToast";
import { StepList } from "./StepList";
import type { ProvisioningResult } from "../types";

const result: ProvisioningResult = {
  userId: "u1",
  userPrincipalName: "ada@contoso.com",
  initialPassword: "Sup3r$ecret!",
  steps: [{ name: "Create account", success: true, detail: "Created" }],
  succeeded: true
};

function renderStepList(r: ProvisioningResult) {
  return render(
    <ThemeProvider theme={theme}>
      <ToastProvider>
        <StepList result={r} />
      </ToastProvider>
    </ThemeProvider>
  );
}

describe("StepList", () => {
  it("renders the step table", () => {
    renderStepList(result);
    expect(screen.getByText("Create account")).toBeInTheDocument();
    expect(screen.getByText("Created")).toBeInTheDocument();
  });

  it("copies the shown-once temporary password to the clipboard", async () => {
    // Regression test: the temp password used to render as plain text with no copy action --
    // it's shown exactly once, so a user who needs it has to retype it by hand.
    const user = userEvent.setup();
    // user-event's own setup() installs its own navigator.clipboard stub (to support user.copy()
    // etc.) -- define after setup() so this mock isn't the one that gets clobbered.
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, "clipboard", { value: { writeText }, configurable: true });
    renderStepList(result);

    expect(screen.getByText("Sup3r$ecret!")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Copy" }));

    expect(writeText).toHaveBeenCalledWith("Sup3r$ecret!");
    expect(await screen.findByText("Copied")).toBeInTheDocument();
  });

  it("does not render a Copy button when there is no temporary password to show", () => {
    renderStepList({ ...result, initialPassword: undefined });
    expect(screen.queryByRole("button", { name: "Copy" })).not.toBeInTheDocument();
  });
});
