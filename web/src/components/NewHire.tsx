import { useEffect, useMemo, useState } from "react";
import { api } from "../api";
import type { DirectoryObject, ProvisioningResult, Sku, Tenant } from "../types";
import { StepList } from "./StepList";

export function NewHire() {
  const [tenants, setTenants] = useState<Tenant[]>([]);
  const [tenantId, setTenantId] = useState("");
  const [skus, setSkus] = useState<Sku[]>([]);
  const [groups, setGroups] = useState<DirectoryObject[]>([]);
  const [form, setForm] = useState({
    givenName: "", surname: "", mailNickname: "", usageLocation: "US",
    jobTitle: "", department: "", upnDomain: ""
  });
  const [licenseSkuIds, setLicenseSkuIds] = useState<Set<string>>(new Set());
  const [groupIds, setGroupIds] = useState<Set<string>>(new Set());
  const [result, setResult] = useState<ProvisioningResult | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => { api.tenants.list().then(setTenants).catch((e) => setError(String(e))); }, []);

  // On tenant change, load its directory + prefill from the contract's provisioning template.
  useEffect(() => {
    if (!tenantId) { setSkus([]); setGroups([]); return; }
    setError(null); setResult(null);
    const tenant = tenants.find((t) => t.id === tenantId);
    Promise.all([api.directory.skus(tenantId), api.directory.groups(tenantId)])
      .then(([s, g]) => { setSkus(s); setGroups(g); })
      .catch((e) => setError(String(e)));
    if (tenant?.contractId) {
      api.provisioning.getTemplate(tenant.contractId).then((tpl) => {
        if (!tpl) return;
        setForm((f) => ({
          ...f, usageLocation: tpl.usageLocation, upnDomain: tpl.upnDomain ?? f.upnDomain,
          jobTitle: tpl.defaultJobTitle ?? f.jobTitle, department: tpl.defaultDepartment ?? f.department
        }));
        setLicenseSkuIds(new Set(tpl.licenseSkuIds));
        setGroupIds(new Set(tpl.groupIds));
      }).catch(() => {});
    }
  }, [tenantId, tenants]);

  const upn = useMemo(
    () => (form.mailNickname && form.upnDomain ? `${form.mailNickname}@${form.upnDomain}` : ""),
    [form.mailNickname, form.upnDomain]
  );
  const displayName = `${form.givenName} ${form.surname}`.trim();

  const toggle = (set: Set<string>, setter: (s: Set<string>) => void, id: string) => {
    const next = new Set(set);
    next.has(id) ? next.delete(id) : next.add(id);
    setter(next);
  };

  const submit = async () => {
    if (!tenantId || !displayName || !upn || !form.mailNickname) return;
    setBusy(true); setError(null); setResult(null);
    try {
      setResult(await api.provisioning.hire(tenantId, {
        displayName, givenName: form.givenName, surname: form.surname,
        userPrincipalName: upn, mailNickname: form.mailNickname, usageLocation: form.usageLocation,
        jobTitle: form.jobTitle || undefined, department: form.department || undefined,
        licenseSkuIds: [...licenseSkuIds], groupIds: [...groupIds]
      }));
    } catch (e) { setError(String(e)); }
    finally { setBusy(false); }
  };

  return (
    <section>
      <h2>New hire</h2>
      <label className="field">
        Tenant
        <select value={tenantId} onChange={(e) => setTenantId(e.target.value)}>
          <option value="">— choose —</option>
          {tenants.map((t) => <option key={t.id} value={t.id}>{t.displayName}</option>)}
        </select>
      </label>

      {tenantId && (
        <>
          <div className="grid">
            <input placeholder="First name" value={form.givenName} onChange={(e) => setForm({ ...form, givenName: e.target.value })} />
            <input placeholder="Last name" value={form.surname} onChange={(e) => setForm({ ...form, surname: e.target.value })} />
            <input placeholder="Mail nickname (e.g. ada)" value={form.mailNickname} onChange={(e) => setForm({ ...form, mailNickname: e.target.value })} />
            <input placeholder="UPN domain (e.g. contoso.com)" value={form.upnDomain} onChange={(e) => setForm({ ...form, upnDomain: e.target.value })} />
            <input placeholder="Job title" value={form.jobTitle} onChange={(e) => setForm({ ...form, jobTitle: e.target.value })} />
            <input placeholder="Department" value={form.department} onChange={(e) => setForm({ ...form, department: e.target.value })} />
            <input placeholder="Usage location" value={form.usageLocation} onChange={(e) => setForm({ ...form, usageLocation: e.target.value })} />
          </div>
          <p className="muted">UPN: <span className="mono">{upn || "—"}</span> · Display: <span className="mono">{displayName || "—"}</span></p>

          <fieldset>
            <legend>Licenses</legend>
            {skus.map((s) => (
              <label key={s.skuId} className="check">
                <input type="checkbox" checked={licenseSkuIds.has(s.skuId)} onChange={() => toggle(licenseSkuIds, setLicenseSkuIds, s.skuId)} />
                {s.skuPartNumber} <span className="muted">({s.consumed}/{s.enabled})</span>
              </label>
            ))}
            {skus.length === 0 && <p className="muted">No SKUs loaded.</p>}
          </fieldset>

          <fieldset>
            <legend>Groups</legend>
            {groups.map((g) => (
              <label key={g.id} className="check">
                <input type="checkbox" checked={groupIds.has(g.id)} onChange={() => toggle(groupIds, setGroupIds, g.id)} />
                {g.displayName}
              </label>
            ))}
            {groups.length === 0 && <p className="muted">No groups loaded.</p>}
          </fieldset>

          <button onClick={submit} disabled={busy || !displayName || !upn}>
            {busy ? "Creating…" : "Create user"}
          </button>
        </>
      )}
      {error && <p className="error">{error}</p>}
      {result && <StepList result={result} />}
    </section>
  );
}
