import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ThemeProvider } from "@mui/material/styles";
import { api } from "../api";
import { ToastProvider } from "../hooks/useToast";
import { theme } from "../theme";
import type { InstanceUser, MeProfile } from "../types";
import { InstanceAccessCard } from "./InstanceAccessCard";

vi.mock("../api", () => ({
  api: {
    instanceAccess: {
      list: vi.fn(),
      replaceRoles: vi.fn()
    }
  }
}));

const me: MeProfile = {
  id: "admin",
  email: "admin@example.com",
  displayName: "Admin",
  isSystemAdmin: true,
  totpEnabled: false,
  tenantAccess: [],
  instanceRoles: ["Administrator"],
  instancePermissions: ["instance.roles.manage"],
  authorizationVersion: 1
};

const users: InstanceUser[] = [
  {
    id: "admin", email: "admin@example.com", displayName: "Admin", isActive: true,
    roles: ["Administrator"], authorizationVersion: 1
  },
  {
    id: "target", email: "target@example.com", displayName: "Target", isActive: true,
    roles: [], authorizationVersion: 4
  }
];

function renderCard() {
  render(
    <ThemeProvider theme={theme}>
      <ToastProvider><InstanceAccessCard me={me} /></ToastProvider>
    </ThemeProvider>
  );
}

describe("InstanceAccessCard", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.instanceAccess.list).mockResolvedValue(users);
  });

  it("disables self-management and saves an exact delegated-role replacement", async () => {
    const updated: InstanceUser = { ...users[1], roles: ["CatalogManager"], authorizationVersion: 5 };
    vi.mocked(api.instanceAccess.replaceRoles).mockResolvedValue(updated);
    const user = userEvent.setup();
    renderCard();

    const adminRow = (await screen.findByText("admin@example.com")).closest("tr")!;
    expect(adminRow.querySelectorAll("input:disabled").length).toBeGreaterThan(0);

    const targetRow = screen.getByText("target@example.com").closest("tr")!;
    await user.click(withinRow(targetRow, "Catalog"));
    await user.click(withinButton(targetRow, "Save"));

    await waitFor(() => expect(api.instanceAccess.replaceRoles)
      .toHaveBeenCalledWith("target", ["CatalogManager"], 4));
    expect(await screen.findByText("Instance access updated for target@example.com")).toBeInTheDocument();
  });

  it("shows a stale-editor response and keeps the draft available", async () => {
    vi.mocked(api.instanceAccess.replaceRoles).mockRejectedValue(
      new Error("412 Precondition Failed: Refresh and try again."));
    const user = userEvent.setup();
    renderCard();

    const targetRow = (await screen.findByText("target@example.com")).closest("tr")!;
    await user.click(withinRow(targetRow, "SAM credentials"));
    await user.click(withinButton(targetRow, "Save"));

    expect(await screen.findByText("412 Precondition Failed: Refresh and try again.")).toBeInTheDocument();
    expect(withinRow(targetRow, "SAM credentials")).toBeChecked();
  });
});

function withinRow(row: HTMLTableRowElement, label: string): HTMLInputElement {
  const labels = Array.from(row.querySelectorAll("label"));
  const match = labels.find((candidate) => candidate.textContent?.includes(label));
  if (!match) throw new Error(`No checkbox labelled ${label}`);
  return match.querySelector("input")!;
}

function withinButton(row: HTMLTableRowElement, label: string): HTMLButtonElement {
  const match = Array.from(row.querySelectorAll("button"))
    .find((candidate) => candidate.textContent?.includes(label));
  if (!match) throw new Error(`No button labelled ${label}`);
  return match;
}
