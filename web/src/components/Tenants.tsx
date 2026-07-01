import { useEffect, useState } from "react";
import { api } from "../api";
import type { Contract, Tenant } from "../types";

export function Tenants() {
  const [tenants, setTenants] = useState<Tenant[]>([]);
  const [contracts, setContracts] = useState<Contract[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = () =>
    Promise.all([api.tenants.list(), api.contracts.list()])
      .then(([t, c]) => { setTenants(t); setContracts(c); })
      .catch((e) => setError(String(e)));

  useEffect(() => { load(); }, []);

  const sync = async () => {
    setBusy(true); setError(null);
    try { await api.tenants.sync(); await load(); }
    catch (e) { setError(String(e)); }
    finally { setBusy(false); }
  };

  const assign = async (id: string, contractId: string) => {
    await api.tenants.setContract(id, contractId || null);
    await load();
  };

  return (
    <section>
      <div className="toolbar">
        <h2>Tenants</h2>
        <button onClick={sync} disabled={busy}>{busy ? "Syncing…" : "Sync from Partner Center"}</button>
      </div>
      {error && <p className="error">{error}</p>}
      <table>
        <thead><tr><th>Name</th><th>Domain</th><th>Status</th><th>Contract</th></tr></thead>
        <tbody>
          {tenants.map((t) => (
            <tr key={t.id}>
              <td>{t.displayName}</td>
              <td>{t.defaultDomain ?? "—"}</td>
              <td><span className={`badge ${t.status.toLowerCase()}`}>{t.status}</span></td>
              <td>
                <select value={t.contractId ?? ""} onChange={(e) => assign(t.id, e.target.value)}>
                  <option value="">— none —</option>
                  {contracts.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
                </select>
              </td>
            </tr>
          ))}
          {tenants.length === 0 && <tr><td colSpan={4} className="muted">No tenants yet. Sync from Partner Center.</td></tr>}
        </tbody>
      </table>
    </section>
  );
}
