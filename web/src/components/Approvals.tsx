import { useEffect, useState } from "react";
import { api } from "../api";
import type { PendingAction } from "../types";

type Action = "approve" | "reject" | "retry";

export function Approvals() {
  const [items, setItems] = useState<PendingAction[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);

  const load = () => api.pendingActions.list().then(setItems).catch((e) => setError(String(e)));
  useEffect(() => { load(); }, []);

  const decide = async (id: string, action: Action) => {
    setBusyId(id);
    setError(null);
    try {
      if (action === "approve") await api.pendingActions.approve(id);
      else if (action === "reject") await api.pendingActions.reject(id);
      else await api.pendingActions.retry(id);
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusyId(null);
    }
  };

  return (
    <section>
      <h2>Approvals</h2>
      <p className="muted">
        Mutating actions requested through MCP land here for tenants in the default Queue approval
        mode. Nothing runs until you approve it.
      </p>
      {error && <p className="error">{error}</p>}
      {items.length === 0 && <p className="muted">No pending approvals.</p>}
      {items.length > 0 && (
        <table>
          <thead><tr><th>Tenant</th><th>Action</th><th>Status</th><th>Preview</th><th>Requested</th><th>Expires</th><th></th></tr></thead>
          <tbody>
            {items.map((item) => {
              const failed = item.status === "Approved" && Boolean(item.executionError);
              return (
                <tr key={item.id}>
                  <td>{item.tenantName}</td>
                  <td>{item.actionType}</td>
                  <td><span className={`badge ${failed ? "failed" : "pending"}`}>{failed ? "Failed" : "Pending"}</span></td>
                  <td>
                    {item.previewSummary}
                    {failed && <div className="error">{item.executionError}</div>}
                  </td>
                  <td>{new Date(item.createdAt).toLocaleString()}</td>
                  <td>{new Date(item.expiresAt).toLocaleString()}</td>
                  <td>
                    {failed ? (
                      <button disabled={busyId === item.id} onClick={() => decide(item.id, "retry")}>Retry</button>
                    ) : (
                      <>
                        <button disabled={busyId === item.id} onClick={() => decide(item.id, "approve")}>Approve</button>
                        <button disabled={busyId === item.id} onClick={() => decide(item.id, "reject")}>Reject</button>
                      </>
                    )}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
    </section>
  );
}
