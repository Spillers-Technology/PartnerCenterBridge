import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { MicrosoftConnections } from "./MicrosoftConnections";
import { api } from "../api";

vi.mock("../api", () => ({
  api: { microsoftConnections: { list: vi.fn(), connect: vi.fn() } },
  errorText: (error: Error) => error.message
}));
const connected = { available: true, configured: true, connections: [
  { id: "local-tenant", tenantId: "directory-id", displayName: "Contoso", username: "admin@contoso.com", reconnectRequired: true }
] };
beforeEach(() => { vi.resetAllMocks(); vi.mocked(api.microsoftConnections.list).mockResolvedValue(connected); });

describe("Microsoft connections", () => {
  it("adds a tenant through Microsoft and refreshes the tenant list", async () => {
    const changed = vi.fn();
    render(<MicrosoftConnections onConnected={changed} />);
    fireEvent.click(await screen.findByRole("button", { name: "Add tenant with Microsoft" }));
    await waitFor(() => expect(api.microsoftConnections.connect).toHaveBeenCalledWith(undefined));
    await waitFor(() => expect(changed).toHaveBeenCalledOnce());
  });
  it("reconnects the selected tenant and shows the remembered username", async () => {
    render(<MicrosoftConnections onConnected={vi.fn()} />);
    expect(await screen.findByText("admin@contoso.com")).toBeVisible();
    expect(screen.getByText("Microsoft needs you to sign in again.")).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: "Reconnect" }));
    await waitFor(() => expect(api.microsoftConnections.connect).toHaveBeenCalledWith("local-tenant"));
  });
  it("keeps existing connections and allows retry after a cancelled sign-in", async () => {
    vi.mocked(api.microsoftConnections.connect).mockRejectedValue(new Error("Sign-in cancelled"));
    const changed = vi.fn();
    render(<MicrosoftConnections onConnected={changed} />);
    fireEvent.click(await screen.findByRole("button", { name: "Add tenant with Microsoft" }));
    expect(await screen.findByText("Sign-in cancelled")).toBeVisible();
    expect(screen.getByText("Contoso")).toBeVisible();
    expect(screen.getByRole("button", { name: "Add tenant with Microsoft" })).toBeEnabled();
    expect(changed).not.toHaveBeenCalled();
  });
  it("explains the one-time configuration before enabling sign-in", async () => {
    vi.mocked(api.microsoftConnections.list).mockResolvedValue({ ...connected, configured: false, connections: [] });
    render(<MicrosoftConnections onConnected={vi.fn()} />);
    expect(await screen.findByRole("button", { name: "Add tenant with Microsoft" })).toBeDisabled();
    expect(screen.getByText(/Set MicrosoftSignIn:ClientId/)).toBeVisible();
  });
});
