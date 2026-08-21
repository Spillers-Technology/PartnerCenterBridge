import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ThemeProvider } from "@mui/material/styles";
import { theme } from "../theme";
import { ConfirmDialogProvider } from "../hooks/useConfirm";
import { ToastProvider } from "../hooks/useToast";
import { DeployWizard } from "./DeployWizard";
import type { AppTemplate, Deployment, Tenant } from "../types";

vi.mock("../api", () => ({
  api: {
    templates: { list: vi.fn() },
    tenants: { list: vi.fn() },
    deployments: { deploy: vi.fn() }
  }
}));

import { api } from "../api";

const template: AppTemplate = {
  id: "t1",
  displayName: "Company Portal",
  installCommandLine: "install.exe",
  uninstallCommandLine: "uninstall.exe",
  contentVersion: 3,
  hasPackage: true,
  detectionRules: [],
  assignments: []
};

const tenant: Tenant = { id: "te1", tenantId: "tid1", displayName: "Fabrikam Inc", status: "Active" };
const tenant2: Tenant = { id: "te2", tenantId: "tid2", displayName: "Contoso Ltd", status: "Active" };

const succeeded: Deployment = {
  id: "d1",
  appTemplateId: "t1",
  tenantId: "te1",
  intuneAppId: "app-1",
  deployedTemplateVersion: 3,
  status: "Succeeded"
};

const failed: Deployment = {
  id: "d2",
  appTemplateId: "t1",
  tenantId: "te2",
  deployedTemplateVersion: 3,
  status: "Failed",
  lastError: "409 conflict"
};

function renderComponent() {
  return render(
    <ThemeProvider theme={theme}>
      <ToastProvider>
        <ConfirmDialogProvider>
          <DeployWizard />
        </ConfirmDialogProvider>
      </ToastProvider>
    </ThemeProvider>
  );
}

async function chooseTemplateAndTenant(user: ReturnType<typeof userEvent.setup>) {
  await user.click(screen.getByLabelText("Template"));
  await user.click(await screen.findByRole("option", { name: "Company Portal v3" }));
  await user.click(screen.getByLabelText("Fabrikam Inc"));
}

describe("DeployWizard", () => {
  it("loads templates and tenants and renders them", async () => {
    vi.mocked(api.templates.list).mockResolvedValue([template]);
    vi.mocked(api.tenants.list).mockResolvedValue([tenant]);
    renderComponent();

    expect(await screen.findByLabelText("Template")).toBeInTheDocument();
    expect(screen.getByText("Target tenants")).toBeInTheDocument();
    expect(screen.getByText("Fabrikam Inc")).toBeInTheDocument();
  });

  it("shows the 'no tenants' message when the tenant list is empty", async () => {
    vi.mocked(api.templates.list).mockResolvedValue([template]);
    vi.mocked(api.tenants.list).mockResolvedValue([]);
    renderComponent();

    expect(await screen.findByText("No tenants. Sync first.")).toBeInTheDocument();
  });

  it("shows an error alert when the initial load fails, instead of loading forever", async () => {
    vi.mocked(api.templates.list).mockRejectedValue(new Error("500 Internal Server Error"));
    vi.mocked(api.tenants.list).mockResolvedValue([]);
    renderComponent();

    expect(await screen.findByText("500 Internal Server Error")).toBeInTheDocument();
  });

  it("disables the deploy button until a template and a tenant are chosen", async () => {
    vi.mocked(api.templates.list).mockResolvedValue([template]);
    vi.mocked(api.tenants.list).mockResolvedValue([tenant]);
    renderComponent();

    expect(await screen.findByRole("button", { name: "Deploy to 0 tenant(s)" })).toBeDisabled();
  });

  it("gates deploy behind confirm -- cancel does not call deploy, confirm does", async () => {
    vi.mocked(api.templates.list).mockResolvedValue([template]);
    vi.mocked(api.tenants.list).mockResolvedValue([tenant]);
    vi.mocked(api.deployments.deploy).mockResolvedValue([succeeded]);
    const user = userEvent.setup();
    renderComponent();

    await screen.findByLabelText("Template");
    await chooseTemplateAndTenant(user);

    await user.click(screen.getByRole("button", { name: "Deploy to 1 tenant(s)" }));
    await screen.findByText("Deploy template?");
    expect(screen.getByText('Deploy "Company Portal" to 1 tenant(s)?')).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Cancel" }));
    expect(api.deployments.deploy).not.toHaveBeenCalled();
    await waitFor(() => expect(screen.queryByText("Deploy template?")).not.toBeInTheDocument());

    await user.click(screen.getByRole("button", { name: "Deploy to 1 tenant(s)" }));
    await screen.findByText("Deploy template?");
    await user.click(screen.getByRole("button", { name: "Deploy" }));

    expect(api.deployments.deploy).toHaveBeenCalledWith("t1", ["te1"]);
  });

  it("deploys successfully and renders the results table with a success toast", async () => {
    vi.mocked(api.templates.list).mockResolvedValue([template]);
    vi.mocked(api.tenants.list).mockResolvedValue([tenant]);
    vi.mocked(api.deployments.deploy).mockResolvedValue([succeeded]);
    const user = userEvent.setup();
    renderComponent();

    await screen.findByLabelText("Template");
    await chooseTemplateAndTenant(user);
    await user.click(screen.getByRole("button", { name: "Deploy to 1 tenant(s)" }));
    await screen.findByText("Deploy template?");
    await user.click(screen.getByRole("button", { name: "Deploy" }));

    expect(await screen.findByText("Deployed to 1 tenant(s).")).toBeInTheDocument();
    expect(screen.getByText("Succeeded")).toBeInTheDocument();
    expect(screen.getByText("app-1")).toBeInTheDocument();
  });

  it("shows a warning toast and the error column when some deployments fail", async () => {
    vi.mocked(api.templates.list).mockResolvedValue([template]);
    vi.mocked(api.tenants.list).mockResolvedValue([tenant, tenant2]);
    vi.mocked(api.deployments.deploy).mockResolvedValue([succeeded, failed]);
    const user = userEvent.setup();
    renderComponent();

    await screen.findByLabelText("Template");
    await chooseTemplateAndTenant(user);
    await user.click(screen.getByLabelText("Contoso Ltd"));
    await user.click(screen.getByRole("button", { name: "Deploy to 2 tenant(s)" }));
    await screen.findByText("Deploy template?");
    await user.click(screen.getByRole("button", { name: "Deploy" }));

    expect(await screen.findByText("Deployed to 1 of 2 tenant(s) - 1 failed.")).toBeInTheDocument();
    expect(screen.getByText("Failed")).toBeInTheDocument();
    expect(screen.getByText("409 conflict")).toBeInTheDocument();
  });
});
