import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { ThemeProvider } from "@mui/material/styles";
import { theme } from "../theme";
import { Dashboard } from "./Dashboard";

vi.mock("../api", () => ({ api: { dashboard: vi.fn() } }));

import { api } from "../api";

function renderDashboard() {
  return render(
    <ThemeProvider theme={theme}>
      <Dashboard />
    </ThemeProvider>
  );
}

describe("Dashboard", () => {
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

  it("shows an error alert with the bare message, no 'Error:' prefix", async () => {
    vi.mocked(api.dashboard).mockRejectedValue(new Error("500 Internal Server Error"));
    renderDashboard();
    // Exact match on purpose: the current Dashboard does setError(String(e)), which stringifies to
    // "Error: 500 Internal Server Error" (with the prefix) -- this must NOT match until the
    // rewrite switches to `e instanceof Error ? e.message : String(e)`.
    expect(await screen.findByText("500 Internal Server Error")).toBeInTheDocument();
  });
});
