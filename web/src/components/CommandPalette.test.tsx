import { useState } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { mockMatchMedia, renderWithRouter } from "../test/renderWithRouter";
import { CommandPalette, CommandPaletteTrigger, useCommandPaletteShortcut } from "./CommandPalette";
import type { WorkflowRunRecord } from "../types";

vi.mock("../api", () => ({
  api: {
    tenants: { list: vi.fn() },
    workflows: { list: vi.fn(), runs: vi.fn() }
  }
}));

import { api } from "../api";

const RUN: WorkflowRunRecord = {
  id: "r1", workflowId: "access-parity", workflowName: "Access parity", tenantId: "t1", tenantName: "Contoso Ltd",
  kind: "Apply", operator: "tech", inputs: {}, findings: [], steps: [], succeeded: true,
  startedAt: "2026-09-01T10:00:00Z", durationMs: 10, outcome: "Succeeded", targetId: "u2", targetDisplayName: "Grace Hopper"
};

function Harness() {
  const [open, setOpen] = useState(false);
  useCommandPaletteShortcut(setOpen);
  return (
    <>
      <input aria-label="Some field" />
      <CommandPaletteTrigger onOpen={() => setOpen(true)} />
      <CommandPalette open={open} onClose={() => setOpen(false)} />
    </>
  );
}

const location = () => screen.getByTestId("location").textContent;

describe("CommandPalette", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockMatchMedia((q) => q.includes("min-width"));
    vi.mocked(api.tenants.list).mockResolvedValue([{ id: "t1", tenantId: "aaaa", displayName: "Contoso Ltd", defaultDomain: "contoso.com", status: "Active" }]);
    vi.mocked(api.workflows.list).mockResolvedValue([{ id: "mfa-reset", name: "MFA reset", description: "", category: "Identity", inputs: [] }]);
    vi.mocked(api.workflows.runs).mockResolvedValue([RUN]);
  });

  it("opens with Ctrl+K and closes with Esc", async () => {
    const user = userEvent.setup();
    renderWithRouter(<Harness />);
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    await user.keyboard("{Control>}k{/Control}");
    const dialog = await screen.findByRole("dialog", { name: "Command palette" });
    expect(within(dialog).getByRole("combobox", { name: "Search commands" })).toHaveFocus();
    expect(within(dialog).getByRole("listbox", { name: "Commands" })).toBeInTheDocument();
    await user.keyboard("{Escape}");
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
  });

  it("also opens from the header affordance and with Cmd+K", async () => {
    const user = userEvent.setup();
    renderWithRouter(<Harness />);
    await user.click(screen.getByRole("button", { name: "Search and jump (Ctrl+K)" }));
    expect(await screen.findByRole("dialog", { name: "Command palette" })).toBeInTheDocument();
    await user.keyboard("{Escape}");
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    await user.keyboard("{Meta>}k{/Meta}");
    expect(await screen.findByRole("dialog", { name: "Command palette" })).toBeInTheDocument();
  });

  it("does not react to plain typing in a text field", async () => {
    const user = userEvent.setup();
    renderWithRouter(<Harness />);
    await user.type(screen.getByLabelText("Some field"), "k");
    expect(screen.getByLabelText("Some field")).toHaveValue("k");
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("filters commands, including tenants, known fixes and a person search", async () => {
    const user = userEvent.setup();
    renderWithRouter(<Harness />);
    await user.keyboard("{Control>}k{/Control}");
    const dialog = await screen.findByRole("dialog", { name: "Command palette" });
    await waitFor(() => expect(within(dialog).getByRole("option", { name: /Access parity - Grace Hopper/ })).toBeInTheDocument());

    await user.type(within(dialog).getByRole("combobox"), "cont");
    const options = within(dialog).getAllByRole("option").map((o) => o.textContent);
    expect(options[0]).toBe("Find person: cont");
    expect(options).toContain("Open tenant: Contoso Ltd");
    expect(options).toContain("Contracts");
    expect(options).not.toContain("Home");

    await user.clear(within(dialog).getByRole("combobox"));
    await user.type(within(dialog).getByRole("combobox"), "mfa");
    expect(within(dialog).getByRole("option", { name: "Run known fix: MFA reset" })).toBeInTheDocument();
  });

  it("moves with the arrow keys and opens the highlighted command with Enter", async () => {
    const user = userEvent.setup();
    renderWithRouter(<Harness />);
    await user.keyboard("{Control>}k{/Control}");
    const dialog = await screen.findByRole("dialog", { name: "Command palette" });
    const input = within(dialog).getByRole("combobox");
    await user.type(input, "approvals");
    expect(within(dialog).getAllByRole("option")[0]).toHaveAttribute("aria-selected", "true");
    await user.keyboard("{ArrowDown}");
    const approvals = within(dialog).getByRole("option", { name: "Approvals" });
    expect(approvals).toHaveAttribute("aria-selected", "true");
    expect(input).toHaveAttribute("aria-activedescendant", approvals.id);
    await user.keyboard("{Enter}");
    await waitFor(() => expect(location()).toBe("/activity/approvals"));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
  });

  it("searches people for the typed text", async () => {
    const user = userEvent.setup();
    renderWithRouter(<Harness />);
    await user.keyboard("{Control>}k{/Control}");
    const dialog = await screen.findByRole("dialog", { name: "Command palette" });
    await user.type(within(dialog).getByRole("combobox"), "ada lovelace{Enter}");
    await waitFor(() => expect(location()).toBe("/people?q=ada+lovelace"));
  });

  it("opens a recent run's evidence", async () => {
    const user = userEvent.setup();
    renderWithRouter(<Harness />);
    await user.keyboard("{Control>}k{/Control}");
    const dialog = await screen.findByRole("dialog", { name: "Command palette" });
    await user.click(await within(dialog).findByRole("option", { name: /Access parity - Grace Hopper/ }));
    await waitFor(() => expect(location()).toBe("/activity/runs/r1"));
  });
});
