import { useEffect, useMemo, useState } from "react";
import { api } from "../api";
import type { DiagnosisResult, Finding, Tenant, WorkflowRunRecord, WorkflowRunResult, WorkflowSummary } from "../types";
import { StepList } from "./StepList";
import type { WorkflowLaunch } from "./UserSearch";

const badgeClass: Record<Finding["status"], string> = {
  Ok: "succeeded", Info: "uptodate", Warning: "pending", Blocker: "failed"
};

function Findings({ result, title }: { result: DiagnosisResult; title: string }) {
  return (
    <div className="plan">
      <h3>{title} {result.healthy ? <span className="badge succeeded">healthy</span> : <span className="badge failed">needs fixing</span>}</h3>
      <table>
        <thead><tr><th>Check</th><th>Status</th><th>Detail</th></tr></thead>
        <tbody>
          {result.findings.map((f, i) => (
            <tr key={i}>
              <td>{f.name}</td>
              <td><span className={`badge ${badgeClass[f.status]}`}>{f.status}</span></td>
              <td className="mono">{f.detail ?? ""}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

export function Workflows({ prefill }: { prefill?: WorkflowLaunch | null }) {
  const [catalog, setCatalog] = useState<WorkflowSummary[]>([]);
  const [tenants, setTenants] = useState<Tenant[]>([]);
  const [tenantId, setTenantId] = useState("");
  const [selectedId, setSelectedId] = useState("");
  const [inputs, setInputs] = useState<Record<string, string>>({});
  const [diagnosis, setDiagnosis] = useState<DiagnosisResult | null>(null);
  const [run, setRun] = useState<WorkflowRunResult | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [runs, setRuns] = useState<WorkflowRunRecord[]>([]);

  const loadRuns = () => api.workflows.runs({ take: 25 }).then(setRuns).catch(() => {});

  useEffect(() => {
    Promise.all([api.workflows.list(), api.tenants.list()])
      .then(([w, t]) => { setCatalog(w); setTenants(t); })
      .catch((e) => setError(String(e)));
    loadRuns();
  }, []);

  const selected = useMemo(() => catalog.find((w) => w.id === selectedId), [catalog, selectedId]);

  // Arriving from Find User: select the workflow, tenant, and inputs in one go.
  useEffect(() => {
    if (!prefill || catalog.length === 0) return;
    const w = catalog.find((x) => x.id === prefill.workflowId);
    if (!w) return;
    setSelectedId(w.id);
    setTenantId(prefill.tenantId);
    setInputs({ ...Object.fromEntries(w.inputs.map((i) => [i.key, i.default ?? ""])), ...prefill.inputs });
    setDiagnosis(null); setRun(null); setError(null);
  }, [prefill, catalog]);

  const pick = (id: string) => {
    setSelectedId(id);
    setDiagnosis(null); setRun(null); setError(null);
    const w = catalog.find((x) => x.id === id);
    setInputs(Object.fromEntries((w?.inputs ?? []).map((i) => [i.key, i.default ?? ""])));
  };

  const ready = Boolean(tenantId && selected &&
    selected.inputs.filter((i) => i.required).every((i) => (inputs[i.key] ?? "").trim()));

  const call = (label: string, fn: () => Promise<void>) => async () => {
    setBusy(label); setError(null);
    try { await fn(); } catch (e) { setError(String(e)); } finally { setBusy(null); }
  };

  const diagnose = call("diagnose", async () => {
    setRun(null);
    setDiagnosis(await api.workflows.diagnose(selected!.id, tenantId, inputs));
    loadRuns();
  });
  const fix = call("fix", async () => {
    const r = await api.workflows.remediate(selected!.id, tenantId, inputs);
    setRun(r); if (r.postState) setDiagnosis(r.postState);
    loadRuns();
  });

  const grouped = catalog.reduce<Record<string, WorkflowSummary[]>>((acc, w) => {
    (acc[w.category] ??= []).push(w); return acc;
  }, {});

  return (
    <section className="workflows">
      <h2>Workflows</h2>
      <div className="wf-layout">
        <aside className="wf-list">
          {Object.entries(grouped).map(([cat, items]) => (
            <div key={cat}>
              <h4>{cat}</h4>
              {items.map((w) => (
                <button key={w.id} className={selectedId === w.id ? "active" : ""} onClick={() => pick(w.id)}>
                  {w.name}
                </button>
              ))}
            </div>
          ))}
          {catalog.length === 0 && <p className="muted">No workflows.</p>}
        </aside>

        <div className="wf-detail">
          {!selected && <p className="muted">Pick a workflow.</p>}
          {selected && (
            <>
              <p className="muted">{selected.description}</p>
              <label className="field">
                Tenant
                <select value={tenantId} onChange={(e) => setTenantId(e.target.value)}>
                  <option value="">— choose —</option>
                  {tenants.map((t) => <option key={t.id} value={t.id}>{t.displayName}</option>)}
                </select>
              </label>
              {selected.inputs.map((i) => i.type === "bool" ? (
                <label key={i.key} className="check">
                  <input type="checkbox" checked={(inputs[i.key] ?? "true") === "true"}
                    onChange={(e) => setInputs({ ...inputs, [i.key]: String(e.target.checked) })} />
                  {i.label}
                </label>
              ) : (
                <label key={i.key} className="field">
                  {i.label}{i.required ? "" : " (optional)"}
                  <input placeholder={i.placeholder} value={inputs[i.key] ?? ""}
                    onChange={(e) => setInputs({ ...inputs, [i.key]: e.target.value })} />
                </label>
              ))}
              <div className="row">
                <button onClick={diagnose} disabled={!ready || busy !== null}>{busy === "diagnose" ? "Checking…" : "Diagnose"}</button>
                <button onClick={fix} disabled={!ready || busy !== null}>{busy === "fix" ? "Applying…" : "Apply fix"}</button>
              </div>
              {error && <p className="error">{error}</p>}
              {diagnosis && <Findings result={diagnosis} title="Diagnosis" />}
              {run && <StepList result={{ steps: run.steps, succeeded: run.succeeded }} />}
              {run?.ephemeral && Object.entries(run.ephemeral).map(([k, v]) => (
                <p key={k}>{k}: <span className="mono">{v}</span> (shown once - not recorded)</p>
              ))}
            </>
          )}
        </div>
      </div>

      <div className="plan">
        <h3>Recent runs</h3>
        {runs.length === 0 && <p className="muted">No runs recorded yet.</p>}
        {runs.length > 0 && (
          <table>
            <thead>
              <tr><th>When</th><th>Workflow</th><th>Tenant</th><th>Kind</th><th>Operator</th><th>Result</th></tr>
            </thead>
            <tbody>
              {runs.map((r) => (
                <tr key={r.id} title={r.error ?? undefined}>
                  <td>{new Date(r.startedAt).toLocaleString()}</td>
                  <td>{r.workflowName}</td>
                  <td>{r.tenantName}</td>
                  <td>{r.kind}</td>
                  <td>{r.operator}</td>
                  <td>
                    <span className={`badge ${r.succeeded ? "succeeded" : "failed"}`}>
                      {r.succeeded ? "ok" : "failed"}
                    </span>{" "}
                    {r.healthy !== null && r.healthy !== undefined && (
                      <span className={`badge ${r.healthy ? "succeeded" : "pending"}`}>
                        {r.healthy ? "healthy" : "needs fixing"}
                      </span>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </section>
  );
}
