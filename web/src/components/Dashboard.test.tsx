import { afterEach, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ThemeProvider } from "@mui/material/styles";
import { theme } from "../theme";
import { Dashboard } from "./Dashboard";
import type { Dashboard as DashboardData } from "../types";

vi.mock("../api", () => ({ api: { dashboard: vi.fn() } }));

import { api } from "../api";

function mockMatchMedia(matches: boolean) {
  window.matchMedia = vi.fn().mockImplementation((query: string) => ({
    matches,
    media: query,
    onchange: null,
    addListener: vi.fn(),
    removeListener: vi.fn(),
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    dispatchEvent: vi.fn()
  }));
}

function renderDashboard(props: { onNavigate?: (tab: "tenants" | "history" | "workflows") => void } = {}) {
  return render(
    <ThemeProvider theme={theme}>
      <Dashboard {...props} />
    </ThemeProvider>
  );
}

const sampleData: DashboardData = {
  stats: { tenants: 5, tenantsNoDelegation: 1, deployments: 8, deploymentsFailed: 1, deploymentsUpdateAvailable: 2, runsLast24h: 6, runsFailedLast7d: 1 },
  needsAttention: [
    { kind: "Deployment failed", tenantId: "t1", tenantName: "Fabrikam Inc", subject: "Company Portal branding", detail: "409 conflict", when: "2026-08-19T10:00:00Z" }
  ],
  recentRuns: []
};

describe("Dashboard", () => {
  afterEach(() => vi.restoreAllMocks());

  it("shows loading skeletons while the fetch is pending, then the populated view once it resolves", async () => {
    let resolveDashboard!: (value: DashboardData) => void;
    vi.mocked(api.dashboard).mockReturnValue(
      new Promise((resolve) => { resolveDashboard = resolve; })
    );

    renderDashboard();

    expect(screen.getByText("Dashboard")).toBeInTheDocument();
    expect(screen.getByText("Loading dashboard...")).toBeInTheDocument();
    expect(screen.queryByText("5")).not.toBeInTheDocument();

    resolveDashboard({
      stats: { tenants: 5, tenantsNoDelegation: 1, deployments: 8, deploymentsFailed: 1, deploymentsUpdateAvailable: 2, runsLast24h: 6, runsFailedLast7d: 1 },
      needsAttention: [],
      recentRuns: []
    });

    expect(await screen.findByText("5")).toBeInTheDocument();
    expect(screen.queryByText("Loading dashboard...")).not.toBeInTheDocument();
  });

  it("renders stats and tables once data loads", async () => {
    vi.mocked(api.dashboard).mockResolvedValue({
      stats: {
        tenants: 5, tenantsNoDelegation: 1, deployments: 8, deploymentsFailed: 1,
        deploymentsUpdateAvailable: 2, runsLast24h: 6, runsFailedLast7d: 1
      },
      needsAttention: [
        { kind: "Deployment failed", tenantId: "t1", tenantName: "Fabrikam Inc", subject: "Company Portal branding", detail: "409 conflict", when: "2026-08-19T10:00:00Z" }
      ],
      recentRuns: [
        { id: "r1", workflowId: "mailbox-archive", workflowName: "Mailbox archive repair", tenantId: "t1", tenantName: "Contoso Ltd", kind: "Remediate", operator: "jspillers", inputs: {}, findings: [], steps: [], succeeded: true, startedAt: "2026-08-19T09:00:00Z", durationMs: 5210 }
      ]
    });

    renderDashboard();

    expect(await screen.findByText("5")).toBeInTheDocument();
    expect(screen.getByText("Fabrikam Inc")).toBeInTheDocument();
    expect(screen.getByText("Deployment failed")).toBeInTheDocument();
    expect(screen.getByText("Mailbox archive repair")).toBeInTheDocument();
    expect(screen.getByText("ok")).toBeInTheDocument();
  });

  it("shows the all-quiet message when nothing needs attention", async () => {
    vi.mocked(api.dashboard).mockResolvedValue({
      stats: { tenants: 2, tenantsNoDelegation: 0, deployments: 2, deploymentsFailed: 0, deploymentsUpdateAvailable: 0, runsLast24h: 0, runsFailedLast7d: 0 },
      needsAttention: [],
      recentRuns: []
    });

    renderDashboard();

    expect(await screen.findByText("Nothing - all quiet.")).toBeInTheDocument();
    expect(screen.getByText("No runs recorded yet.")).toBeInTheDocument();
  });

  it("shows a failed run's error as visible text, not only in a hover-only title attribute", async () => {
    // Regression test: the error used to live only in a native `title` attribute on the row,
    // which only surfaces on mouse hover -- invisible to keyboard and touch users.
    vi.mocked(api.dashboard).mockResolvedValue({
      stats: { tenants: 2, tenantsNoDelegation: 0, deployments: 2, deploymentsFailed: 0, deploymentsUpdateAvailable: 0, runsLast24h: 0, runsFailedLast7d: 0 },
      needsAttention: [],
      recentRuns: [
        {
          id: "r1", workflowId: "mailbox-archive", workflowName: "Mailbox archive repair", tenantId: "t1",
          tenantName: "Contoso Ltd", kind: "Remediate", operator: "jspillers", inputs: {}, findings: [], steps: [],
          succeeded: false, error: "Graph API 403", startedAt: "2026-08-19T09:00:00Z", durationMs: 5210
        }
      ]
    });

    renderDashboard();

    expect(await screen.findByText("Graph API 403")).toBeInTheDocument();
  });

  it("shows an error alert with the bare message, no 'Error:' prefix", async () => {
    vi.mocked(api.dashboard).mockRejectedValue(new Error("500 Internal Server Error"));
    renderDashboard();
    expect(await screen.findByText("500 Internal Server Error")).toBeInTheDocument();
  });

  it("puts Needs attention before the stat tiles on a phone-width screen", async () => {
    mockMatchMedia(true);
    vi.mocked(api.dashboard).mockResolvedValue(sampleData);
    renderDashboard();

    await screen.findByText("Fabrikam Inc");
    const headings = screen.getAllByRole("heading", { level: 3 }).map((h) => h.textContent);
    expect(headings.indexOf("Needs attention")).toBeLessThan(headings.indexOf("Recent workflow runs"));
    // "Needs attention" (an h3) now renders before the Tenants stat tile in the DOM.
    const needsAttentionHeading = screen.getByRole("heading", { name: "Needs attention" });
    const tenantsTile = screen.getByText("Tenants");
    expect(needsAttentionHeading.compareDocumentPosition(tenantsTile) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it("calls onNavigate with the right tab when a linked stat tile is clicked", async () => {
    mockMatchMedia(false);
    vi.mocked(api.dashboard).mockResolvedValue(sampleData);
    const onNavigate = vi.fn();
    const user = userEvent.setup();
    renderDashboard({ onNavigate });

    await screen.findByText("Fabrikam Inc");
    await user.click(screen.getByRole("button", { name: /Tenants/ }));
    expect(onNavigate).toHaveBeenCalledWith("tenants");

    await user.click(screen.getByRole("button", { name: /Failed deployments/ }));
    expect(onNavigate).toHaveBeenCalledWith("history");

    await user.click(screen.getByRole("button", { name: /Runs \(24h\)/ }));
    expect(onNavigate).toHaveBeenCalledWith("workflows");
  });

  it("does not make stat tiles clickable when onNavigate is not provided", async () => {
    mockMatchMedia(false);
    vi.mocked(api.dashboard).mockResolvedValue(sampleData);
    renderDashboard();

    await screen.findByText("Fabrikam Inc");
    expect(screen.queryByRole("button", { name: /Tenants/ })).not.toBeInTheDocument();
  });
});
