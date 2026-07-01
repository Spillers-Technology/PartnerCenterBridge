import { useEffect, useState } from "react";
import { api } from "../api";
import type { AppTemplate, Deployment, Tenant } from "../types";

export function DeployWizard() {
  const [templates, setTemplates] = useState<AppTemplate[]>([]);
  const [tenants, setTenants] = useState<Tenant[]>([]);
  const [templateId, setTemplateId] = useState("");
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [running, setRunning] = useState(false);
  const [results, setResults] = useState<Deployment[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    Promise.all([api.templates.list(), api.tenants.list()])
      .then(([tpl, tn]) => { setTemplates(tpl); setTenants(tn); })
      .catch((e) => setError(String(e)));
  }, []);

  const toggle = (id: string) => {
    const next = new Set(selected);
    next.has(id) ? next.delete(id) : next.add(id);
    setSelected(next);
  };

  const deploy = async () => {
    if (!templateId || selected.size === 0) return;
    setRunning(true); setError(null); setResults(null);
    try { setResults(await api.deployments.deploy(templateId, [...selected])); }
    catch (e) { setError(String(e)); }
    finally { setRunning(false); }
  };

  const chosen = templates.find((t) => t.id === templateId);

  return (
    <section>
      <h2>Deploy a template</h2>
      <label className="field">
        Template
        <select value={templateId} onChange={(e) => setTemplateId(e.target.value)}>
          <option value="">— choose —</option>
          {templates.map((t) => (
            <option key={t.id} value={t.id} disabled={!t.hasPackage}>
              {t.displayName} v{t.contentVersion}{t.hasPackage ? "" : " (no package)"}
            </option>
          ))}
        </select>
      </label>

      <fieldset>
        <legend>Target tenants</legend>
        {tenants.map((t) => (
          <label key={t.id} className="check">
            <input type="checkbox" checked={selected.has(t.id)} onChange={() => toggle(t.id)} />
            {t.displayName}
          </label>
        ))}
        {tenants.length === 0 && <p className="muted">No tenants. Sync first.</p>}
      </fieldset>

      <button onClick={deploy} disabled={running || !chosen?.hasPackage || selected.size === 0}>
        {running ? "Deploying…" : `Deploy to ${selected.size} tenant(s)`}
      </button>
      {error && <p className="error">{error}</p>}

      {results && (
        <table>
          <thead><tr><th>Tenant</th><th>Status</th><th>Intune app id</th><th>Error</th></tr></thead>
          <tbody>
            {results.map((r) => {
              const t = tenants.find((x) => x.id === r.tenantId);
              return (
                <tr key={r.id}>
                  <td>{t?.displayName ?? r.tenantId}</td>
                  <td><span className={`badge ${r.status.toLowerCase()}`}>{r.status}</span></td>
                  <td className="mono">{r.intuneAppId ?? "—"}</td>
                  <td className="error">{r.lastError ?? ""}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
    </section>
  );
}
