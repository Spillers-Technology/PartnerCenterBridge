import { useEffect, useState } from "react";
import { api } from "../api";
import type { AppTemplate, Deployment, Tenant } from "../types";

export function Deployments() {
  const [deployments, setDeployments] = useState<Deployment[]>([]);
  const [tenants, setTenants] = useState<Tenant[]>([]);
  const [templates, setTemplates] = useState<AppTemplate[]>([]);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    Promise.all([api.deployments.list(), api.tenants.list(), api.templates.list()])
      .then(([d, tn, tpl]) => { setDeployments(d); setTenants(tn); setTemplates(tpl); })
      .catch((e) => setError(String(e)));
  }, []);

  const name = (id: string, list: { id: string; displayName: string }[]) =>
    list.find((x) => x.id === id)?.displayName ?? id;

  return (
    <section>
      <h2>Deployment history</h2>
      {error && <p className="error">{error}</p>}
      <table>
        <thead><tr><th>Template</th><th>Tenant</th><th>Version</th><th>Status</th><th>Last synced</th></tr></thead>
        <tbody>
          {deployments.map((d) => (
            <tr key={d.id}>
              <td>{name(d.appTemplateId, templates)}</td>
              <td>{name(d.tenantId, tenants)}</td>
              <td>v{d.deployedTemplateVersion}</td>
              <td><span className={`badge ${d.status.toLowerCase()}`}>{d.status}</span></td>
              <td>{d.lastSyncedAt ? new Date(d.lastSyncedAt).toLocaleString() : "—"}</td>
            </tr>
          ))}
          {deployments.length === 0 && <tr><td colSpan={5} className="muted">No deployments yet.</td></tr>}
        </tbody>
      </table>
    </section>
  );
}
