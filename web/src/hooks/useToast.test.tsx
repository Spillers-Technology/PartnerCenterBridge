import { describe, expect, it } from "vitest";
import { render, screen, waitForElementToBeRemoved } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ThemeProvider } from "@mui/material/styles";
import { theme } from "../theme";
import { ToastProvider, useToast } from "./useToast";

function Harness() {
  const toast = useToast();
  return <button onClick={() => toast("Deployment succeeded", "success")}>Notify</button>;
}

describe("useToast / ToastProvider", () => {
  it("shows a toast and dismisses it on close", async () => {
    const user = userEvent.setup();
    render(
      <ThemeProvider theme={theme}>
        <ToastProvider>
          <Harness />
        </ToastProvider>
      </ThemeProvider>
    );

    await user.click(screen.getByRole("button", { name: "Notify" }));
    expect(await screen.findByText("Deployment succeeded")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: /close/i }));
    await waitForElementToBeRemoved(() => screen.queryByText("Deployment succeeded"));
  });

  it("queues a second toast behind the first, showing them one at a time", async () => {
    const user = userEvent.setup();

    function TwoHarness() {
      const toast = useToast();
      return (
        <>
          <button onClick={() => toast("First", "info")}>One</button>
          <button onClick={() => toast("Second", "error")}>Two</button>
        </>
      );
    }

    render(
      <ThemeProvider theme={theme}>
        <ToastProvider>
          <TwoHarness />
        </ToastProvider>
      </ThemeProvider>
    );

    await user.click(screen.getByRole("button", { name: "One" }));
    await user.click(screen.getByRole("button", { name: "Two" }));

    expect(await screen.findByText("First")).toBeInTheDocument();
    expect(screen.queryByText("Second")).not.toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: /close/i }));
    expect(await screen.findByText("Second")).toBeInTheDocument();
  });

  it("throws when used outside the provider", () => {
    function Bare() {
      useToast();
      return null;
    }
    expect(() => render(<Bare />)).toThrow("useToast must be used within a ToastProvider");
  });
});
