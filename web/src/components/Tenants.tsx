import { Fragment, useEffect, useState } from "react";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Chip from "@mui/material/Chip";
import FormControl from "@mui/material/FormControl";
import InputLabel from "@mui/material/InputLabel";
import MenuItem from "@mui/material/MenuItem";
import Select from "@mui/material/Select";
import Stack from "@mui/material/Stack";
import Table from "@mui/material/Table";
import TableBody from "@mui/material/TableBody";
import TableCell from "@mui/material/TableCell";
import TableContainer from "@mui/material/TableContainer";
import TableHead from "@mui/material/TableHead";
import TableRow from "@mui/material/TableRow";
import TextField from "@mui/material/TextField";
import Typography from "@mui/material/Typography";
import { api } from "../api";
import { hasInstancePermission } from "../permissions";
import { useAsyncAction } from "../hooks/useAsyncAction";
import { useConfirm } from "../hooks/useConfirm";
import { useToast } from "../hooks/useToast";
import type { Contract, MeProfile, Tenant, TenantGrant, TenantRole, TenantStatus } from "../types";

const ROLES: TenantRole[] = ["Viewer", "Operator", "Owner"];

const STATUS_COLOR: Record<TenantStatus, "success" | "warning" | "error"> = {
  Active: "success",
  Suspended: "warning",
  NoDelegation: "warning",
  Removed: "error"
};

function SharePanel({ tenant, onChanged }: { tenant: Tenant; onChanged: () => void }) {
  const [grants, setGrants] = useState<TenantGrant[]>([]);
  const [email, setEmail] = useState("");
  const [role, setRole] = useState<TenantRole>("Operator");
  const [lastAction, setLastAction] = useState<"load" | "grant" | "revoke" | null>(null);
  const confirm = useConfirm();
  const toast = useToast();

  const loadAction = useAsyncAction(async () => {
    const g = await api.tenantAccess.list(tenant.id);
    setGrants(g);
  });

  useEffect(() => {
    setLastAction("load");
    void loadAction.run();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tenant.id]);

  const grantAction = useAsyncAction(async () => {
    const grantedEmail = email;
    await api.tenantAccess.grant(tenant.id, email, role);
    setEmail("");
    await loadAction.run();
    onChanged();
    toast(`${grantedEmail} now has ${role} access to ${tenant.displayName}`, "success");
  });

  const revokeAction = useAsyncAction(async (userId: string, grantEmail: string) => {
    await api.tenantAccess.revoke(tenant.id, userId);
    await loadAction.run();
    onChanged();
    toast(`Access revoked for ${grantEmail}`, "success");
  });

  const handleRevoke = async (userId: string, grantEmail: string) => {
    const ok = await confirm({
      title: "Revoke access?",
      message: `${grantEmail} will lose access to ${tenant.displayName}.`,
      confirmLabel: "Revoke",
      destructive: true
    });
    if (!ok) return;
    setLastAction("revoke");
    await revokeAction.run(userId, grantEmail);
  };

  // Each async action's error is shown only while it is the most recently attempted one -- this
  // mirrors Login.tsx's lastAttempt pattern and avoids a stale error from one action masking (or
  // outliving) a more recent success/failure from an unrelated action.
  const error =
    lastAction === "load" ? loadAction.error :
    lastAction === "grant" ? grantAction.error :
    lastAction === "revoke" ? revokeAction.error :
    null;

  return (
    <TableRow>
      <TableCell colSpan={5} sx={{ bgcolor: "action.hover" }}>
        <Box sx={{ p: 1 }}>
          <Typography variant="subtitle2" gutterBottom>
            Who has access to {tenant.displayName}
          </Typography>
          {error && (
            <Alert severity="error" sx={{ mb: 1 }}>
              {error}
            </Alert>
          )}

          <TableContainer sx={{ mb: 2, overflowX: "auto" }}>
            <Table size="small">
              <TableHead>
                <TableRow>
                  <TableCell>Email</TableCell>
                  <TableCell>Role</TableCell>
                  <TableCell>Granted</TableCell>
                  <TableCell></TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {grants.map((g) => (
                  <TableRow key={g.userId}>
                    <TableCell>{g.email}</TableCell>
                    <TableCell>{g.role}</TableCell>
                    <TableCell>{new Date(g.grantedAt).toLocaleDateString()}</TableCell>
                    <TableCell>
                      <Button
                        size="small"
                        color="error"
                        onClick={() => void handleRevoke(g.userId, g.email)}
                        disabled={revokeAction.busy}
                      >
                        Revoke
                      </Button>
                    </TableCell>
                  </TableRow>
                ))}
                {grants.length === 0 && (
                  <TableRow>
                    <TableCell colSpan={4}>
                      <Typography variant="body2" color="text.secondary">
                        Only you have access so far.
                      </Typography>
                    </TableCell>
                  </TableRow>
                )}
              </TableBody>
            </Table>
          </TableContainer>

          <Stack
            component="form"
            direction={{ xs: "column", sm: "row" }}
            spacing={1}
            sx={{ alignItems: { sm: "flex-start" } }}
            onSubmit={(ev) => {
              ev.preventDefault();
              setLastAction("grant");
              void grantAction.run();
            }}
          >
            <TextField
              size="small"
              label="Email"
              placeholder="teammate@example.com"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
            />
            <FormControl size="small" sx={{ minWidth: 140 }}>
              <InputLabel id={`share-role-${tenant.id}`}>Role</InputLabel>
              <Select
                labelId={`share-role-${tenant.id}`}
                label="Role"
                value={role}
                onChange={(e) => setRole(e.target.value as TenantRole)}
              >
                {ROLES.map((r) => (
                  <MenuItem key={r} value={r}>
                    {r}
                  </MenuItem>
                ))}
              </Select>
            </FormControl>
            <Button type="submit" variant="contained" disabled={grantAction.busy}>
              {grantAction.busy ? "Sharing..." : "Share"}
            </Button>
          </Stack>
          <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>
            They need to already have a registered account. Viewer = read-only, Operator = can run
            workflows/deploy, Owner = can also share/revoke.
          </Typography>
        </Box>
      </TableCell>
    </TableRow>
  );
}

export function Tenants({ me, onProfileChanged }: { me: MeProfile | null; onProfileChanged: () => void }) {
  const [tenants, setTenants] = useState<Tenant[]>([]);
  const [contracts, setContracts] = useState<Contract[]>([]);
  const [sharingId, setSharingId] = useState<string | null>(null);
  const [newTenant, setNewTenant] = useState({ tenantId: "", displayName: "", defaultDomain: "" });
  const [lastAction, setLastAction] = useState<"load" | "sync" | "add" | "assign" | null>(null);
  const toast = useToast();

  const loadAction = useAsyncAction(async () => {
    const [t, c] = await Promise.all([api.tenants.list(), api.contracts.list()]);
    setTenants(t);
    setContracts(c);
  });

  useEffect(() => {
    setLastAction("load");
    void loadAction.run();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const syncAction = useAsyncAction(async () => {
    await api.tenants.sync();
    await loadAction.run();
    onProfileChanged();
    toast("Synced from Partner Center", "success");
  });

  const addTenantAction = useAsyncAction(async () => {
    const addedName = newTenant.displayName;
    await api.tenants.create(newTenant.tenantId, newTenant.displayName, newTenant.defaultDomain || undefined);
    setNewTenant({ tenantId: "", displayName: "", defaultDomain: "" });
    await loadAction.run();
    onProfileChanged();
    toast(`${addedName} added`, "success");
  });

  const assignAction = useAsyncAction(async (id: string, contractId: string) => {
    await api.tenants.setContract(id, contractId || null);
    await loadAction.run();
    const t = tenants.find((x) => x.id === id);
    const c = contracts.find((x) => x.id === contractId);
    toast(
      c ? `${t?.displayName ?? "Tenant"} assigned to ${c.name}` : `${t?.displayName ?? "Tenant"} contract cleared`,
      "success"
    );
  });

  // Under Auth:Mode=Local, sharing is only meaningful (and only enabled server-side) for the
  // Owner of a tenant. OIDC/dev-auth operators (me === null) already have unrestricted access,
  // so there's nothing to share -- the button doesn't apply to them.
  const roleFor = (tenantId: string) => me?.tenantAccess.find((a) => a.tenantId === tenantId)?.role;
  const canShare = (tenantId: string) => me !== null && roleFor(tenantId) === "Owner";
  const canAssign = (tenantId: string) => me === null || roleFor(tenantId) === "Owner";
  const canManageRegistry = hasInstancePermission(me, "instance.tenant-registry.manage");

  // See SharePanel's identical comment: error is scoped to the most recently attempted action so
  // an older failure from an unrelated action can't mask or outlive a newer one.
  const error =
    lastAction === "load" ? loadAction.error :
    lastAction === "sync" ? syncAction.error :
    lastAction === "add" ? addTenantAction.error :
    lastAction === "assign" ? assignAction.error :
    null;

  return (
    <Box component="section">
      <Stack direction="row" sx={{ alignItems: "center", justifyContent: "space-between", mb: 2 }}>
        <Typography variant="h5" component="h2">
          Tenants
        </Typography>
        {canManageRegistry && (
          <Button
            variant="contained"
            onClick={() => {
              setLastAction("sync");
              void syncAction.run();
            }}
            disabled={syncAction.busy}
          >
            {syncAction.busy ? "Syncing..." : "Sync from Partner Center"}
          </Button>
        )}
      </Stack>
      {error && (
        <Alert severity="error" sx={{ mb: 2 }}>
          {error}
        </Alert>
      )}

      <TableContainer sx={{ mb: 3, overflowX: "auto" }}>
        <Table size="small">
          <TableHead>
            <TableRow>
              <TableCell>Name</TableCell>
              <TableCell>Domain</TableCell>
              <TableCell>Status</TableCell>
              <TableCell>Contract</TableCell>
              <TableCell></TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {tenants.map((t) => (
              <Fragment key={t.id}>
                <TableRow>
                  <TableCell>{t.displayName}</TableCell>
                  <TableCell>{t.defaultDomain ?? "--"}</TableCell>
                  <TableCell>
                    <Chip size="small" label={t.status} color={STATUS_COLOR[t.status]} />
                  </TableCell>
                  <TableCell>
                    <FormControl size="small" sx={{ minWidth: 160 }}>
                      <InputLabel id={`contract-label-${t.id}`}>Contract</InputLabel>
                      <Select
                        labelId={`contract-label-${t.id}`}
                        label="Contract"
                        size="small"
                        value={t.contractId ?? ""}
                        onChange={(e) => {
                          setLastAction("assign");
                          void assignAction.run(t.id, e.target.value);
                        }}
                        disabled={assignAction.busy || !canAssign(t.id)}
                        displayEmpty
                      >
                        <MenuItem value="">-- none --</MenuItem>
                        {contracts.map((c) => (
                          <MenuItem key={c.id} value={c.id}>
                            {c.name}
                          </MenuItem>
                        ))}
                      </Select>
                    </FormControl>
                  </TableCell>
                  <TableCell>
                    {canShare(t.id) && (
                      <Button size="small" onClick={() => setSharingId(sharingId === t.id ? null : t.id)}>
                        {sharingId === t.id ? "Close" : "Share"}
                      </Button>
                    )}
                  </TableCell>
                </TableRow>
                {sharingId === t.id && <SharePanel tenant={t} onChanged={() => void loadAction.run()} />}
              </Fragment>
            ))}
            {tenants.length === 0 && (
              <TableRow>
                <TableCell colSpan={5}>
                  <Typography variant="body2" color="text.secondary">
                    {canManageRegistry
                      ? "No tenants yet. Sync from Partner Center or add one below."
                      : "No tenants have been shared with you yet."}
                  </Typography>
                </TableCell>
              </TableRow>
            )}
          </TableBody>
        </Table>
      </TableContainer>

      {canManageRegistry && <Box component="fieldset" sx={{ border: 1, borderColor: "divider", borderRadius: 1, p: 2 }}>
        <Typography component="legend" variant="subtitle1">
          Add a tenant
        </Typography>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
          Already have a GDAP relationship with a customer? Register it directly instead of
          waiting for a full sync -- you become its Owner immediately and can share it from there.
        </Typography>
        <Stack
          component="form"
          direction={{ xs: "column", sm: "row" }}
          spacing={1}
          onSubmit={(ev) => {
            ev.preventDefault();
            setLastAction("add");
            void addTenantAction.run();
          }}
        >
          <TextField
            size="small"
            label="Entra tenant id (GUID)"
            value={newTenant.tenantId}
            onChange={(e) => setNewTenant({ ...newTenant, tenantId: e.target.value })}
          />
          <TextField
            size="small"
            label="Display name"
            value={newTenant.displayName}
            onChange={(e) => setNewTenant({ ...newTenant, displayName: e.target.value })}
          />
          <TextField
            size="small"
            label="Default domain (optional)"
            value={newTenant.defaultDomain}
            onChange={(e) => setNewTenant({ ...newTenant, defaultDomain: e.target.value })}
          />
          <Button type="submit" variant="contained" disabled={addTenantAction.busy}>
            {addTenantAction.busy ? "Adding..." : "Add tenant"}
          </Button>
        </Stack>
      </Box>}
    </Box>
  );
}
