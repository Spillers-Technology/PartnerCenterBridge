import { useEffect, useState } from "react";
import { api } from "../api";
import type { Contract } from "../types";

export function Contracts() {
  const [contracts, setContracts] = useState<Contract[]>([]);
  const [name, setName] = useState("");
  const [notes, setNotes] = useState("");
  const [plan, setPlan] = useState<Record<string, string>[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = () => api.contracts.list().then(setContracts).catch((e) => setError(String(e)));
  useEffect(() => { load(); }, []);

  const create = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!name.trim()) return;
    await api.contracts.create(name, notes || undefined);
    setName(""); setNotes("");
    await load();
  };

  const showPlan = async (id: string) => {
    setError(null);
    try { setPlan(await api.contracts.plan(id)); }
    catch (e) { setError(String(e)); }
  };

  return (
    <section>
      <h2>Contracts</h2>
      <form className="row" onSubmit={create}>
        <input placeholder="Contract name" value={name} onChange={(e) => setName(e.target.value)} />
        <input placeholder="Notes (optional)" value={notes} onChange={(e) => setNotes(e.target.value)} />
        <button type="submit">Add contract</button>
      </form>
      {error && <p className="error">{error}</p>}
      <table>
        <thead><tr><th>Name</th><th>Tenants</th><th>Desired apps</th><th></th></tr></thead>
        <tbody>
          {contracts.map((c) => (
            <tr key={c.id}>
              <td>{c.name}</td>
              <td>{c.tenantCount}</td>
              <td>{c.desiredAppCount}</td>
              <td><button onClick={() => showPlan(c.id)}>Preview plan</button></td>
            </tr>
          ))}
        </tbody>
      </table>
      {plan && (
        <div className="plan">
          <h3>Reconcile plan (dry run)</h3>
          <table>
            <thead><tr><th>Tenant</th><th>Template</th><th>Action</th></tr></thead>
            <tbody>
              {plan.map((p, i) => (
                <tr key={i}>
                  <td>{p.tenantName}</td><td>{p.templateName}</td>
                  <td><span className={`badge ${p.action.toLowerCase()}`}>{p.action}</span></td>
                </tr>
              ))}
              {plan.length === 0 && <tr><td colSpan={3} className="muted">Nothing to do.</td></tr>}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
