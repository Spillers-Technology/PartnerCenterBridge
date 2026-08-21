import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ThemeProvider } from "@mui/material/styles";
import { theme } from "../theme";
import { ToastProvider } from "../hooks/useToast";
import { Contracts } from "./Contracts";
import type { Contract } from "../types";

vi.mock("../api", () => ({
  api: {
    contracts: {
      list: vi.fn(),
      create: vi.fn(),
      plan: vi.fn()
    }
  }
}));

import { api } from "../api";

const contract: Contract = {
  id: "c1",
  name: "Contoso baseline",
  notes: "Standard apps",
  tenantCount: 2,
  desiredAppCount: 3
};

function renderContracts() {
  render(
    <ThemeProvider theme={theme}>
      <ToastProvider>
        <Contracts />
      </ToastProvider>
    </ThemeProvider>
  );
}

describe("Contracts", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.contracts.list).mockResolvedValue([]);
    vi.mocked(api.contracts.create).mockResolvedValue(contract);
    vi.mocked(api.contracts.plan).mockResolvedValue([]);
  });

  it("renders the contracts list", async () => {
    vi.mocked(api.contracts.list).mockResolvedValue([contract]);
    renderContracts();

    expect(await screen.findByText("Contoso baseline")).toBeInTheDocument();
    expect(screen.getByText("2")).toBeInTheDocument();
    expect(screen.getByText("3")).toBeInTheDocument();
  });

  it("creates a contract and shows a success toast", async () => {
    const user = userEvent.setup();
    renderContracts();

    await user.type(await screen.findByLabelText("Contract name"), "Fabrikam baseline");
    await user.type(screen.getByLabelText("Notes (optional)"), "Pilot apps");
    await user.click(screen.getByRole("button", { name: "Add contract" }));

    await waitFor(() => expect(api.contracts.create).toHaveBeenCalledWith("Fabrikam baseline", "Pilot apps"));
    expect(await screen.findByText("Fabrikam baseline added")).toBeInTheDocument();
  });

  it("shows an error alert when loading contracts fails", async () => {
    vi.mocked(api.contracts.list).mockRejectedValue(new Error("boom"));
    renderContracts();

    expect(await screen.findByText("boom")).toBeInTheDocument();
  });

  it("renders a preview plan", async () => {
    vi.mocked(api.contracts.list).mockResolvedValue([contract]);
    vi.mocked(api.contracts.plan).mockResolvedValue([
      { tenantId: "t1", tenantName: "Contoso", templateId: "a1", templateName: "Defender", action: "Install" }
    ]);
    const user = userEvent.setup();
    renderContracts();

    await user.click(await screen.findByRole("button", { name: "Preview plan" }));

    expect(await screen.findByText("Reconcile plan (dry run)")).toBeInTheDocument();
    expect(screen.getByText("Contoso")).toBeInTheDocument();
    expect(screen.getByText("Defender")).toBeInTheDocument();
    expect(screen.getByText("Install")).toBeInTheDocument();
  });

  it("shows the empty preview plan state", async () => {
    vi.mocked(api.contracts.list).mockResolvedValue([contract]);
    const user = userEvent.setup();
    renderContracts();

    await user.click(await screen.findByRole("button", { name: "Preview plan" }));

    expect(await screen.findByText("Nothing to do.")).toBeInTheDocument();
  });

  it("shows an error when the plan preview fails", async () => {
    vi.mocked(api.contracts.list).mockResolvedValue([contract]);
    vi.mocked(api.contracts.plan).mockRejectedValue(new Error("plan boom"));
    const user = userEvent.setup();
    renderContracts();

    await user.click(await screen.findByRole("button", { name: "Preview plan" }));

    expect(await screen.findByText("plan boom")).toBeInTheDocument();
  });

  it("clears a previously shown plan once a new preview attempt fails", async () => {
    // Regression test: the plan table used to stay on screen showing a stale (and by then
    // unrelated) result if a later preview attempt failed, since `plan` state wasn't reset when a
    // new preview request started.
    vi.mocked(api.contracts.list).mockResolvedValue([contract]);
    vi.mocked(api.contracts.plan)
      .mockResolvedValueOnce([
        { tenantId: "t1", tenantName: "Contoso", templateId: "a1", templateName: "Defender", action: "Install" }
      ])
      .mockRejectedValueOnce(new Error("plan boom"));
    const user = userEvent.setup();
    renderContracts();

    const previewButton = await screen.findByRole("button", { name: "Preview plan" });
    await user.click(previewButton);
    expect(await screen.findByText("Defender")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Preview plan" }));
    expect(await screen.findByText("plan boom")).toBeInTheDocument();
    expect(screen.queryByText("Defender")).not.toBeInTheDocument();
  });
});
