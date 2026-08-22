import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ThemeProvider } from "@mui/material/styles";
import { theme } from "../theme";
import { ConfirmDialogProvider } from "../hooks/useConfirm";
import { ToastProvider } from "../hooks/useToast";
import { Deployments } from "./Deployments";
import type { AppTemplate, Deployment, Tenant } from "../types";

vi.mock("../api", () => ({
  api: {
    deployments: { list: vi.fn(), deploy: vi.fn() },
    tenants: { list: vi.fn() },
    templates: { list: vi.fn() }
  }
}));

import { api } from "../api";

function renderDeployments() {
  return render(
    <ThemeProvider theme={theme}>
      <ToastProvider>
        <ConfirmDialogProvider>
          <Deployments />
        </ConfirmDialogProvider>
      </ToastProvider>
    </ThemeProvider>
  );
}

const tenant: Tenant = { id: "t1", tenantId: "aad-t1", displayName: "Contoso Ltd", status: "Active" };
const template: AppTemplate = {
  id: "tpl1",
  displayName: "Company Portal",
  installCommandLine: "install.exe",
  uninstallCommandLine: "uninstall.exe",
  contentVersion: 3,
  hasPackage: true,
  detectionRules: [],
  assignments: []
};
const deployment: Deployment = {
  id: "d1",
  appTemplateId: "tpl1",
  tenantId: "t1",
  deployedTemplateVersion: 3,
  status: "Succeeded",
  lastSyncedAt: "2026-08-19T10:00:00Z"
};

describe("Deployments", () => {
  beforeEach(() => vi.clearAllMocks());

  it("shows a loading skeleton while the fetches are pending, then the populated view once they resolve", async () => {
    let resolveDeployments!: (value: Deployment[]) => void;
    vi.mocked(api.deployments.list).mockReturnValue(
      new Promise((resolve) => { resolveDeployments = resolve; })
    );
    vi.mocked(api.tenants.list).mockResolvedValue([tenant]);
    vi.mocked(api.templates.list).mockResolvedValue([template]);

    renderDeployments();

    expect(screen.getByText("Deployment history")).toBeInTheDocument();
    expect(screen.getByText("Loading deployment history...")).toBeInTheDocument();
    expect(screen.queryByText("Company Portal")).not.toBeInTheDocument();

    resolveDeployments([deployment]);

    expect(await screen.findByText("Company Portal")).toBeInTheDocument();
    expect(screen.queryByText("Loading deployment history...")).not.toBeInTheDocument();
  });

  it("renders the table with resolved tenant/template display names, not raw ids", async () => {
    vi.mocked(api.deployments.list).mockResolvedValue([deployment]);
    vi.mocked(api.tenants.list).mockResolvedValue([tenant]);
    vi.mocked(api.templates.list).mockResolvedValue([template]);

    renderDeployments();

    expect(await screen.findByText("Company Portal")).toBeInTheDocument();
    expect(screen.getByText("Contoso Ltd")).toBeInTheDocument();
    expect(screen.queryByText("tpl1")).not.toBeInTheDocument();
    expect(screen.queryByText("t1")).not.toBeInTheDocument();
    expect(screen.getByText("v3")).toBeInTheDocument();
    expect(screen.getByText("Succeeded")).toBeInTheDocument();
  });

  it("shows the empty-state message when there are no deployments", async () => {
    vi.mocked(api.deployments.list).mockResolvedValue([]);
    vi.mocked(api.tenants.list).mockResolvedValue([]);
    vi.mocked(api.templates.list).mockResolvedValue([]);

    renderDeployments();

    expect(await screen.findByText("No deployments yet.")).toBeInTheDocument();
  });

  it("shows an error alert with the bare message when a fetch rejects", async () => {
    vi.mocked(api.deployments.list).mockRejectedValue(new Error("500 Internal Server Error"));
    vi.mocked(api.tenants.list).mockResolvedValue([]);
    vi.mocked(api.templates.list).mockResolvedValue([]);

    renderDeployments();

    expect(await screen.findByText("500 Internal Server Error")).toBeInTheDocument();
  });

  const failed: Deployment = { ...deployment, id: "d2", status: "Failed", lastError: "Graph API 403" };
  const updateAvailable: Deployment = { ...deployment, id: "d3", status: "UpdateAvailable", lastError: undefined };

  it("shows the last error inline for a failed deployment", async () => {
    vi.mocked(api.deployments.list).mockResolvedValue([failed]);
    vi.mocked(api.tenants.list).mockResolvedValue([tenant]);
    vi.mocked(api.templates.list).mockResolvedValue([template]);

    renderDeployments();

    expect(await screen.findByText("Graph API 403")).toBeInTheDocument();
  });

  it("offers Retry for a failed deployment and Update for one with an update available", async () => {
    vi.mocked(api.deployments.list).mockResolvedValue([failed, updateAvailable]);
    vi.mocked(api.tenants.list).mockResolvedValue([tenant]);
    vi.mocked(api.templates.list).mockResolvedValue([template]);
    renderDeployments();

    await screen.findByText("Graph API 403");
    expect(screen.getByRole("button", { name: "Retry" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Update" })).toBeInTheDocument();
  });

  it("gates Retry behind useConfirm: cancel does not call deploy", async () => {
    vi.mocked(api.deployments.list).mockResolvedValue([failed]);
    vi.mocked(api.tenants.list).mockResolvedValue([tenant]);
    vi.mocked(api.templates.list).mockResolvedValue([template]);
    const user = userEvent.setup();
    renderDeployments();

    await screen.findByText("Graph API 403");
    await user.click(screen.getByRole("button", { name: "Retry" }));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText("Retry this deployment?")).toBeInTheDocument();
    await user.click(within(dialog).getByRole("button", { name: "Cancel" }));

    expect(api.deployments.deploy).not.toHaveBeenCalled();
  });

  it("does not offer Retry/Update for a template that no longer has a package attached", async () => {
    vi.mocked(api.deployments.list).mockResolvedValue([failed]);
    vi.mocked(api.tenants.list).mockResolvedValue([tenant]);
    vi.mocked(api.templates.list).mockResolvedValue([{ ...template, hasPackage: false }]);

    renderDeployments();

    await screen.findByText("Graph API 403");
    expect(screen.getByRole("button", { name: "Retry" })).toBeDisabled();
  });

  it("re-deploys the same template+tenant and refreshes the list on confirm", async () => {
    vi.mocked(api.deployments.list)
      .mockResolvedValueOnce([failed])
      .mockResolvedValueOnce([{ ...failed, status: "Succeeded", lastError: undefined }]);
    vi.mocked(api.tenants.list).mockResolvedValue([tenant]);
    vi.mocked(api.templates.list).mockResolvedValue([template]);
    vi.mocked(api.deployments.deploy).mockResolvedValue([{ ...failed, status: "Succeeded", lastError: undefined }]);
    const user = userEvent.setup();
    renderDeployments();

    await screen.findByText("Graph API 403");
    await user.click(screen.getByRole("button", { name: "Retry" }));
    const dialog = await screen.findByRole("dialog");
    await user.click(within(dialog).getByRole("button", { name: "Retry" }));

    expect(await screen.findByText(/redeployed to Contoso Ltd/)).toBeInTheDocument();
    expect(api.deployments.deploy).toHaveBeenCalledWith("tpl1", ["t1"]);
    expect(api.deployments.list).toHaveBeenCalledTimes(2);
  });
});
