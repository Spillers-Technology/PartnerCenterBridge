import { useEffect, useRef, useState } from "react";
import Alert from "@mui/material/Alert";
import AlertTitle from "@mui/material/AlertTitle";
import Chip from "@mui/material/Chip";
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
import { api, errorText } from "../api";
import { useAsyncAction } from "../hooks/useAsyncAction";
import { useConfirm } from "../hooks/useConfirm";
import { useToast } from "../hooks/useToast";
import type { DirectoryObject, OffboardingPolicy, OperationPlan, Tenant, TerminateResult } from "../types";
import { EvidencePanel } from "./EvidencePanel";
import { FindingList, outcomeMeta, planItemLabel, SentenceList } from "./operationUi";
import { StepList } from "./StepList";

/** Plain-language summary of the contract policy parts the checkboxes below don't show. */
function policyExtras(p: OffboardingPolicy): string[] {
  const extras: string[] = [];
  if (p.groupCleanup === "RemoveAssignable") extras.push("group cleanup leaves role-assignable groups");
  if (p.hideFromGal) extras.push("hide from the address list");
  if (p.managerAccess === "FullAccess") extras.push("give the manager full mailbox access");
  if (p.wipeDevices === "Retire") extras.push("retire managed devices");
  if (p.followUpDays > 0) extras.push(`follow-up reminder after ${p.followUpDays} days`);
  return extras;
}

/** The ordered offboarding plan: what runs, in order, what is destructive, and what won't run and why. */
function PlanView({ plan }: { plan: OperationPlan }) {
  const runs = plan.items.filter((i) => i.eligible);
  return (
    <Box component="section" aria-label="Offboarding plan" sx={{ border: 1, borderColor: "divider", borderRadius: 1, p: 2 }}>
      <Typography variant="subtitle1" component="h3" sx={{ fontWeight: 600 }}>
        Plan for {plan.target.displayName || "this user"}
      </Typography>
      <Typography variant="body2" color="text.secondary">
        {runs.length} of {plan.items.length} steps will run, in this order. Nothing has changed yet.
      </Typography>
      {plan.warnings.length > 0 && (
        <Alert severity="warning" sx={{ mt: 1 }}>
          <SentenceList items={plan.warnings} label="Plan warnings" />
        </Alert>
      )}
      <Box component="ol" aria-label="Plan steps" sx={{ m: 0, mt: 1, pl: 3 }}>
        {plan.items.map((i) => (
          <Box component="li" key={i.id} sx={{ py: 0.75, color: i.eligible ? "text.primary" : "text.secondary" }}>
            <Stack direction="row" spacing={1} useFlexGap sx={{ alignItems: "center", flexWrap: "wrap" }}>
              <Typography variant="body2" sx={{ overflowWrap: "anywhere", minWidth: 0 }}>{planItemLabel(i)}</Typography>
              {i.eligible && i.destructive && <Chip size="small" color="error" variant="outlined" label="Destructive" />}
              {!i.eligible && <Chip size="small" variant="outlined" label="Won't run" />}
            </Stack>
            {i.reason && (
              <Typography variant="body2" color="text.secondary" sx={{ overflowWrap: "anywhere" }}>{i.reason}</Typography>
            )}
          </Box>
        ))}
      </Box>
      {plan.preflight.length > 0 && (
        <Box sx={{ mt: 1.5 }}>
          <Typography variant="subtitle2" component="h4">Preflight</Typography>
          <FindingList findings={plan.preflight} label="Preflight checks" />
        </Box>
      )}
      {plan.limitations.length > 0 && (
        <Alert severity="info" variant="outlined" sx={{ mt: 1.5 }}>
          <AlertTitle>Limitations</AlertTitle>
          <SentenceList items={plan.limitations} label="Plan limitations" />
        </Alert>
      )}
    </Box>
  );
}

const ACTIONS = [
  ["blockSignIn", "Block sign-in"],
  ["revokeSessions", "Revoke sessions"],
  ["removeLicenses", "Remove licenses"],
  ["removeFromGroups", "Remove from groups"],
  ["convertMailboxToShared", "Convert mailbox to shared (Exchange Online)"]
] as const;

export function Offboard({
  prefill
}: {
  /** Pre-selects a tenant and searches for (and, on an exact match, selects) a user. */
  prefill?: { tenantId: string; user?: string } | null;
} = {}) {
  const [tenants, setTenants] = useState<Tenant[]>([]);
  const [tenantId, setTenantId] = useState("");
  const [search, setSearch] = useState("");
  const [users, setUsers] = useState<DirectoryObject[]>([]);
  const [userId, setUserId] = useState("");
  const [opts, setOpts] = useState({ blockSignIn: true, revokeSessions: true, removeLicenses: true, removeFromGroups: true, convertMailboxToShared: false });
  const [forwardingSmtpAddress, setForwardingSmtpAddress] = useState("");
  const [result, setResult] = useState<TerminateResult | null>(null);
  const [lastAction, setLastAction] = useState<"tenants" | "search" | "submit" | "plan" | null>(null);
  const [policy, setPolicy] = useState<OffboardingPolicy | null>(null);
  const [plan, setPlan] = useState<{ key: string; plan: OperationPlan } | null>(null);
  // useAsyncAction's own busy flag only turns on once the terminate call actually starts, which
  // leaves a window open while the confirm dialog is awaited: a second click during that window
  // could queue a second confirm request (useConfirm queues rather than rejecting a second call)
  // and, if the user confirms it after the first terminate has already finished, fire a duplicate
  // destructive submission. This local guard covers that whole window, not just the API call.
  const [confirming, setConfirming] = useState(false);
  const currentTenantRef = useRef("");
  const confirm = useConfirm();
  const toast = useToast();

  const tenantsAction = useAsyncAction(async () => {
    setTenants(await api.tenants.list());
  });

  const searchAction = useAsyncAction(async (id: string, query: string) => {
    const loadedUsers = await api.directory.users(id, query || undefined);
    if (currentTenantRef.current !== id) return;
    setUsers(loadedUsers);
  });

  const selectedUser = users.find((user) => user.id === userId);
  const selectedTenant = tenants.find((t) => t.id === tenantId);

  // The request body both the plan and the apply send; a plan is only shown while it still
  // describes exactly this body.
  const body = () => ({ userId, ...opts, forwardingSmtpAddress: forwardingSmtpAddress || undefined });
  const bodyKey = JSON.stringify({ tenantId, ...body() });
  const currentPlan = plan && plan.key === bodyKey ? plan.plan : null;

  const submitAction = useAsyncAction(async () => {
    const offboardResult = await api.provisioning.terminate(tenantId, body());
    setResult(offboardResult);
    setPlan(null);
    const name = selectedUser?.displayName ?? "User";
    const outcome = offboardResult.evidence?.outcome;
    if (outcome) {
      if (outcome === "Succeeded" || outcome === "NoChangeNeeded") toast(`${name} offboarded`, "success");
      else toast(`Offboarding ${name} finished: ${outcomeMeta(outcome).label.toLowerCase()}. Review the result.`, "warning");
    } else if (offboardResult.succeeded) {
      toast(`${name} offboarded`, "success");
    }
  });

  const planAction = useAsyncAction(async () => {
    const key = bodyKey;
    try {
      const p = await api.provisioning.terminatePlan(tenantId, body());
      setPlan({ key, plan: p });
    } catch (e) {
      throw new Error(errorText(e));
    }
  });

  // The tenant's contract policy sets the starting options (and the parts not shown as checkboxes).
  useEffect(() => {
    setPolicy(null);
    const contractId = tenants.find((t) => t.id === tenantId)?.contractId;
    if (!contractId) return;
    let alive = true;
    api.contracts.getOffboardingPolicy(contractId)
      .then((p) => {
        if (!alive) return;
        setPolicy(p);
        setOpts({
          blockSignIn: p.blockSignIn,
          revokeSessions: p.revokeSessions,
          removeLicenses: p.removeLicenses,
          removeFromGroups: p.groupCleanup !== "None",
          convertMailboxToShared: p.convertMailboxToShared
        });
        // forwardTo stays with the policy: an empty field here means "use the policy's address".
      })
      .catch(() => { /* older server or no access: the built-in defaults apply */ });
    return () => { alive = false; };
  }, [tenantId, tenants]);

  // A stale userId (from before the most recent search, or a user no longer in the search
  // results) must never stay submittable -- selectedUser is the single source of truth for
  // "there is a real, currently-visible target selected," not just a non-empty userId string.
  const canSubmit = Boolean(selectedUser) && !searchAction.busy;

  // Arriving from a person (or a ?tenant=&user= link): choose the tenant, run the user search, and
  // select the result only when it matches exactly -- a fuzzy match is left for the operator to
  // pick, since the next step is destructive.
  const prefillAppliedRef = useRef(false);
  const pendingPrefillUserRef = useRef<string | null>(null);
  useEffect(() => {
    if (!prefill || prefillAppliedRef.current || tenants.length === 0) return;
    if (!tenants.some((t) => t.id === prefill.tenantId)) return;
    prefillAppliedRef.current = true;
    currentTenantRef.current = prefill.tenantId;
    setTenantId(prefill.tenantId);
    if (prefill.user) {
      pendingPrefillUserRef.current = prefill.user.toLowerCase();
      setSearch(prefill.user);
      setLastAction("search");
      void searchAction.run(prefill.tenantId, prefill.user);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tenants, prefill]);
  useEffect(() => {
    const wanted = pendingPrefillUserRef.current;
    if (!wanted || users.length === 0) return;
    pendingPrefillUserRef.current = null;
    const exact = users.find((u) => u.id.toLowerCase() === wanted || u.userPrincipalName?.toLowerCase() === wanted);
    if (exact) setUserId(exact.id);
  }, [users]);

  useEffect(() => {
    setLastAction("tenants");
    void tenantsAction.run();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const error =
    lastAction === "tenants" ? tenantsAction.error :
    lastAction === "search" ? searchAction.error :
    lastAction === "submit" ? submitAction.error :
    lastAction === "plan" ? planAction.error :
    null;

  const find = () => {
    if (!tenantId) return;
    // A new search invalidates any previously selected user -- it may not appear in the new
    // results at all, and leaving it selected would let a stale ID stay submittable underneath a
    // dropdown that visually shows nothing chosen. Same for a target-specific forwarding address
    // and any leftover result panel from a prior offboard.
    setUserId("");
    setForwardingSmtpAddress("");
    setResult(null);
    setLastAction("search");
    void searchAction.run(tenantId, search);
  };

  const submit = async () => {
    if (!tenantId || !selectedUser || confirming) return;
    setConfirming(true);
    try {
      const enabledActions = ACTIONS.filter(([key]) => opts[key]).map(([, label]) => label.toLowerCase());
      const forwardingNote = forwardingSmtpAddress ? ` Mail will forward to ${forwardingSmtpAddress}.` : "";
      const steps = currentPlan?.items.filter((i) => i.eligible) ?? [];
      const destructiveCount = steps.filter((i) => i.destructive).length;
      const who = `${selectedUser.displayName} (${selectedUser.userPrincipalName}) in ${selectedTenant?.displayName ?? "this tenant"} will be offboarded.`;
      const ok = await confirm({
        title: "Offboard this user?",
        message: currentPlan
          ? `${who} PCB re-checks the plan, then runs these ${steps.length} steps in order${destructiveCount > 0 ? `; ${destructiveCount} of them are destructive` : ""}.${forwardingNote}`
          : `${who} Actions: ${enabledActions.join(", ") || "none"}.${forwardingNote}`,
        items: currentPlan ? steps.map((i) => `${planItemLabel(i)}${i.destructive ? " (destructive)" : ""}`) : undefined,
        itemsLabel: "Steps to run",
        confirmLabel: "Offboard",
        destructive: true
      });
      if (!ok) return;
      setResult(null);
      setLastAction("submit");
      await submitAction.run();
    } finally {
      setConfirming(false);
    }
  };

  return (
    <Box component="section">
      <Typography variant="h5" component="h2" gutterBottom>
        Offboard
      </Typography>
      <FormControl fullWidth sx={{ maxWidth: 360, mb: 2 }}>
        <InputLabel id="offboard-tenant-label">Tenant</InputLabel>
        <Select
          labelId="offboard-tenant-label"
          label="Tenant"
          value={tenantId}
          displayEmpty
          onChange={(e) => {
            const id = e.target.value;
            currentTenantRef.current = id;
            setTenantId(id);
            setUsers([]);
            setUserId("");
            setForwardingSmtpAddress("");
            setResult(null);
            setPlan(null);
          }}
        >
          <MenuItem value=""><em>Choose...</em></MenuItem>
          {tenants.map((tenant) => <MenuItem key={tenant.id} value={tenant.id}>{tenant.displayName}</MenuItem>)}
        </Select>
      </FormControl>

      {tenantId && (
        <Stack spacing={2}>
          <Stack direction={{ xs: "column", sm: "row" }} spacing={1} sx={{ alignItems: { sm: "flex-start" } }}>
            <TextField fullWidth label="Search name or UPN" value={search} onChange={(e) => setSearch(e.target.value)} />
            <Button variant="contained" onClick={find} disabled={searchAction.busy}>Search users</Button>
          </Stack>
          {users.length > 0 && (
            <FormControl fullWidth sx={{ maxWidth: 560 }}>
              <InputLabel id="offboard-user-label">User</InputLabel>
              <Select
                labelId="offboard-user-label"
                label="User"
                value={userId}
                displayEmpty
                onChange={(e) => {
                  setUserId(e.target.value);
                  setForwardingSmtpAddress("");
                }}
              >
                <MenuItem value=""><em>Choose...</em></MenuItem>
                {users.map((user) => <MenuItem key={user.id} value={user.id}>{user.displayName} ({user.userPrincipalName})</MenuItem>)}
              </Select>
            </FormControl>
          )}

          <Box component="fieldset" sx={{ border: 1, borderColor: "divider", borderRadius: 1, p: 2 }}>
            <Typography component="legend" variant="subtitle1">Actions</Typography>
            <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>
              {policy
                ? `Defaults from this tenant's contract offboarding policy${policyExtras(policy).length > 0 ? `, which also asks to ${policyExtras(policy).join("; ")}` : ""}. Changes here apply to this offboarding only.`
                : "Built-in defaults. A contract offboarding policy can change them for every leaver."}
            </Typography>
            <FormGroup>
              {ACTIONS.map(([key, label]) => (
                <FormControlLabel key={key} control={<Checkbox checked={opts[key]} onChange={(e) => setOpts({ ...opts, [key]: e.target.checked })} />} label={label} />
              ))}
            </FormGroup>
          </Box>

          {opts.convertMailboxToShared && (
            <TextField fullWidth label="Forward mailbox to (optional SMTP)" placeholder={policy?.forwardTo ? `Policy default: ${policy.forwardTo}` : "manager@contoso.com"} value={forwardingSmtpAddress} onChange={(e) => setForwardingSmtpAddress(e.target.value)} />
          )}

          <Stack direction={{ xs: "column", sm: "row" }} spacing={1}>
            <Button
              variant="outlined"
              onClick={() => { setLastAction("plan"); setResult(null); void planAction.run(); }}
              disabled={planAction.busy || submitAction.busy || confirming || !canSubmit}
            >
              {planAction.busy ? "Planning..." : currentPlan ? "Refresh plan" : "Preview plan"}
            </Button>
            <Button variant="contained" color="error" onClick={() => void submit()} disabled={submitAction.busy || confirming || !canSubmit}>
              {submitAction.busy ? "Offboarding..." : "Offboard user"}
            </Button>
          </Stack>
          {currentPlan && <PlanView plan={currentPlan} />}
        </Stack>
      )}
      {error && <Alert severity="error" sx={{ mt: 2 }}>{error}</Alert>}
      {result && (result.evidence
        ? <Box sx={{ mt: 2 }}><EvidencePanel evidence={result.evidence} title="Offboarding" /></Box>
        : <StepList result={result} />)}
    </Box>
  );
}
