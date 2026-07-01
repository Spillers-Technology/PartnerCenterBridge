import { useEffect, useState } from "react";
import { api } from "../api";
import type { Dashboard as DashboardData } from "../types";

function Stat({ label, value, tone }: { label: string; value: number; tone?: "ok" | "warn" | "err" }) {
  return (
    <div className="stat">
      <div className={`stat-value ${tone ?? ""}`}>{value}</div>
      <div className="stat-label">{label}</div>
    </div>
  );
}

export function Dashboard() {
  const [data, setData] = useState<DashboardData | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api.dashboard().then(setData).catch((e) => setError(String(e)));
  }, []);

  if (error) return <section><h2>Dashboard</h2><p className="error">{error}</p></section>;
  if (!data) return <section><h2>Dashboard</h2><p className="muted">Loading…</p></section>;

  const s = data.stats;
  return (
    <section>
      <h2>Dashboard</h2>

      <div className="stats">
        <Stat label="Tenants" value={s.tenants} />
        <Stat label="No delegation" value={s.tenantsNoDelegation} tone={s.tenantsNoDelegation > 0 ? "warn" : "ok"} />
        <Stat label="Deployments" value={s.deployments} />
        <Stat label="Failed deployments" value={s.deploymentsFailed} tone={s.deploymentsFailed > 0 ? "err" : "ok"} />
        <Stat label="Updates available" value={s.deploymentsUpdateAvailable} tone={s.deploymentsUpdateAvailable > 0 ? "warn" : "ok"} />
        <Stat label="Runs (24h)" value={s.runsLast24h} />
        <Stat label="Failed runs (7d)" value={s.runsFailedLast7d} tone={s.runsFailedLast7d > 0 ? "err" : "ok"} />
      </div>

      <div className="plan">
        <h3>Needs attention</h3>
        {data.needsAttention.length === 0 && <p className="muted">Nothing - all quiet.</p>}
        {data.needsAttention.length > 0 && (
          <table>
            <thead><tr><th>What</th><th>Tenant</th><th>Subject</th><th>Detail</th><th>When</th></tr></thead>
            <tbody>
              {data.needsAttention.map((a, i) => (
                <tr key={i}>
                  <td><span className={`badge ${a.kind === "No delegation" ? "pending" : "failed"}`}>{a.kind}</span></td>
                  <td>{a.tenantName}</td>
                  <td>{a.subject}</td>
                  <td className="muted">{a.detail}</td>
                  <td>{a.when ? new Date(a.when).toLocaleString() : ""}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      <div className="plan">
        <h3>Recent workflow runs</h3>
        {data.recentRuns.length === 0 && <p className="muted">No runs recorded yet.</p>}
        {data.recentRuns.length > 0 && (
          <table>
            <thead><tr><th>When</th><th>Workflow</th><th>Tenant</th><th>Kind</th><th>Operator</th><th>Result</th></tr></thead>
            <tbody>
              {data.recentRuns.map((r) => (
                <tr key={r.id} title={r.error ?? undefined}>
                  <td>{new Date(r.startedAt).toLocaleString()}</td>
                  <td>{r.workflowName}</td>
                  <td>{r.tenantName}</td>
                  <td>{r.kind}</td>
                  <td>{r.operator}</td>
                  <td><span className={`badge ${r.succeeded ? "succeeded" : "failed"}`}>{r.succeeded ? "ok" : "failed"}</span></td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </section>
  );
}
