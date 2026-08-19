import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ThemeProvider } from "@mui/material/styles";
import { theme } from "../theme";
import { ConfirmDialogProvider, useConfirm } from "./useConfirm";

function Harness({ onResult }: { onResult: (v: boolean) => void }) {
  const confirm = useConfirm();
  return (
    <button
      onClick={async () =>
        onResult(await confirm({ title: "Remove passkey?", message: "This cannot be undone.", destructive: true }))
      }
    >
      Ask
    </button>
  );
}

function renderHarness(onResult: (v: boolean) => void) {
  return render(
    <ThemeProvider theme={theme}>
      <ConfirmDialogProvider>
        <Harness onResult={onResult} />
      </ConfirmDialogProvider>
    </ThemeProvider>
  );
}

describe("useConfirm / ConfirmDialogProvider", () => {
  it("resolves true when the confirm button is clicked", async () => {
    const user = userEvent.setup();
    const results: boolean[] = [];
    renderHarness((v) => results.push(v));

    await user.click(screen.getByRole("button", { name: "Ask" }));
    expect(await screen.findByText("Remove passkey?")).toBeInTheDocument();
    expect(screen.getByText("This cannot be undone.")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Confirm" }));
    expect(results).toEqual([true]);
  });

  it("resolves false when cancelled, and custom labels render", async () => {
    const user = userEvent.setup();
    const results: boolean[] = [];

    function CustomHarness() {
      const confirm = useConfirm();
      return (
        <button
          onClick={async () =>
            results.push(
              await confirm({ title: "Deploy?", message: "To 4 tenants.", confirmLabel: "Deploy", cancelLabel: "Not now" })
            )
          }
        >
          Ask
        </button>
      );
    }

    render(
      <ThemeProvider theme={theme}>
        <ConfirmDialogProvider>
          <CustomHarness />
        </ConfirmDialogProvider>
      </ThemeProvider>
    );

    await user.click(screen.getByRole("button", { name: "Ask" }));
    await screen.findByText("Deploy?");
    await user.click(screen.getByRole("button", { name: "Not now" }));
    expect(results).toEqual([false]);
  });

  it("throws when used outside the provider", () => {
    function Bare() {
      useConfirm();
      return null;
    }
    expect(() => render(<Bare />)).toThrow("useConfirm must be used within a ConfirmDialogProvider");
  });
});
