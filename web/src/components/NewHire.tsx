import { useEffect, useMemo, useRef, useState } from "react";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Checkbox from "@mui/material/Checkbox";
import FormControl from "@mui/material/FormControl";
import FormControlLabel from "@mui/material/FormControlLabel";
import FormGroup from "@mui/material/FormGroup";
import InputLabel from "@mui/material/InputLabel";
import MenuItem from "@mui/material/MenuItem";
import Select from "@mui/material/Select";
import Stack from "@mui/material/Stack";
import TextField from "@mui/material/TextField";
import Typography from "@mui/material/Typography";
import { api } from "../api";
import { useAsyncAction } from "../hooks/useAsyncAction";
import { useToast } from "../hooks/useToast";
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
  const [lastAction, setLastAction] = useState<"tenants" | "directory" | "submit" | null>(null);
  const toast = useToast();

  // Guards against a slower directory/template response for a tenant the user has since switched
  // away from silently overwriting the currently selected tenant's state (e.g. tenant A's license
  // selections landing on tenant B's form after a quick A -> B switch). Neither fetch below is
  // cancellable, so instead each commits its result only if it's still for the tenant that's
  // current at the time it resolves.
  const currentTenantRef = useRef("");

  const tenantsAction = useAsyncAction(async () => {
    setTenants(await api.tenants.list());
  });

  const directoryAction = useAsyncAction(async (id: string) => {
    const [loadedSkus, loadedGroups] = await Promise.all([api.directory.skus(id), api.directory.groups(id)]);
    if (currentTenantRef.current !== id) return;
    setSkus(loadedSkus);
    setGroups(loadedGroups);
  });

  const upn = useMemo(
    () => (form.mailNickname && form.upnDomain ? `${form.mailNickname}@${form.upnDomain}` : ""),
    [form.mailNickname, form.upnDomain]
  );
  const displayName = `${form.givenName} ${form.surname}`.trim();

  const submitAction = useAsyncAction(async () => {
    const hireResult = await api.provisioning.hire(tenantId, {
      displayName, givenName: form.givenName, surname: form.surname,
      userPrincipalName: upn, mailNickname: form.mailNickname, usageLocation: form.usageLocation,
      jobTitle: form.jobTitle || undefined, department: form.department || undefined,
      licenseSkuIds: [...licenseSkuIds], groupIds: [...groupIds]
    });
    setResult(hireResult);
    if (hireResult.succeeded) toast(`${displayName} provisioned`, "success");
  });

  useEffect(() => {
    setLastAction("tenants");
    void tenantsAction.run();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // On tenant change, load its directory + prefill from the contract's provisioning template.
  useEffect(() => {
    currentTenantRef.current = tenantId;
    // Clear immediately so a switch never leaves the previous tenant's directory/prefill on
    // screen while the new tenant's own fetch is in flight (or, in the case of directoryAction's
    // shared busy-mutex, silently dropped because a prior fetch for a different tenant hadn't
    // settled yet -- either way, stale-looking data never lingers).
    setSkus([]);
    setGroups([]);
    if (!tenantId) return;
    setResult(null);
    setLastAction("directory");
    void directoryAction.run(tenantId);
    const tenant = tenants.find((t) => t.id === tenantId);
    if (tenant?.contractId) {
      const requestedTenantId = tenantId;
      api.provisioning.getTemplate(tenant.contractId).then((tpl) => {
        if (!tpl || currentTenantRef.current !== requestedTenantId) return;
        setForm((f) => ({
          ...f, usageLocation: tpl.usageLocation, upnDomain: tpl.upnDomain ?? f.upnDomain,
          jobTitle: tpl.defaultJobTitle ?? f.jobTitle, department: tpl.defaultDepartment ?? f.department
        }));
        setLicenseSkuIds(new Set(tpl.licenseSkuIds));
        setGroupIds(new Set(tpl.groupIds));
      }).catch(() => {});
    }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tenantId, tenants]);

  const error =
    lastAction === "tenants" ? tenantsAction.error :
    lastAction === "directory" ? directoryAction.error :
    lastAction === "submit" ? submitAction.error :
    null;

  const toggle = (set: Set<string>, setter: (s: Set<string>) => void, id: string) => {
    const next = new Set(set);
    next.has(id) ? next.delete(id) : next.add(id);
    setter(next);
  };

  const submit = () => {
    if (!tenantId || !displayName || !upn || !form.mailNickname) return;
    setResult(null);
    setLastAction("submit");
    void submitAction.run();
  };

  return (
    <Box component="section">
      <Typography variant="h5" component="h2" gutterBottom>
        New hire
      </Typography>
      <FormControl fullWidth sx={{ maxWidth: 360, mb: 2 }}>
        <InputLabel id="new-hire-tenant-label">Tenant</InputLabel>
        <Select labelId="new-hire-tenant-label" label="Tenant" value={tenantId} onChange={(e) => setTenantId(e.target.value)} displayEmpty>
          <MenuItem value="">-- choose --</MenuItem>
          {tenants.map((t) => <MenuItem key={t.id} value={t.id}>{t.displayName}</MenuItem>)}
        </Select>
      </FormControl>

      {tenantId && (
        <Stack spacing={2}>
          <Box sx={{ display: "grid", gridTemplateColumns: { xs: "1fr", sm: "repeat(2, minmax(0, 1fr))" }, gap: 1 }}>
            <TextField fullWidth label="First name" value={form.givenName} onChange={(e) => setForm({ ...form, givenName: e.target.value })} />
            <TextField fullWidth label="Last name" value={form.surname} onChange={(e) => setForm({ ...form, surname: e.target.value })} />
            <TextField fullWidth label="Mail nickname (e.g. ada)" value={form.mailNickname} onChange={(e) => setForm({ ...form, mailNickname: e.target.value })} />
            <TextField fullWidth label="UPN domain (e.g. contoso.com)" value={form.upnDomain} onChange={(e) => setForm({ ...form, upnDomain: e.target.value })} />
            <TextField fullWidth label="Job title" value={form.jobTitle} onChange={(e) => setForm({ ...form, jobTitle: e.target.value })} />
            <TextField fullWidth label="Department" value={form.department} onChange={(e) => setForm({ ...form, department: e.target.value })} />
            <TextField fullWidth label="Usage location" value={form.usageLocation} onChange={(e) => setForm({ ...form, usageLocation: e.target.value })} />
          </Box>
          <Typography variant="body2" color="text.secondary">
            UPN: <Typography component="span" sx={{ fontFamily: "monospace" }}>{upn || "(none)"}</Typography> - Display: <Typography component="span" sx={{ fontFamily: "monospace" }}>{displayName || "(none)"}</Typography>
          </Typography>

          <Box component="fieldset" sx={{ border: 1, borderColor: "divider", borderRadius: 1, p: 2 }}>
            <Typography component="legend" variant="subtitle1">Licenses</Typography>
            <FormGroup>
              {skus.map((s) => (
                <FormControlLabel key={s.skuId} control={<Checkbox checked={licenseSkuIds.has(s.skuId)} onChange={() => toggle(licenseSkuIds, setLicenseSkuIds, s.skuId)} />} label={`${s.skuPartNumber} (${s.consumed}/${s.enabled})`} />
              ))}
            </FormGroup>
            {skus.length === 0 && <Typography variant="body2" color="text.secondary">No SKUs loaded.</Typography>}
          </Box>

          <Box component="fieldset" sx={{ border: 1, borderColor: "divider", borderRadius: 1, p: 2 }}>
            <Typography component="legend" variant="subtitle1">Groups</Typography>
            <FormGroup>
              {groups.map((g) => (
                <FormControlLabel key={g.id} control={<Checkbox checked={groupIds.has(g.id)} onChange={() => toggle(groupIds, setGroupIds, g.id)} />} label={g.displayName} />
              ))}
            </FormGroup>
            {groups.length === 0 && <Typography variant="body2" color="text.secondary">No groups loaded.</Typography>}
          </Box>

          <Box>
            <Button variant="contained" onClick={submit} disabled={submitAction.busy || !displayName || !upn}>
              {submitAction.busy ? "Creating..." : "Create user"}
            </Button>
          </Box>
        </Stack>
      )}
      {error && <Alert severity="error" sx={{ mt: 2 }}>{error}</Alert>}
      {result && <StepList result={result} />}
    </Box>
  );
}