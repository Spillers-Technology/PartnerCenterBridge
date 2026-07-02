import { useState } from "react";
import { api } from "../api";
import type { GlobalSearchResult } from "../types";

export interface WorkflowLaunch {
  workflowId: string;
  tenantId: string;
  inputs: Record<string, string>;
}

/** Person-first workflow shortcuts shown per hit; userUpn is the shared input key. */
const ACTIONS: { workflowId: string; label: string }[] = [
  { workflowId: "mfa-reset", label: "MFA reset" },
  { workflowId: "password-reset", label: "Password reset" },
  { workflowId: "compromised-lockdown", label: "Lockdown" },
  { workflowId: "license-repair", label: "License repair" }
];

export function UserSearch({ onLaunch }: { onLaunch: (launch: WorkflowLaunch) => void }) {
  const [q, setQ] = useState("");
  const [result, setResult] = useState<GlobalSearchResult | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const search = async () => {
    if (q.trim().length < 3) { setError("Type at least 3 characters."); return; }
    setBusy(true); setError(null);
    try { setResult(await api.search.users(q.trim())); }
    catch (e) { setError(String(e)); }
    finally { setBusy(false); }
  };

  return (
    <section>
      <h2>Find user</h2>
      <p className="muted">Search every active tenant at once - start from the person, not the portal.</p>

      <div className="row">
        <input placeholder="Name or UPN (min 3 chars)" value={q}
          onChange={(e) => setQ(e.target.value)}
          onKeyDown={(e) => { if (e.key === "Enter") search(); }} />
        <button onClick={search} disabled={busy}>{busy ? "Searching…" : "Search"}</button>
      </div>
      {error && <p className="error">{error}</p>}

      {result && (
        <>
          <p className="muted">
            {result.hits.length} match(es) across {result.tenantsSearched} tenant(s)
            {result.errors.length > 0 && ` - ${result.errors.length} tenant(s) unreachable`}
          </p>

          {result.hits.length > 0 && (
            <table>
              <thead><tr><th>User</th><th>UPN</th><th>Tenant</th><th>Fix something</th></tr></thead>
              <tbody>
                {result.hits.map((h) => (
                  <tr key={`${h.tenantId}:${h.id}`}>
                    <td>{h.displayName}</td>
                    <td className="mono">{h.userPrincipalName ?? ""}</td>
                    <td>{h.tenantName}</td>
                    <td>
                      <div className="row-actions">
                        {ACTIONS.map((a) => (
                          <button key={a.workflowId} onClick={() => onLaunch({
                            workflowId: a.workflowId,
                            tenantId: h.tenantId,
                            inputs: { userUpn: h.userPrincipalName ?? h.id }
                          })}>
                            {a.label}
                          </button>
                        ))}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}

          {result.errors.length > 0 && (
            <div className="plan">
              <h3>Unreachable tenants</h3>
              <table>
                <thead><tr><th>Tenant</th><th>Error</th></tr></thead>
                <tbody>
                  {result.errors.map((e) => (
                    <tr key={e.tenantId}><td>{e.tenantName}</td><td className="muted">{e.message}</td></tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </>
      )}
    </section>
  );
}
