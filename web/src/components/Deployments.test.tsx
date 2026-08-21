import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { ThemeProvider } from "@mui/material/styles";
import { theme } from "../theme";
import { Deployments } from "./Deployments";
import type { AppTemplate, Deployment, Tenant } from "../types";

vi.mock("../api", () => ({
  api: {
    deployments: { list: vi.fn() },
    tenants: { list: vi.fn() },
    templates: { list: vi.fn() }
  }
}));

import { api } from "../api";

function renderDeployments() {
  return render(
    <ThemeProvider theme={theme}>
      <Deployments />
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
});
