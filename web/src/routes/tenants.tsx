import { useCallback, useEffect, useState, type ReactNode } from "react";
import { Link as RouterLink, useParams, useSearchParams } from "react-router";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Card from "@mui/material/Card";
import CardContent from "@mui/material/CardContent";
import Chip from "@mui/material/Chip";
import FormControl from "@mui/material/FormControl";
import InputLabel from "@mui/material/InputLabel";
import Link from "@mui/material/Link";
import MenuItem from "@mui/material/MenuItem";
import Select from "@mui/material/Select";
import Skeleton from "@mui/material/Skeleton";
import Stack from "@mui/material/Stack";
import Tab from "@mui/material/Tab";
import Table from "@mui/material/Table";
import TableBody from "@mui/material/TableBody";
import TableCell from "@mui/material/TableCell";
import TableContainer from "@mui/material/TableContainer";
import TableHead from "@mui/material/TableHead";
import TableRow from "@mui/material/TableRow";
import Tabs from "@mui/material/Tabs";
import Typography from "@mui/material/Typography";
import { api } from "../api";
import { humanizeEnum, Timestamp } from "../format";
import { useAsyncAction } from "../hooks/useAsyncAction";
import { useToast } from "../hooks/useToast";
import { AccessNotice } from "../components/AccessNotice";
import { ConfigSnapshots } from "../components/ConfigSnapshots";
import { Deployments } from "../components/Deployments";
import { TenantAudits } from "../components/TenantAudits";
import { PageHeader } from "../components/PageHeader";
import { TenantAccessPanel, Tenants } from "../components/Tenants";
import { hasTenantRole, tenantRole } from "../permissions";
import type { Contract, Tenant, TenantStatus, WorkflowRunRecord } from "../types";
import { useWorkbench } from "../workbench";

export function TenantsPage() {
  const { me, refreshMe } = useWorkbench();
  return <Tenants me={me} onProfileChanged={() => void refreshMe()} tenantPath={(t) => `/tenants/${t.id}`} />;
}

const TENANT_TABS = [
  { key: "overview", label: "Overview" },
  { key: "access", label: "Access" },
  { key: "contract", label: "Contract" },
  { key: "audits", label: "Audits" },
  { key: "snapshots", label: "Snapshots" },
  { key: "history", label: "History" }
] as const;
type TenantTab = (typeof TENANT_TABS)[number]["key"];

const STATUS_COLOR: Record<TenantStatus, "success" | "warning" | "error"> = {
  Active: "success",
  Suspended: "warning",
  NoDelegation: "warning",
  Removed: "error"
};

function Fact({ label, children }: { label: string; children: ReactNode }) {
  return (
    <Box sx={{ minWidth: 0 }}>
      <Typography variant="caption" color="text.secondary" component="div">{label}</Typography>
      <Typography variant="body2" component="div" sx={{ overflowWrap: "anywhere" }}>{children}</Typography>
    </Box>
  );
}

function RunsTable({ runs, label }: { runs: WorkflowRunRecord[]; label: string }) {
  return (
    <TableContainer sx={{ overflowX: "auto" }}>
      <Table size="small" aria-label={label}>
        <TableHead>
          <TableRow>
            <TableCell>When</TableCell>
            <TableCell>Operation</TableCell>
            <TableCell>Kind</TableCell>
            <TableCell>Operator</TableCell>
            <TableCell>Result</TableCell>
          </TableRow>
        </TableHead>
        <TableBody>
          {runs.map((r) => (
            <TableRow key={r.id}>
              <TableCell><Timestamp value={r.startedAt} /></TableCell>
              <TableCell>
                <Link component={RouterLink} to={`/activity/runs/${r.id}`} underline="hover">{r.workflowName}</Link>
              </TableCell>
              <TableCell>{r.kind}</TableCell>
              <TableCell>{r.operator}</TableCell>
              <TableCell>
                <Chip size="small" label={r.succeeded ? "ok" : "failed"} color={r.succeeded ? "success" : "error"} />
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </TableContainer>
  );
}

function useTenantRuns(tenantId: string, take: number) {
  const [runs, setRuns] = useState<WorkflowRunRecord[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    let alive = true;
    setRuns(null);
    setError(null);
    api.workflows.runs({ tenantId, take })
      .then((r) => { if (alive) setRuns(r); })
      .catch((e) => { if (alive) setError(e instanceof Error ? e.message : String(e)); });
    return () => { alive = false; };
  }, [tenantId, take]);
  return { runs, error };
}

function OverviewTab({ tenant, contract }: { tenant: Tenant; contract: Contract | undefined }) {
  const { me } = useWorkbench();
  const { runs, error } = useTenantRuns(tenant.id, 5);
  const role = tenantRole(me, tenant.id);
  const canOperate = hasTenantRole(me, tenant.id, "Operator");
  const q = `tenant=${encodeURIComponent(tenant.id)}`;

  return (
    <Stack spacing={2}>
      {tenant.status === "NoDelegation" && (
        <AccessNotice title="PCB can't act in this tenant" severity="warning">
          The GDAP relationship is missing or expired. Restore delegated admin access for this
          customer in Partner Center, then sync tenants.
        </AccessNotice>
      )}
      <Box sx={{ display: "grid", gridTemplateColumns: { xs: "1fr", md: "minmax(0, 3fr) minmax(0, 2fr)" }, gap: 2 }}>
        <Card variant="outlined">
          <CardContent>
            <Typography variant="h6" component="h3" gutterBottom>Details</Typography>
            <Box sx={{ display: "grid", gridTemplateColumns: { xs: "1fr", sm: "repeat(2, minmax(0, 1fr))" }, gap: 1.5 }}>
              <Fact label="Default domain">{tenant.defaultDomain ?? "--"}</Fact>
              <Fact label="Status">{humanizeEnum(tenant.status)}</Fact>
              <Fact label="Entra tenant id"><Box component="span" sx={{ fontFamily: "monospace" }}>{tenant.tenantId}</Box></Fact>
              <Fact label="Contract">{contract?.name ?? "None"}</Fact>
              <Fact label="Your role">{role ?? "None"}</Fact>
            </Box>
          </CardContent>
        </Card>
        <Card variant="outlined">
          <CardContent>
            <Typography variant="h6" component="h3" gutterBottom>Work in this tenant</Typography>
            {!canOperate && (
              <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>
                Onboarding and offboarding need Operator on this tenant; you can still diagnose.
              </Typography>
            )}
            <Stack spacing={1} sx={{ alignItems: "flex-start" }}>
              <Button component={RouterLink} to={`/tenants/${tenant.id}?tab=audits`} size="small">Run a health check</Button>
              <Button component={RouterLink} to={`/operations/workflows?${q}`} size="small">Run a known fix</Button>
              <Button component={RouterLink} to={`/operations/onboard?${q}`} size="small" disabled={!canOperate}>Onboard a new hire</Button>
              <Button component={RouterLink} to={`/operations/offboard?${q}`} size="small" disabled={!canOperate}>Offboard someone</Button>
            </Stack>
          </CardContent>
        </Card>
      </Box>
      <Box>
        <Stack direction="row" sx={{ alignItems: "baseline", justifyContent: "space-between", mb: 1 }}>
          <Typography variant="h6" component="h3">Recent runs</Typography>
          <Button component={RouterLink} to={`/tenants/${tenant.id}?tab=history`} size="small">All history</Button>
        </Stack>
        {error && <Alert severity="error">{error}</Alert>}
        {!error && runs === null && <Skeleton variant="rounded" height={80} />}
        {runs && runs.length === 0 && (
          <Typography variant="body2" color="text.secondary">No runs in this tenant yet.</Typography>
        )}
        {runs && runs.length > 0 && <RunsTable runs={runs} label="Recent runs in this tenant" />}
      </Box>
    </Stack>
  );
}

function AccessTab({ tenant }: { tenant: Tenant }) {
  const { me, authMode, refreshMe } = useWorkbench();
  if (me === null) {
    return (
      <AccessNotice title="Access is managed by your identity provider">
        This workbench runs in {authMode === "Oidc" ? "OIDC" : "Dev"} mode: every signed-in operator can
        reach every tenant, so there are no per-tenant grants to manage here.
      </AccessNotice>
    );
  }
  const role = tenantRole(me, tenant.id);
  if (role !== "Owner") {
    return (
      <AccessNotice title="Only an Owner can manage access">
        Your role on {tenant.displayName} is {role ?? "none"}. Ask one of its Owners to share it with a
        teammate or to change a role.
      </AccessNotice>
    );
  }
  return (
    <Card variant="outlined">
      <CardContent>
        <TenantAccessPanel tenant={tenant} onChanged={() => void refreshMe()} />
      </CardContent>
    </Card>
  );
}

function ContractTab({ tenant, contracts, onChanged }: { tenant: Tenant; contracts: Contract[]; onChanged: () => Promise<void> }) {
  const { me } = useWorkbench();
  const toast = useToast();
  const canAssign = hasTenantRole(me, tenant.id, "Owner");
  const current = contracts.find((c) => c.id === tenant.contractId);
  const assign = useAsyncAction(async (contractId: string) => {
    await api.tenants.setContract(tenant.id, contractId || null);
    await onChanged();
    const c = contracts.find((x) => x.id === contractId);
    toast(c ? `${tenant.displayName} assigned to ${c.name}` : `${tenant.displayName} contract cleared`, "success");
  });

  return (
    <Card variant="outlined">
      <CardContent>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
          The contract decides which apps this tenant should have and the defaults new hires get.
        </Typography>
        {!canAssign && (
          <AccessNotice title="Only an Owner can change the contract">
            {current ? `This tenant is on ${current.name}.` : "This tenant has no contract."} Ask a tenant
            Owner to change it.
          </AccessNotice>
        )}
        <FormControl size="small" sx={{ minWidth: 240, maxWidth: "100%" }}>
          <InputLabel id="tenant-contract-label" shrink>Contract</InputLabel>
          <Select
            labelId="tenant-contract-label"
            label="Contract"
            notched
            displayEmpty
            value={tenant.contractId ?? ""}
            disabled={!canAssign || assign.busy}
            onChange={(e) => void assign.run(e.target.value)}
          >
            <MenuItem value=""><em>none</em></MenuItem>
            {contracts.map((c) => <MenuItem key={c.id} value={c.id}>{c.name}</MenuItem>)}
          </Select>
        </FormControl>
        {assign.error && <Alert severity="error" sx={{ mt: 2 }}>{assign.error}</Alert>}
        {current && (
          <Typography variant="body2" sx={{ mt: 2 }}>
            {current.name}: {current.desiredAppCount} desired app{current.desiredAppCount === 1 ? "" : "s"}
            {current.notes ? ` - ${current.notes}` : ""}
          </Typography>
        )}
        <Button component={RouterLink} to="/operations/contracts" size="small" sx={{ mt: 1 }}>
          Manage contracts
        </Button>
      </CardContent>
    </Card>
  );
}

function HistoryTab({ tenant }: { tenant: Tenant }) {
  const { runs, error } = useTenantRuns(tenant.id, 50);
  return (
    <Stack spacing={3}>
      <Box>
        <Stack direction="row" sx={{ alignItems: "baseline", justifyContent: "space-between", mb: 1 }}>
          <Typography variant="h6" component="h3">Workflow runs</Typography>
          <Button component={RouterLink} to={`/activity?tenant=${encodeURIComponent(tenant.id)}`} size="small">Open in Activity</Button>
        </Stack>
        {error && <Alert severity="error">{error}</Alert>}
        {!error && runs === null && <Skeleton variant="rounded" height={120} />}
        {runs && runs.length === 0 && <Typography variant="body2" color="text.secondary">No runs in this tenant yet.</Typography>}
        {runs && runs.length > 0 && <RunsTable runs={runs} label="Workflow runs in this tenant" />}
      </Box>
      <Deployments tenantId={tenant.id} heading="Deployments" />
    </Stack>
  );
}

/** /tenants/:tenantId?tab= -- one customer tenant: details, access, contract, audits, snapshots, history. */
export function TenantWorkspacePage() {
  const { tenantId = "" } = useParams();
  const [params, setParams] = useSearchParams();
  const { me } = useWorkbench();
  const requested = params.get("tab");
  const tab: TenantTab = TENANT_TABS.some((t) => t.key === requested) ? (requested as TenantTab) : "overview";

  const [tenants, setTenants] = useState<Tenant[] | null>(null);
  const [contracts, setContracts] = useState<Contract[]>([]);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const [t, c] = await Promise.all([api.tenants.list(), api.contracts.list().catch(() => [] as Contract[])]);
      setTenants(t);
      setContracts(c);
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }, []);

  useEffect(() => { void load(); }, [load]);

  if (error) {
    return (
      <Box>
        <PageHeader title="Tenant" parent={{ label: "Tenants", to: "/tenants" }} />
        <Alert severity="error">{error}</Alert>
      </Box>
    );
  }
  if (tenants === null) {
    return (
      <Box aria-busy="true">
        <PageHeader title={<Skeleton width={200} />} parent={{ label: "Tenants", to: "/tenants" }} />
        <Skeleton variant="rounded" height={160} />
      </Box>
    );
  }

  const tenant = tenants.find((t) => t.id === tenantId);
  if (!tenant) {
    return (
      <Box>
        <PageHeader title="Tenant not found" parent={{ label: "Tenants", to: "/tenants" }} />
        <AccessNotice title="This tenant isn't available">
          It doesn't exist, was removed, or hasn't been shared with you
          {me ? " -- ask one of its Owners for access" : ""}.
        </AccessNotice>
      </Box>
    );
  }

  const contract = contracts.find((c) => c.id === tenant.contractId);
  const role = tenantRole(me, tenant.id);

  return (
    <Box>
      <PageHeader
        parent={{ label: "Tenants", to: "/tenants" }}
        title={tenant.displayName}
        meta={
          <>
            <Chip size="small" label={humanizeEnum(tenant.status)} color={STATUS_COLOR[tenant.status]} />
            {me && role && <Chip size="small" variant="outlined" label={role} />}
          </>
        }
        subtitle={tenant.defaultDomain}
      />
      <Tabs
        value={tab}
        onChange={(_, v: TenantTab) => setParams(v === "overview" ? {} : { tab: v })}
        aria-label="Tenant sections"
        variant="scrollable"
        scrollButtons="auto"
        allowScrollButtonsMobile
        sx={{ mb: 2, borderBottom: 1, borderColor: "divider" }}
      >
        {TENANT_TABS.map((t) => <Tab key={t.key} value={t.key} label={t.label} />)}
      </Tabs>

      {tab === "overview" && <OverviewTab tenant={tenant} contract={contract} />}
      {tab === "access" && <AccessTab tenant={tenant} />}
      {tab === "contract" && <ContractTab tenant={tenant} contracts={contracts} onChanged={load} />}
      {tab === "audits" && <TenantAudits tenant={tenant} />}
      {tab === "snapshots" && <ConfigSnapshots me={me} fixedTenantId={tenant.id} heading="Config snapshots" />}
      {tab === "history" && <HistoryTab tenant={tenant} />}
    </Box>
  );
}
