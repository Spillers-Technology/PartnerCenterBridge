import { useEffect, useRef, useState } from "react";
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
import { useConfirm } from "../hooks/useConfirm";
import { useToast } from "../hooks/useToast";
import type { DirectoryObject, ProvisioningResult, Tenant } from "../types";
import { StepList } from "./StepList";

const ACTIONS = [
  ["blockSignIn", "Block sign-in"],
  ["revokeSessions", "Revoke sessions"],
  ["removeLicenses", "Remove licenses"],
  ["removeFromGroups", "Remove from groups"],
  ["convertMailboxToShared", "Convert mailbox to shared (Exchange Online)"]
] as const;

export function Offboard() {
  const [tenants, setTenants] = useState<Tenant[]>([]);
  const [tenantId, setTenantId] = useState("");
  const [search, setSearch] = useState("");
  const [users, setUsers] = useState<DirectoryObject[]>([]);
  const [userId, setUserId] = useState("");
  const [opts, setOpts] = useState({ blockSignIn: true, revokeSessions: true, removeLicenses: true, removeFromGroups: true, convertMailboxToShared: false });
  const [forwardingSmtpAddress, setForwardingSmtpAddress] = useState("");
  const [result, setResult] = useState<ProvisioningResult | null>(null);
  const [lastAction, setLastAction] = useState<"tenants" | "search" | "submit" | null>(null);
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

  const submitAction = useAsyncAction(async () => {
    const offboardResult = await api.provisioning.terminate(tenantId, {
      userId, ...opts,
      forwardingSmtpAddress: forwardingSmtpAddress || undefined
    });
    setResult(offboardResult);
    if (offboardResult.succeeded) toast(`${selectedUser?.displayName ?? "User"} offboarded`, "success");
  });

  // A stale userId (from before the most recent search, or a user no longer in the search
  // results) must never stay submittable -- selectedUser is the single source of truth for
  // "there is a real, currently-visible target selected," not just a non-empty userId string.
  const canSubmit = Boolean(selectedUser) && !searchAction.busy;

  useEffect(() => {
    setLastAction("tenants");
    void tenantsAction.run();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const error =
    lastAction === "tenants" ? tenantsAction.error :
    lastAction === "search" ? searchAction.error :
    lastAction === "submit" ? submitAction.error :
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
      const ok = await confirm({
        title: "Offboard this user?",
        message: `${selectedUser.displayName} (${selectedUser.userPrincipalName}) in ${selectedTenant?.displayName ?? "this tenant"} will be offboarded. Actions: ${enabledActions.join(", ") || "none"}.${forwardingNote}`,
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
          }}
        >
          <MenuItem value="">-- choose --</MenuItem>
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
                <MenuItem value="">-- choose --</MenuItem>
                {users.map((user) => <MenuItem key={user.id} value={user.id}>{user.displayName} ({user.userPrincipalName})</MenuItem>)}
              </Select>
            </FormControl>
          )}

          <Box component="fieldset" sx={{ border: 1, borderColor: "divider", borderRadius: 1, p: 2 }}>
            <Typography component="legend" variant="subtitle1">Actions</Typography>
            <FormGroup>
              {ACTIONS.map(([key, label]) => (
                <FormControlLabel key={key} control={<Checkbox checked={opts[key]} onChange={(e) => setOpts({ ...opts, [key]: e.target.checked })} />} label={label} />
              ))}
            </FormGroup>
          </Box>

          {opts.convertMailboxToShared && (
            <TextField fullWidth label="Forward mailbox to (optional SMTP)" placeholder="manager@contoso.com" value={forwardingSmtpAddress} onChange={(e) => setForwardingSmtpAddress(e.target.value)} />
          )}

          <Box>
            <Button variant="contained" color="error" onClick={() => void submit()} disabled={submitAction.busy || confirming || !canSubmit}>
              {submitAction.busy ? "Offboarding..." : "Offboard user"}
            </Button>
          </Box>
        </Stack>
      )}
      {error && <Alert severity="error" sx={{ mt: 2 }}>{error}</Alert>}
      {result && <StepList result={result} />}
    </Box>
  );
}
