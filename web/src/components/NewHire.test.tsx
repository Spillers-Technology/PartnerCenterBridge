import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ThemeProvider } from "@mui/material/styles";
import { theme } from "../theme";
import { ToastProvider } from "../hooks/useToast";
import { NewHire } from "./NewHire";
import type { DirectoryObject, ProvisioningResult, ProvisioningTemplate, Sku, Tenant } from "../types";

vi.mock("../api", () => ({
  api: {
    tenants: { list: vi.fn() },
    directory: { skus: vi.fn(), groups: vi.fn() },
    provisioning: { getTemplate: vi.fn(), hire: vi.fn() }
  }
}));

import { api } from "../api";

const tenant: Tenant = {
  id: "t1",
  tenantId: "guid-1",
  displayName: "Contoso",
  defaultDomain: "contoso.com",
  status: "Active"
};

const sku: Sku = { skuId: "sku-1", skuPartNumber: "M365_BUSINESS", enabled: 25, consumed: 4 };
const group: DirectoryObject = { id: "group-1", displayName: "New starters" };
const result: ProvisioningResult = {
  userId: "user-1",
  userPrincipalName: "ada@contoso.com",
  steps: [{ name: "Create account", success: true, detail: "Created" }],
  succeeded: true
};

function renderNewHire() {
  render(
    <ThemeProvider theme={theme}>
      <ToastProvider>
        <NewHire />
      </ToastProvider>
    </ThemeProvider>
  );
}

async function selectTenant(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByLabelText("Tenant"));
  await user.click(screen.getByRole("option", { name: "Contoso" }));
}

describe("NewHire", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.tenants.list).mockResolvedValue([tenant]);
    vi.mocked(api.directory.skus).mockResolvedValue([sku]);
    vi.mocked(api.directory.groups).mockResolvedValue([group]);
    vi.mocked(api.provisioning.getTemplate).mockResolvedValue(undefined);
    vi.mocked(api.provisioning.hire).mockResolvedValue(result);
  });

  it("reveals the form and loads SKUs and groups after selecting a tenant", async () => {
    const user = userEvent.setup();
    renderNewHire();

    await selectTenant(user);

    expect(await screen.findByLabelText("First name")).toBeInTheDocument();
    expect(await screen.findByLabelText("M365_BUSINESS (4/25)")).toBeInTheDocument();
    expect(await screen.findByLabelText("New starters")).toBeInTheDocument();
    expect(api.directory.skus).toHaveBeenCalledWith("t1");
    expect(api.directory.groups).toHaveBeenCalledWith("t1");
  });

  it("submits the hire payload, shows a toast, and renders the result steps", async () => {
    const user = userEvent.setup();
    renderNewHire();

    await selectTenant(user);
    await screen.findByLabelText("First name");
    // UPN domain is seeded from the tenant's own defaultDomain as soon as it's selected -- no
    // typing needed here, and typing more into it would append rather than replace.
    expect(screen.getByLabelText("UPN domain (e.g. contoso.com)")).toHaveValue("contoso.com");
    await user.type(screen.getByLabelText("First name"), "Ada");
    await user.type(screen.getByLabelText("Last name"), "Lovelace");
    await user.type(screen.getByLabelText("Mail nickname (e.g. ada)"), "ada");
    await user.type(screen.getByLabelText("Job title"), "Engineer");
    await user.type(screen.getByLabelText("Department"), "Platform");
    await user.click(screen.getByLabelText("M365_BUSINESS (4/25)"));
    await user.click(screen.getByLabelText("New starters"));
    await user.click(screen.getByRole("button", { name: "Create user" }));

    await waitFor(() => expect(api.provisioning.hire).toHaveBeenCalledWith("t1", {
      displayName: "Ada Lovelace",
      givenName: "Ada",
      surname: "Lovelace",
      userPrincipalName: "ada@contoso.com",
      mailNickname: "ada",
      usageLocation: "US",
      jobTitle: "Engineer",
      department: "Platform",
      licenseSkuIds: ["sku-1"],
      groupIds: ["group-1"]
    }));
    expect(await screen.findByText("Ada Lovelace provisioned")).toBeInTheDocument();
    expect(await screen.findByText("Create account")).toBeInTheDocument();
  });

  it("shows an error alert when directory loading fails", async () => {
    vi.mocked(api.directory.skus).mockRejectedValue(new Error("directory boom"));
    const user = userEvent.setup();
    renderNewHire();

    await selectTenant(user);

    expect(await screen.findByRole("alert")).toHaveTextContent("directory boom");
  });

  it("does not let a slower tenant's stale template response overwrite a newer tenant's selections", async () => {
    // Regression test: switching tenants before the previous tenant's provisioning-template
    // prefill resolves used to let the stale response silently overwrite the newly selected
    // tenant's license/group selections. It must now be discarded instead.
    const tenant1 = { ...tenant, contractId: "c1" };
    const tenant2: Tenant = { ...tenant, id: "t2", displayName: "Fabrikam", contractId: "c2" };
    vi.mocked(api.tenants.list).mockResolvedValue([tenant1, tenant2]);

    let resolveTenant1Template!: (tpl: ProvisioningTemplate) => void;
    const tenant1Template = new Promise<ProvisioningTemplate>((resolve) => {
      resolveTenant1Template = resolve;
    });
    vi.mocked(api.provisioning.getTemplate).mockImplementation((contractId: string) => {
      if (contractId === "c1") return tenant1Template;
      if (contractId === "c2") {
        return Promise.resolve({ contractId: "c2", usageLocation: "US", licenseSkuIds: [], groupIds: [] });
      }
      return Promise.resolve(undefined);
    });

    const user = userEvent.setup();
    renderNewHire();

    // Select Contoso (t1) -- its template fetch is deliberately left pending.
    await user.click(await screen.findByLabelText("Tenant"));
    await user.click(screen.getByRole("option", { name: "Contoso" }));
    await screen.findByLabelText("First name");

    // Switch to Fabrikam (t2) before t1's template resolves; t2's own template resolves
    // immediately with no licenses selected.
    await user.click(screen.getByLabelText("Tenant"));
    await user.click(screen.getByRole("option", { name: "Fabrikam" }));
    const licenseCheckbox = await screen.findByLabelText<HTMLInputElement>("M365_BUSINESS (4/25)");
    await waitFor(() => expect(licenseCheckbox.checked).toBe(false));

    // Now let t1's stale response land. It must not retroactively check t2's license box.
    resolveTenant1Template({ contractId: "c1", usageLocation: "US", licenseSkuIds: ["sku-1"], groupIds: ["group-1"] });
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(licenseCheckbox.checked).toBe(false);
  });

  it("resets tenant-scoped fields and selections when switching to a tenant with no provisioning template", async () => {
    // Regression test: a tenant with no contractId used to inherit the previous tenant's UPN
    // domain/job title/department and license/group selections wholesale, submittable as-is.
    const tenant2: Tenant = { id: "t2", tenantId: "guid-2", displayName: "Fabrikam", defaultDomain: "fabrikam.com", status: "Active" };
    vi.mocked(api.tenants.list).mockResolvedValue([tenant, tenant2]);
    const user = userEvent.setup();
    renderNewHire();

    await selectTenant(user);
    await screen.findByLabelText("First name");
    await user.type(screen.getByLabelText("Job title"), "Engineer");
    await user.type(screen.getByLabelText("Department"), "Platform");
    await user.click(screen.getByLabelText("M365_BUSINESS (4/25)"));

    await user.click(screen.getByLabelText("Tenant"));
    await user.click(screen.getByRole("option", { name: "Fabrikam" }));
    await screen.findByLabelText("First name");

    expect(screen.getByLabelText("UPN domain (e.g. contoso.com)")).toHaveValue("fabrikam.com");
    expect(screen.getByLabelText("Job title")).toHaveValue("");
    expect(screen.getByLabelText("Department")).toHaveValue("");
    expect(await screen.findByLabelText<HTMLInputElement>("M365_BUSINESS (4/25)")).not.toBeChecked();
  });

  it("still loads the second tenant's directory after a rapid switch, instead of the request being silently dropped", async () => {
    // Regression test: useAsyncAction.run() is single-flight -- a directory fetch for the tenant
    // switched away from used to silently swallow the newly selected tenant's own fetch entirely.
    const tenant2: Tenant = { id: "t2", tenantId: "guid-2", displayName: "Fabrikam", defaultDomain: "fabrikam.com", status: "Active" };
    vi.mocked(api.tenants.list).mockResolvedValue([tenant, tenant2]);

    let resolveT1Skus!: (v: Sku[]) => void;
    const t1SkusPromise = new Promise<Sku[]>((resolve) => { resolveT1Skus = resolve; });
    vi.mocked(api.directory.skus).mockImplementation((id: string) => (id === "t1" ? t1SkusPromise : Promise.resolve([sku])));
    vi.mocked(api.directory.groups).mockResolvedValue([group]);

    const user = userEvent.setup();
    renderNewHire();

    await user.click(await screen.findByLabelText("Tenant"));
    await user.click(screen.getByRole("option", { name: "Contoso" })); // t1 -- its SKUs fetch is left pending

    await user.click(screen.getByLabelText("Tenant"));
    await user.click(screen.getByRole("option", { name: "Fabrikam" })); // t2, while t1's fetch is still in flight

    resolveT1Skus([]); // let the stale, in-flight t1 fetch finish so the gate can retry for t2
    expect(await screen.findByLabelText("M365_BUSINESS (4/25)")).toBeInTheDocument();
    expect(api.directory.skus).toHaveBeenCalledWith("t2");
  });
});