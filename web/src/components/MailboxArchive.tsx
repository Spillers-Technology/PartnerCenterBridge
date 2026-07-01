import { useEffect, useState } from "react";
import { api } from "../api";
import type { ArchiveState, ProvisioningStep, Tenant } from "../types";
import { StepList } from "./StepList";

/** Derive the human-readable problems from a state snapshot so the fix is transparent. */
function issues(s: ArchiveState): { label: string; severity: "blocker" | "warn" }[] {
  const out: { label: string; severity: "blocker" | "warn" }[] = [];
  if (!s.archiveEnabled) out.push({ label: "Archive mailbox not enabled", severity: "blocker" });
  if (s.retentionHoldEnabled) out.push({ label: "Retention hold is ON — the assistant won't move items", severity: "blocker" });
  if (s.elcProcessingDisabled) out.push({ label: "ELC processing disabled — the assistant is turned off for this mailbox", severity: "blocker" });
  if (!s.retentionPolicy) out.push({ label: "No retention policy assigned — nothing tells items to move", severity: "blocker" });
  if (!s.autoExpandingArchiveEnabled) out.push({ label: "Auto-expanding archive off — archive can hit its quota", severity: "warn" });
  return out;
}

function StateTable({ s }: { s: ArchiveState }) {
  const row = (k: string, v: React.ReactNode, bad?: boolean) => (
    <tr><td>{k}</td><td className={bad ? "error" : "mono"}>{v}</td></tr>
  );
  return (
    <table>
      <tbody>
        {row("Primary size", `${s.primarySize} (${s.primaryItemCount} items)`)}
        {row("Send/receive quota", s.prohibitSendReceiveQuota)}
        {row("Archive enabled", String(s.archiveEnabled), !s.archiveEnabled)}
        {row("Auto-expanding archive", String(s.autoExpandingArchiveEnabled), !s.autoExpandingArchiveEnabled)}
        {row("Archive size", `${s.archiveSize ?? "—"} (${s.archiveItemCount} items)`)}
        {row("Archive quota", s.archiveQuota ?? "—")}
        {row("Retention policy", s.retentionPolicy || "(none)", !s.retentionPolicy)}
        {row("Retention hold", String(s.retentionHoldEnabled), s.retentionHoldEnabled)}
        {row("ELC processing disabled", String(s.elcProcessingDisabled), s.elcProcessingDisabled)}
      </tbody>
    </table>
  );
}

export function MailboxArchive() {
  const [tenants, setTenants] = useState<Tenant[]>([]);
  const [tenantId, setTenantId] = useState("");
  const [identity, setIdentity] = useState("");
  const [state, setState] = useState<ArchiveState | null>(null);
  const [steps, setSteps] = useState<ProvisioningStep[] | null>(null);
  const [opts, setOpts] = useState({
    enableAutoExpandingArchive: true, retentionPolicyName: "Default MRM Policy",
    clearProcessingBlocks: true, triggerProcessing: true
  });
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => { api.tenants.list().then(setTenants).catch((e) => setError(String(e))); }, []);

  const run = async (label: string, fn: () => Promise<void>) => {
    setBusy(label); setError(null);
    try { await fn(); } catch (e) { setError(String(e)); } finally { setBusy(null); }
  };

  const diagnose = () => run("diagnose", async () => {
    setSteps(null);
    setState(await api.exchange.archiveState(tenantId, identity));
  });
  const fix = () => run("fix", async () => {
    const r = await api.exchange.remediateArchive(tenantId, identity, opts);
    setSteps(r.steps); if (r.state) setState(r.state);
  });
  const nudge = () => run("nudge", async () => {
    const r = await api.exchange.nudgeArchive(tenantId, identity);
    setSteps(r.steps); if (r.state) setState(r.state);
  });

  const ready = Boolean(tenantId && identity);
  const found = state ? issues(state) : [];

  return (
    <section>
      <h2>Mailbox archive — fix “full / not archiving”</h2>
      <p className="muted">
        Enables the archive + auto-expand, ensures a retention policy, clears the hidden blockers
        (retention hold / ELC processing), and kicks the Managed Folder Assistant. The move runs
        <em> asynchronously</em>, so re-run <strong>Nudge</strong> and watch the archive size climb.
      </p>

      <div className="row">
        <select value={tenantId} onChange={(e) => { setTenantId(e.target.value); setState(null); setSteps(null); }}>
          <option value="">— tenant —</option>
          {tenants.map((t) => <option key={t.id} value={t.id}>{t.displayName}</option>)}
        </select>
        <input placeholder="Mailbox UPN (e.g. user@contoso.com)" value={identity} onChange={(e) => setIdentity(e.target.value)} />
        <button onClick={diagnose} disabled={!ready || busy !== null}>{busy === "diagnose" ? "Checking…" : "Diagnose"}</button>
      </div>
      {error && <p className="error">{error}</p>}

      {state && (
        <>
          <h3>Current state</h3>
          {found.length > 0 ? (
            <ul className="issues">
              {found.map((i, n) => (
                <li key={n}><span className={`badge ${i.severity === "blocker" ? "failed" : "pending"}`}>{i.severity}</span> {i.label}</li>
              ))}
            </ul>
          ) : <p className="badge succeeded">No blocking issues detected</p>}
          <StateTable s={state} />

          <fieldset>
            <legend>Fix options</legend>
            <label className="check"><input type="checkbox" checked={opts.enableAutoExpandingArchive} onChange={(e) => setOpts({ ...opts, enableAutoExpandingArchive: e.target.checked })} /> Enable auto-expanding archive</label>
            <label className="check"><input type="checkbox" checked={opts.clearProcessingBlocks} onChange={(e) => setOpts({ ...opts, clearProcessingBlocks: e.target.checked })} /> Clear retention hold / ELC blocks</label>
            <label className="check"><input type="checkbox" checked={opts.triggerProcessing} onChange={(e) => setOpts({ ...opts, triggerProcessing: e.target.checked })} /> Trigger assistant now</label>
            <label className="field">Retention policy if none assigned
              <input value={opts.retentionPolicyName} onChange={(e) => setOpts({ ...opts, retentionPolicyName: e.target.value })} />
            </label>
          </fieldset>

          <div className="row">
            <button onClick={fix} disabled={busy !== null}>{busy === "fix" ? "Applying…" : "Apply fix"}</button>
            <button onClick={nudge} disabled={busy !== null}>{busy === "nudge" ? "Nudging…" : "Nudge (re-trigger + re-check)"}</button>
          </div>
        </>
      )}

      {steps && <StepList result={{ steps, succeeded: steps.every((s) => s.success) }} />}
    </section>
  );
}
