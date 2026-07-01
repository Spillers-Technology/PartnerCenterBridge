import { useEffect, useState } from "react";
import { api } from "../api";
import type { DirectoryObject, ProvisioningResult, Tenant } from "../types";
import { StepList } from "./StepList";

export function Offboard() {
  const [tenants, setTenants] = useState<Tenant[]>([]);
  const [tenantId, setTenantId] = useState("");
  const [search, setSearch] = useState("");
  const [users, setUsers] = useState<DirectoryObject[]>([]);
  const [userId, setUserId] = useState("");
  const [opts, setOpts] = useState({ blockSignIn: true, revokeSessions: true, removeLicenses: true, removeFromGroups: true });
  const [result, setResult] = useState<ProvisioningResult | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => { api.tenants.list().then(setTenants).catch((e) => setError(String(e))); }, []);

  const find = async () => {
    if (!tenantId) return;
    setError(null);
    try { setUsers(await api.directory.users(tenantId, search || undefined)); }
    catch (e) { setError(String(e)); }
  };

  const submit = async () => {
    if (!tenantId || !userId) return;
    setBusy(true); setError(null); setResult(null);
    try { setResult(await api.provisioning.terminate(tenantId, { userId, ...opts })); }
    catch (e) { setError(String(e)); }
    finally { setBusy(false); }
  };

  return (
    <section>
      <h2>Offboard</h2>
      <label className="field">
        Tenant
        <select value={tenantId} onChange={(e) => { setTenantId(e.target.value); setUsers([]); setUserId(""); }}>
          <option value="">— choose —</option>
          {tenants.map((t) => <option key={t.id} value={t.id}>{t.displayName}</option>)}
        </select>
      </label>

      {tenantId && (
        <>
          <div className="row">
            <input placeholder="Search name or UPN" value={search} onChange={(e) => setSearch(e.target.value)} />
            <button onClick={find}>Search users</button>
          </div>
          {users.length > 0 && (
            <label className="field">
              User
              <select value={userId} onChange={(e) => setUserId(e.target.value)}>
                <option value="">— choose —</option>
                {users.map((u) => <option key={u.id} value={u.id}>{u.displayName} ({u.userPrincipalName})</option>)}
              </select>
            </label>
          )}

          <fieldset>
            <legend>Actions</legend>
            {([
              ["blockSignIn", "Block sign-in"],
              ["revokeSessions", "Revoke sessions"],
              ["removeLicenses", "Remove licenses"],
              ["removeFromGroups", "Remove from groups"]
            ] as const).map(([key, label]) => (
              <label key={key} className="check">
                <input type="checkbox" checked={opts[key]} onChange={(e) => setOpts({ ...opts, [key]: e.target.checked })} />
                {label}
              </label>
            ))}
          </fieldset>

          <button onClick={submit} disabled={busy || !userId}>{busy ? "Offboarding…" : "Offboard user"}</button>
        </>
      )}
      {error && <p className="error">{error}</p>}
      {result && <StepList result={result} />}
    </section>
  );
}
