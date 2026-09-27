import { useEffect, useMemo, useRef, useState } from "react";
import Alert from "@mui/material/Alert";
import AlertTitle from "@mui/material/AlertTitle";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Card from "@mui/material/Card";
import CardContent from "@mui/material/CardContent";
import Checkbox from "@mui/material/Checkbox";
import MenuItem from "@mui/material/MenuItem";
import Skeleton from "@mui/material/Skeleton";
import Stack from "@mui/material/Stack";
import TextField from "@mui/material/TextField";
import Typography from "@mui/material/Typography";
import CompareArrows from "@mui/icons-material/CompareArrows";
import { api, errorText, isApiStatus } from "../api";
import { pluralize } from "../format";
import { useConfirm } from "../hooks/useConfirm";
import { hasTenantRole, tenantRole } from "../permissions";
import type { DirectoryObject, OperationEvidence, OperationPlan, PlanItem, Tenant } from "../types";
import { useWorkbench } from "../workbench";
import { AccessNotice } from "./AccessNotice";
import { EvidencePanel } from "./EvidencePanel";
import { CategoryChip, FindingList, SentenceList } from "./operationUi";
import { UserPicker } from "./UserPicker";

export interface AccessParityParams { tenant?: string; source?: string; target?: string }

/** Why PCB leaves a kind of group alone, as section headings for the "not copied" list. */
const NOT_COPIED_GROUPS: { key: string; title: string; categories: string[] }[] = [
  { key: "dynamic", title: "Dynamic groups", categories: ["Dynamic"] },
  { key: "role", title: "Role-assignable groups", categories: ["RoleAssignable"] },
  { key: "synced", title: "Synced from on-premises AD", categories: ["OnPremSynced"] },
  { key: "mail", title: "Mail-enabled groups and distribution lists", categories: ["MailEnabledSecurity", "Distribution"] },
  { key: "roles", title: "Directory roles", categories: ["DirectoryRole"] },
  { key: "other", title: "Other", categories: [] }
];

function groupNotCopied(items: PlanItem[]) {
  const known = new Set(NOT_COPIED_GROUPS.flatMap((g) => g.categories));
  return NOT_COPIED_GROUPS
    .map((g) => ({
      ...g,
      items: items.filter((i) => (g.categories.length > 0 ? g.categories.includes(i.category) : !known.has(i.category)))
    }))
    .filter((g) => g.items.length > 0);
}

/** A user named only by id/UPN in the URL: shown as-is until the directory search resolves it. */
function placeholder(ref: string): DirectoryObject {
  return { id: ref, displayName: ref, userPrincipalName: ref.includes("@") ? ref : undefined };
}

function userRef(u: DirectoryObject | null): string | undefined {
  return u ? u.userPrincipalName ?? u.id : undefined;
}

/**
 * Access Parity: plan, review and apply additive group-membership mirroring from a source user to a
 * target user. The tenant and both people live in the URL; a link that names all three compares
 * once on load (operator's choice, 2026-09-26: speed over the extra audit run a refresh creates --
 * every plan is still recorded as a read-only Plan run). Otherwise the plan runs on Compare.
 */
export function AccessParity({
  params,
  onParamsChange
}: {
  params: AccessParityParams;
  onParamsChange: (next: AccessParityParams) => void;
}) {
  const { me } = useWorkbench();
  const confirm = useConfirm();
  const [tenants, setTenants] = useState<Tenant[] | null>(null);
  const [tenantsError, setTenantsError] = useState<string | null>(null);
  const [tenantId, setTenantId] = useState(params.tenant ?? "");
  const [source, setSource] = useState<DirectoryObject | null>(params.source ? placeholder(params.source) : null);
  const [target, setTarget] = useState<DirectoryObject | null>(params.target ? placeholder(params.target) : null);
  const [plan, setPlan] = useState<OperationPlan | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [evidence, setEvidence] = useState<OperationEvidence | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<"plan" | "apply" | null>(null);
  const [unsupported, setUnsupported] = useState(false);
  const generation = useRef(0);
  // Synchronous re-entry guard: `busy` only updates on the next render, so two calls in the same
  // tick could both pass the state check and queue two confirmations.
  const applying = useRef(false);

  useEffect(() => {
    api.tenants.list().then(setTenants).catch((e) => setTenantsError(e instanceof Error ? e.message : String(e)));
  }, []);

  // Resolve people named in the URL to real directory entries (for their names), best effort.
  useEffect(() => {
    if (!params.tenant) return;
    const resolve = (ref: string | undefined, set: (u: DirectoryObject) => void) => {
      if (!ref) return;
      api.directory.users(params.tenant!, ref)
        .then((users) => {
          const wanted = ref.toLowerCase();
          const hit = users.find((u) => u.id.toLowerCase() === wanted || u.userPrincipalName?.toLowerCase() === wanted);
          if (hit) set(hit);
        })
        .catch(() => { /* keep the placeholder; the plan call still works with an id or UPN */ });
    };
    resolve(params.source, (u) => setSource((cur) => (cur && cur.id === params.source ? u : cur)));
    resolve(params.target, (u) => setTarget((cur) => (cur && cur.id === params.target ? u : cur)));
    // Only the initial URL is resolved; later picks come from the directory already.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const tenant = tenants?.find((t) => t.id === tenantId);
  const tenantName = tenant?.displayName ?? "this tenant";
  const role = tenantId ? tenantRole(me, tenantId) : null;
  const canApply = Boolean(tenantId) && hasTenantRole(me, tenantId, "Operator");
  const sameUser = Boolean(source && target && (source.id === target.id ||
    (source.userPrincipalName && source.userPrincipalName.toLowerCase() === target.userPrincipalName?.toLowerCase())));
  const ready = Boolean(tenantId && source && target && !sameUser);

  const invalidate = () => {
    generation.current++;
    setPlan(null);
    setEvidence(null);
    setError(null);
    setSelected(new Set());
  };

  const update = (next: { tenant?: string; source?: DirectoryObject | null; target?: DirectoryObject | null }) => {
    invalidate();
    const t = next.tenant !== undefined ? next.tenant : tenantId;
    const s = next.source !== undefined ? next.source : source;
    const g = next.target !== undefined ? next.target : target;
    if (next.tenant !== undefined) setTenantId(next.tenant);
    if (next.source !== undefined) setSource(next.source);
    if (next.target !== undefined) setTarget(next.target);
    onParamsChange({ tenant: t || undefined, source: userRef(s), target: userRef(g) });
  };

  const compare = async () => {
    if (!ready || busy) return;
    const gen = ++generation.current;
    setBusy("plan");
    setError(null);
    setEvidence(null);
    try {
      const p = await api.accessParity.plan(tenantId, source!.id, target!.id);
      if (gen !== generation.current) return;
      setPlan(p);
      setSelected(new Set(p.items.filter((i) => i.eligible).map((i) => i.id)));
    } catch (e) {
      if (gen !== generation.current) return;
      if (isApiStatus(e, 404) && !errorText(e).toLowerCase().includes("tenant")) setUnsupported(true);
      setError(errorText(e));
    } finally {
      setBusy(null);
    }
  };

  // A fully specified link compares once on mount; later edits go back to explicit Compare.
  const autoCompared = useRef(false);
  useEffect(() => {
    if (autoCompared.current || !params.tenant || !params.source || !params.target) return;
    if (params.source.toLowerCase() === params.target.toLowerCase()) return;
    autoCompared.current = true;
    void compare();
    // Initial URL only, by design.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const eligible = useMemo(() => plan?.items.filter((i) => i.eligible) ?? [], [plan]);
  const already = useMemo(() => plan?.items.filter((i) => !i.eligible && i.category === "AlreadyMember") ?? [], [plan]);
  const notCopied = useMemo(
    () => groupNotCopied(plan?.items.filter((i) => !i.eligible && i.category !== "AlreadyMember") ?? []),
    [plan]
  );
  const chosen = eligible.filter((i) => selected.has(i.id));
  const targetName = plan?.target.displayName || target?.displayName || "the target";
  const sourceName = source?.displayName ?? "the source";

  const apply = async () => {
    if (!plan || !canApply || busy || applying.current || chosen.length === 0) return;
    applying.current = true;
    setBusy("apply");
    try {
      const ok = await confirm({
        title: `Add ${targetName} to ${pluralize(chosen.length, "group")}?`,
        message: `In ${tenantName}, PCB will add ${targetName} to exactly these groups, then re-read their memberships to verify. Nothing is removed.`,
        items: chosen.map((i) => i.objectName),
        itemsLabel: "Groups to add",
        confirmLabel: `Add to ${pluralize(chosen.length, "group")}`,
        mutating: true
      });
      if (!ok) return;
      const gen = generation.current;
      setError(null);
      const result = await api.accessParity.apply(tenantId, source!.id, target!.id, chosen.map((i) => i.id));
      if (gen !== generation.current) return;
      setEvidence(result);
      setPlan(null);
    } catch (e) {
      setError(errorText(e));
    } finally {
      applying.current = false;
      setBusy(null);
    }
  };

  const toggle = (id: string, on: boolean) =>
    setSelected((prev) => {
      const next = new Set(prev);
      if (on) next.add(id); else next.delete(id);
      return next;
    });

  return (
    <Stack spacing={2}>
      <Card variant="outlined">
        <CardContent>
          <Box sx={{ display: "grid", gridTemplateColumns: { xs: "1fr", md: "repeat(3, minmax(0, 1fr))" }, gap: 2 }}>
            <TextField
              select
              label="Tenant"
              value={tenants && tenantId && !tenant ? "" : tenantId}
              onChange={(e) => update({ tenant: e.target.value, source: null, target: null })}
              disabled={busy !== null}
              helperText={tenantsError ? `Tenants couldn't load: ${tenantsError}` : undefined}
              error={Boolean(tenantsError)}
              slotProps={{ select: { displayEmpty: true }, inputLabel: { shrink: true } }}
            >
              <MenuItem value=""><em>Choose a tenant</em></MenuItem>
              {(tenants ?? []).map((t) => <MenuItem key={t.id} value={t.id}>{t.displayName}</MenuItem>)}
            </TextField>
            <UserPicker
              id="parity-source"
              tenantId={tenantId}
              label="Copy access from"
              value={source}
              onChange={(u) => update({ source: u })}
              excludeId={target?.id}
              disabled={busy !== null}
              helperText="The colleague whose groups to mirror"
            />
            <UserPicker
              id="parity-target"
              tenantId={tenantId}
              label="Give access to"
              value={target}
              onChange={(u) => update({ target: u })}
              excludeId={source?.id}
              disabled={busy !== null}
              helperText="The person who gets the groups"
            />
          </Box>
          {sameUser && (
            <Alert severity="warning" sx={{ mt: 2 }}>Choose two different people.</Alert>
          )}
          <Stack direction={{ xs: "column", sm: "row" }} spacing={1.5} sx={{ mt: 2, alignItems: { sm: "center" } }}>
            <Button variant="contained" startIcon={<CompareArrows />} onClick={() => void compare()} disabled={!ready || busy !== null}>
              {busy === "plan" ? "Comparing..." : plan ? "Compare again" : "Compare"}
            </Button>
            <Typography variant="body2" color="text.secondary">
              Additive only -- nothing will be removed. Comparing changes nothing.
            </Typography>
          </Stack>
        </CardContent>
      </Card>

      {tenantId && role !== null && !canApply && (
        <AccessNotice title={`You have ${role} access to ${tenantName}`}>
          You can compare two people, but adding the groups needs Operator. Ask one of the tenant's
          Owners to raise your role, or send the plan to someone who has it.
        </AccessNotice>
      )}

      {error && (
        <Alert severity="error">
          {unsupported
            ? "This server doesn't have the Access Parity operation (it needs Partner Center Bridge 0.9.0 or later)."
            : error}
        </Alert>
      )}

      {busy === "plan" && <Skeleton variant="rounded" height={200} aria-label="Loading plan" />}

      {plan && (
        <Stack spacing={2} component="section" aria-label="Access parity plan">
          <Alert severity="info" variant="outlined">
            <AlertTitle>Additive only -- nothing will be removed</AlertTitle>
            PCB adds {targetName} to groups {sourceName} is a direct member of. Memberships {targetName} already
            has that {sourceName} doesn't are kept; this operation never removes anything.
          </Alert>

          {plan.limitations.length > 0 && (
            <Alert severity="warning" variant="outlined">
              <AlertTitle>Not compared</AlertTitle>
              <SentenceList items={plan.limitations} label="Limitations" />
            </Alert>
          )}

          {plan.warnings.length > 0 && (
            <Alert severity="warning">
              <SentenceList items={plan.warnings} label="Plan warnings" />
            </Alert>
          )}

          {plan.preflight.some((f) => f.status !== "Ok") && (
            <Card variant="outlined">
              <CardContent>
                <Typography variant="subtitle1" component="h3" sx={{ fontWeight: 600 }} gutterBottom>Preflight</Typography>
                <FindingList findings={plan.preflight} label="Preflight checks" />
              </CardContent>
            </Card>
          )}

          <Card variant="outlined">
            <CardContent>
              <Stack direction="row" spacing={1} sx={{ alignItems: "center", justifyContent: "space-between", flexWrap: "wrap" }} useFlexGap>
                <Typography variant="subtitle1" component="h3" sx={{ fontWeight: 600 }}>
                  Groups to add
                  <Typography component="span" variant="body2" color="text.secondary" sx={{ ml: 1 }}>
                    {chosen.length} of {eligible.length} selected
                  </Typography>
                </Typography>
                {eligible.length > 1 && (
                  <Box component="label" sx={{ display: "inline-flex", alignItems: "center", typography: "body2" }}>
                    <Checkbox
                      size="small"
                      checked={chosen.length === eligible.length}
                      indeterminate={chosen.length > 0 && chosen.length < eligible.length}
                      onChange={(e) => setSelected(new Set(e.target.checked ? eligible.map((i) => i.id) : []))}
                    />
                    Select all
                  </Box>
                )}
              </Stack>
              {eligible.length === 0 ? (
                <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>
                  Nothing to add: {targetName} already has every group PCB can copy from {sourceName}.
                </Typography>
              ) : (
                <Box component="ul" aria-label="Eligible groups" sx={{ listStyle: "none", m: 0, p: 0 }}>
                  {eligible.map((i) => (
                    <Box component="li" key={i.id} sx={{ display: "flex", alignItems: "center", gap: 1, py: 0.25, minWidth: 0 }}>
                      <Checkbox
                        checked={selected.has(i.id)}
                        onChange={(e) => toggle(i.id, e.target.checked)}
                        slotProps={{ input: { "aria-label": i.objectName } }}
                      />
                      <Typography variant="body2" sx={{ flex: 1, minWidth: 0, overflowWrap: "anywhere" }}>{i.objectName}</Typography>
                      <CategoryChip category={i.category} />
                    </Box>
                  ))}
                </Box>
              )}
              {already.length > 0 && (
                <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>
                  {targetName} is already a member of {pluralize(already.length, "other group")} {sourceName} has; no change needed.
                </Typography>
              )}
              <Stack direction={{ xs: "column", sm: "row" }} spacing={1.5} sx={{ mt: 2, alignItems: { sm: "center" } }}>
                <Button
                  variant="contained"
                  onClick={() => void apply()}
                  disabled={!canApply || chosen.length === 0 || busy !== null}
                >
                  {busy === "apply" ? "Adding..." : `Add ${targetName} to ${pluralize(chosen.length, "group")}`}
                </Button>
                {!canApply && (
                  <Typography variant="body2" color="text.secondary">Adding groups needs Operator on {tenantName}.</Typography>
                )}
              </Stack>
            </CardContent>
          </Card>

          {notCopied.length > 0 && (
            <Card variant="outlined">
              <CardContent>
                <Typography variant="subtitle1" component="h3" sx={{ fontWeight: 600 }}>
                  Not copied
                  <Typography component="span" variant="body2" color="text.secondary" sx={{ ml: 1 }}>
                    {pluralize(notCopied.reduce((n, g) => n + g.items.length, 0), "item")} PCB leaves for a human
                  </Typography>
                </Typography>
                {notCopied.map((g) => (
                  <Box key={g.key} component="section" aria-label={g.title} sx={{ mt: 1.5 }}>
                    <Typography variant="subtitle2" component="h4">{g.title}</Typography>
                    <Box component="ul" sx={{ listStyle: "none", m: 0, p: 0 }}>
                      {g.items.map((i) => (
                        <Box component="li" key={i.id} sx={{ py: 0.75, borderBottom: 1, borderColor: "divider", "&:last-child": { borderBottom: 0 } }}>
                          <Stack direction="row" spacing={1} useFlexGap sx={{ alignItems: "center", flexWrap: "wrap" }}>
                            <Typography variant="body2" sx={{ overflowWrap: "anywhere", minWidth: 0 }}>{i.objectName}</Typography>
                            <CategoryChip category={i.category} />
                          </Stack>
                          {i.reason && (
                            <Typography variant="body2" color="text.secondary" sx={{ overflowWrap: "anywhere" }}>{i.reason}</Typography>
                          )}
                        </Box>
                      ))}
                    </Box>
                  </Box>
                ))}
              </CardContent>
            </Card>
          )}
        </Stack>
      )}

      {evidence && (
        <Box>
          <EvidencePanel evidence={evidence} title="Mirror access" />
          <Button sx={{ mt: 2 }} onClick={() => invalidate()}>Start another comparison</Button>
        </Box>
      )}
    </Stack>
  );
}
