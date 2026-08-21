import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ThemeProvider } from "@mui/material/styles";
import { theme } from "../theme";
import { ConfirmDialogProvider } from "../hooks/useConfirm";
import { ToastProvider } from "../hooks/useToast";
import { Tenants } from "./Tenants";
import type { Contract, MeProfile, Tenant, TenantGrant } from "../types";

vi.mock("../api", () => ({
  api: {
    tenants: {
      list: vi.fn(),
      sync: vi.fn(),
      create: vi.fn(),
      setContract: vi.fn()
    },
    contracts: {
      list: vi.fn()
    },
    tenantAccess: {
      list: vi.fn(),
      grant: vi.fn(),
      revoke: vi.fn()
    }
  }
}));

import { api } from "../api";

const tenant1: Tenant = {
  id: "t1",
  tenantId: "guid-1",
  displayName: "Contoso",
  defaultDomain: "contoso.com",
  status: "Active",
  contractId: undefined
};

const grant1: TenantGrant = {
  userId: "u2",
  email: "teammate@example.com",
  role: "Viewer",
  grantedAt: "2026-01-01T00:00:00Z"
};

const ownerMe: MeProfile = {
  id: "u1",
  email: "me@example.com",
  displayName: "Me",
  isSystemAdmin: false,
  totpEnabled: false,
  tenantAccess: [{ tenantId: "t1", tenantName: "Contoso", role: "Owner" }]
};

function renderTenants(me: MeProfile | null = null) {
  const onProfileChanged = vi.fn();
  render(
    <ThemeProvider theme={theme}>
      <ToastProvider>
        <ConfirmDialogProvider>
          <Tenants me={me} onProfileChanged={onProfileChanged} />
        </ConfirmDialogProvider>
      </ToastProvider>
    </ThemeProvider>
  );
  return { onProfileChanged };
}

describe("Tenants", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.tenants.list).mockResolvedValue([]);
    vi.mocked(api.contracts.list).mockResolvedValue([]);
    vi.mocked(api.tenants.sync).mockResolvedValue([]);
    vi.mocked(api.tenants.create).mockResolvedValue(tenant1);
    vi.mocked(api.tenants.setContract).mockResolvedValue(undefined);
    vi.mocked(api.tenantAccess.list).mockResolvedValue([]);
    vi.mocked(api.tenantAccess.grant).mockResolvedValue(undefined);
    vi.mocked(api.tenantAccess.revoke).mockResolvedValue(undefined);
  });

  it("renders the tenants list with name, domain and status", async () => {
    vi.mocked(api.tenants.list).mockResolvedValue([tenant1]);
    renderTenants();

    expect(await screen.findByText("Contoso")).toBeInTheDocument();
    expect(screen.getByText("contoso.com")).toBeInTheDocument();
    expect(screen.getByText("Active")).toBeInTheDocument();
  });

  it("syncs from Partner Center and reloads the list", async () => {
    vi.mocked(api.tenants.list).mockResolvedValueOnce([]).mockResolvedValueOnce([tenant1]);
    const user = userEvent.setup();
    const { onProfileChanged } = renderTenants();

    await screen.findByText("No tenants yet. Sync from Partner Center or add one below.");

    await user.click(screen.getByRole("button", { name: "Sync from Partner Center" }));

    expect(await screen.findByText("Contoso")).toBeInTheDocument();
    expect(api.tenants.sync).toHaveBeenCalled();
    expect(onProfileChanged).toHaveBeenCalled();
    expect(await screen.findByText("Synced from Partner Center")).toBeInTheDocument();
  });

  it("adds a tenant via the add-tenant form", async () => {
    const user = userEvent.setup();
    const { onProfileChanged } = renderTenants();

    await screen.findByText("No tenants yet. Sync from Partner Center or add one below.");

    await user.type(screen.getByLabelText("Entra tenant id (GUID)"), "guid-9");
    await user.type(screen.getByLabelText("Display name"), "Fabrikam");
    await user.type(screen.getByLabelText("Default domain (optional)"), "fabrikam.com");
    await user.click(screen.getByRole("button", { name: "Add tenant" }));

    await waitFor(() =>
      expect(api.tenants.create).toHaveBeenCalledWith("guid-9", "Fabrikam", "fabrikam.com")
    );
    expect(onProfileChanged).toHaveBeenCalled();
    expect(await screen.findByText("Fabrikam added")).toBeInTheDocument();
  });

  it("shows an error alert when loading tenants fails", async () => {
    vi.mocked(api.tenants.list).mockRejectedValue(new Error("boom"));
    renderTenants();

    expect(await screen.findByText("boom")).toBeInTheDocument();
  });

  it("gates revoke behind confirm: cancel does not call the API, confirm does", async () => {
    vi.mocked(api.tenants.list).mockResolvedValue([tenant1]);
    vi.mocked(api.contracts.list).mockResolvedValue([] as Contract[]);
    vi.mocked(api.tenantAccess.list).mockResolvedValue([grant1]);
    const user = userEvent.setup();
    renderTenants(ownerMe);

    await screen.findByText("Contoso");
    await user.click(screen.getByRole("button", { name: "Share" }));

    await screen.findByText("Who has access to Contoso");
    await screen.findByText("teammate@example.com");

    // Cancel path: revoke must not be called.
    await user.click(screen.getByRole("button", { name: "Revoke" }));
    const dialog1 = await screen.findByRole("dialog");
    await user.click(within(dialog1).getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(api.tenantAccess.revoke).not.toHaveBeenCalled();

    // Confirm path: revoke must be called with the right args.
    await user.click(screen.getByRole("button", { name: "Revoke" }));
    const dialog2 = await screen.findByRole("dialog");
    await user.click(within(dialog2).getByRole("button", { name: "Revoke" }));

    await waitFor(() => expect(api.tenantAccess.revoke).toHaveBeenCalledWith("t1", "u2"));
    expect(await screen.findByText("Access revoked for teammate@example.com")).toBeInTheDocument();
  });

  it("clears a stale error from one action once a different action succeeds", async () => {
    // Regression test: errors from independent useAsyncAction instances used to be combined with
    // `??`, so an older failure could mask (or outlive) a newer, unrelated success. Each action's
    // error must now be scoped to whichever action was most recently attempted.
    vi.mocked(api.tenants.list).mockResolvedValue([]);
    vi.mocked(api.tenants.sync).mockRejectedValue(new Error("sync failed"));
    const user = userEvent.setup();
    renderTenants();

    await screen.findByText("No tenants yet. Sync from Partner Center or add one below.");

    await user.click(screen.getByRole("button", { name: "Sync from Partner Center" }));
    expect(await screen.findByText("sync failed")).toBeInTheDocument();

    await user.type(screen.getByLabelText("Entra tenant id (GUID)"), "guid-9");
    await user.type(screen.getByLabelText("Display name"), "Fabrikam");
    await user.click(screen.getByRole("button", { name: "Add tenant" }));

    await waitFor(() => expect(api.tenants.create).toHaveBeenCalled());
    expect(screen.queryByText("sync failed")).not.toBeInTheDocument();
  });
});
