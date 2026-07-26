import { Fragment, useEffect, useState } from "react";
import { api } from "../api";
import type { Contract, MeProfile, Tenant, TenantGrant, TenantRole } from "../types";

const ROLES: TenantRole[] = ["Viewer", "Operator", "Owner"];

function SharePanel({ tenant, onChanged }: { tenant: Tenant; onChanged: () => void }) {
  const [grants, setGrants] = useState<TenantGrant[]>([]);
  const [email, setEmail] = useState("");
  const [role, setRole] = useState<TenantRole>("Operator");
  const [error, setError] = useState<string | null>(null);

  const load = () => api.tenantAccess.list(tenant.id).then(setGrants).catch((e) => setError(String(e)));
  useEffect(() => { load(); }, [tenant.id]);

  const grant = async (ev: React.FormEvent) => {
    ev.preventDefault();
    setError(null);
    try {
      await api.tenantAccess.grant(tenant.id, email, role);
      setEmail("");
      await load();
      onChanged();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  };

  const revoke = async (userId: string) => {
    await api.tenantAccess.revoke(tenant.id, userId);
    await load();
    onChanged();
  };

  return (
    <tr>
      <td colSpan={5}>
        <div className="password">
          <strong>Who has access to {tenant.displayName}</strong>
          {error && <p className="error">{error}</p>}
          <table>
            <thead><tr><th>Email</th><th>Role</th><th>Granted</th><th></th></tr></thead>
            <tbody>
              {grants.map((g) => (
                <tr key={g.userId}>
                  <td>{g.email}</td>
                  <td>{g.role}</td>
                  <td>{new Date(g.grantedAt).toLocaleDateString()}</td>
                  <td><button onClick={() => revoke(g.userId)}>Revoke</button></td>
                </tr>
              ))}
              {grants.length === 0 && <tr><td colSpan={4} className="muted">Only you have access so far.</td></tr>}
            </tbody>
          </table>
          <form className="row" onSubmit={grant}>
            <input placeholder="teammate@example.com" value={email} onChange={(e) => setEmail(e.target.value)} />
            <select value={role} onChange={(e) => setRole(e.target.value as TenantRole)}>
              {ROLES.map((r) => <option key={r} value={r}>{r}</option>)}
            </select>
            <button>Share</button>
          </form>
          <p className="muted">They need to already have a registered account. Viewer = read-only, Operator = can run workflows/deploy, Owner = can also share/revoke.</p>
        </div>
      </td>
    </tr>
  );
}

export function Tenants({ me }: { me: MeProfile | null }) {
  const [tenants, setTenants] = useState<Tenant[]>([]);
  const [contracts, setContracts] = useState<Contract[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [sharingId, setSharingId] = useState<string | null>(null);
  const [newTenant, setNewTenant] = useState({ tenantId: "", displayName: "", defaultDomain: "" });

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

  const addTenant = async (ev: React.FormEvent) => {
    ev.preventDefault();
    setError(null);
    try {
      await api.tenants.create(newTenant.tenantId, newTenant.displayName, newTenant.defaultDomain || undefined);
      setNewTenant({ tenantId: "", displayName: "", defaultDomain: "" });
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  };

  const assign = async (id: string, contractId: string) => {
    await api.tenants.setContract(id, contractId || null);
    await load();
  };

  // Under Auth:Mode=Local, sharing is only meaningful (and only enabled server-side) for the
  // Owner of a tenant. OIDC/dev-auth operators (me === null) already have unrestricted access,
  // so there's nothing to share -- the button doesn't apply to them.
  const roleFor = (tenantId: string) => me?.tenantAccess.find((a) => a.tenantId === tenantId)?.role;
  const canShare = (tenantId: string) => me !== null && roleFor(tenantId) === "Owner";

  return (
    <section>
      <div className="toolbar">
        <h2>Tenants</h2>
        <button onClick={sync} disabled={busy}>{busy ? "Syncing…" : "Sync from Partner Center"}</button>
      </div>
      {error && <p className="error">{error}</p>}

      <table>
        <thead><tr><th>Name</th><th>Domain</th><th>Status</th><th>Contract</th><th></th></tr></thead>
        <tbody>
          {tenants.map((t) => (
            <Fragment key={t.id}>
              <tr>
                <td>{t.displayName}</td>
                <td>{t.defaultDomain ?? "—"}</td>
                <td><span className={`badge ${t.status.toLowerCase()}`}>{t.status}</span></td>
                <td>
                  <select value={t.contractId ?? ""} onChange={(e) => assign(t.id, e.target.value)}>
                    <option value="">— none —</option>
                    {contracts.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
                  </select>
                </td>
                <td>
                  {canShare(t.id) && (
                    <button onClick={() => setSharingId(sharingId === t.id ? null : t.id)}>
                      {sharingId === t.id ? "Close" : "Share"}
                    </button>
                  )}
                </td>
              </tr>
              {sharingId === t.id && <SharePanel tenant={t} onChanged={load} />}
            </Fragment>
          ))}
          {tenants.length === 0 && <tr><td colSpan={5} className="muted">No tenants yet. Sync from Partner Center or add one below.</td></tr>}
        </tbody>
      </table>

      <fieldset>
        <legend>Add a tenant</legend>
        <p className="muted">
          Already have a GDAP relationship with a customer? Register it directly instead of
          waiting for a full sync -- you become its Owner immediately and can share it from there.
        </p>
        <form className="row" onSubmit={addTenant}>
          <input placeholder="Entra tenant id (GUID)" value={newTenant.tenantId}
            onChange={(e) => setNewTenant({ ...newTenant, tenantId: e.target.value })} />
          <input placeholder="Display name" value={newTenant.displayName}
            onChange={(e) => setNewTenant({ ...newTenant, displayName: e.target.value })} />
          <input placeholder="Default domain (optional)" value={newTenant.defaultDomain}
            onChange={(e) => setNewTenant({ ...newTenant, defaultDomain: e.target.value })} />
          <button>Add tenant</button>
        </form>
      </fieldset>
    </section>
  );
}
