import { useEffect, useState } from "react";
import { api } from "../api";
import type { ConfigSnapshotRun, MeProfile, SectionDiff, Tenant } from "../types";

function badge(kind: string) {
  const cls = kind === "Added" ? "ok" : kind === "Removed" ? "err" : "warn";
  return <span className={`badge ${cls}`}>{kind}</span>;
}

export function ConfigSnapshots({ me }: { me: MeProfile | null }) {
  const [tenants, setTenants] = useState<Tenant[]>([]);
  const [tenantId, setTenantId] = useState("");
  const [runs, setRuns] = useState<ConfigSnapshotRun[]>([]);
  const [beforeRunId, setBeforeRunId] = useState("");
  const [afterRunId, setAfterRunId] = useState("");
  const [diffs, setDiffs] = useState<SectionDiff[] | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [importFile, setImportFile] = useState<File | null>(null);

  useEffect(() => { api.tenants.list().then((t) => { setTenants(t); if (t.length > 0) setTenantId(t[0].id); }); }, []);

  const loadRuns = (id: string) => api.configSnapshots.list(id).then(setRuns).catch((e) => setError(String(e)));
  useEffect(() => { if (tenantId) { setDiffs(null); loadRuns(tenantId); } }, [tenantId]);

  const canOperate = me === null || me.tenantAccess.some((a) => a.tenantId === tenantId && a.role !== "Viewer");

  const capture = async () => {
    setBusy(true); setError(null);
    try { await api.configSnapshots.capture(tenantId); await loadRuns(tenantId); }
    catch (e) { setError(String(e)); }
    finally { setBusy(false); }
  };

  const viewDiff = async () => {
    if (!beforeRunId || !afterRunId) return;
    setBusy(true); setError(null);
    try { setDiffs(await api.configSnapshots.diff(tenantId, beforeRunId, afterRunId)); }
    catch (e) { setError(String(e)); }
    finally { setBusy(false); }
  };

  const exportPatch = () => beforeRunId && afterRunId &&
    api.configSnapshots.exportDiff(tenantId, beforeRunId, afterRunId).catch((e) => setError(String(e)));

  const importWorkbook = async () => {
    if (!importFile) return;
    setBusy(true); setError(null);
    try {
      const workbook = JSON.parse(await importFile.text());
      await api.configSnapshots.import(tenantId, workbook.sections);
      setImportFile(null);
      await loadRuns(tenantId);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  const runLabel = (r: ConfigSnapshotRun) =>
    `${new Date(r.startedAt).toLocaleString()} -- ${r.operator}${r.imported ? " (imported)" : ""}`;

  return (
    <section>
      <div className="toolbar">
        <h2>Config Snapshots</h2>
        <div className="row">
          <select value={tenantId} onChange={(e) => setTenantId(e.target.value)}>
            {tenants.map((t) => <option key={t.id} value={t.id}>{t.displayName}</option>)}
          </select>
          {canOperate && <button onClick={capture} disabled={busy || !tenantId}>{busy ? "Working…" : "Take Snapshot"}</button>}
        </div>
      </div>
      <p className="muted">
        Point-in-time backups of this tenant's configuration (Conditional Access, Named Locations,
        Device Compliance Policies), diffable against each other. There is no "apply" button --
        making changes stays the job of the Deploy wizard and known-fix Workflows, where every
        write is a single reviewed action.
      </p>
      {error && <p className="error">{error}</p>}

      <table>
        <thead><tr><th>Captured</th><th>Sections</th><th>Status</th><th>Git</th><th></th></tr></thead>
        <tbody>
          {runs.map((r) => (
            <tr key={r.id}>
              <td>{runLabel(r)}</td>
              <td>{r.sections.map((s) => `${s.sectionName} (${s.failed ? "failed" : s.itemCount})`).join(", ")}</td>
              <td><span className={`badge ${r.succeeded ? "ok" : "err"}`}>{r.succeeded ? "ok" : "partial failure"}</span></td>
              <td className="mono">{r.gitCommitSha ? r.gitCommitSha.slice(0, 8) : "—"}</td>
              <td><button onClick={() => api.configSnapshots.exportRun(tenantId, r.id).catch((e) => setError(String(e)))}>Export</button></td>
            </tr>
          ))}
          {runs.length === 0 && <tr><td colSpan={5} className="muted">No snapshots yet for this tenant.</td></tr>}
        </tbody>
      </table>

      <fieldset>
        <legend>Diff two snapshots</legend>
        <div className="row">
          <select value={beforeRunId} onChange={(e) => setBeforeRunId(e.target.value)}>
            <option value="">before…</option>
            {runs.map((r) => <option key={r.id} value={r.id}>{runLabel(r)}</option>)}
          </select>
          <select value={afterRunId} onChange={(e) => setAfterRunId(e.target.value)}>
            <option value="">after…</option>
            {runs.map((r) => <option key={r.id} value={r.id}>{runLabel(r)}</option>)}
          </select>
          <button onClick={viewDiff} disabled={busy || !beforeRunId || !afterRunId}>View diff</button>
          <button onClick={exportPatch} disabled={!beforeRunId || !afterRunId}>Export as patch</button>
        </div>

        {diffs && (
          <div className="issues">
            {diffs.every((d) => d.changes.length === 0) && <p className="muted">No changes between these two snapshots.</p>}
            {diffs.filter((d) => d.changes.length > 0).map((d) => (
              <div key={d.sectionId} className="password">
                <strong>{d.sectionName}</strong>
                <ul className="issues">
                  {d.changes.map((c) => (
                    <li key={c.itemId}>
                      {badge(c.kind)} {c.label ?? c.itemId}
                      {c.fieldChanges.length > 0 && (
                        <ul className="issues">
                          {c.fieldChanges.map((f) => (
                            <li key={f.field} className="mono">{f.field}: {f.before ?? "(none)"} → {f.after ?? "(none)"}</li>
                          ))}
                        </ul>
                      )}
                    </li>
                  ))}
                </ul>
              </div>
            ))}
          </div>
        )}
      </fieldset>

      {canOperate && (
        <fieldset>
          <legend>Import a workbook</legend>
          <p className="muted">Bring in a snapshot exported from elsewhere for comparison. Never writes to the tenant.</p>
          <div className="row">
            <input type="file" accept="application/json" onChange={(e) => setImportFile(e.target.files?.[0] ?? null)} />
            <button onClick={importWorkbook} disabled={busy || !importFile}>Import</button>
          </div>
        </fieldset>
      )}
    </section>
  );
}
