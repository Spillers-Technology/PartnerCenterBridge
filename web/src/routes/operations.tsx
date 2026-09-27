import { useEffect, useMemo, useState, type ReactNode } from "react";
import { Link as RouterLink, useNavigate, useParams, useSearchParams } from "react-router";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Card from "@mui/material/Card";
import CardActionArea from "@mui/material/CardActionArea";
import CardContent from "@mui/material/CardContent";
import List from "@mui/material/List";
import ListItemButton from "@mui/material/ListItemButton";
import ListItemText from "@mui/material/ListItemText";
import Skeleton from "@mui/material/Skeleton";
import Typography from "@mui/material/Typography";
import PersonAddAlt from "@mui/icons-material/PersonAddAlt";
import PersonRemoveOutlined from "@mui/icons-material/PersonRemoveOutlined";
import CompareArrows from "@mui/icons-material/CompareArrows";
import CloudUploadOutlined from "@mui/icons-material/CloudUploadOutlined";
import DescriptionOutlined from "@mui/icons-material/DescriptionOutlined";
import Inventory2Outlined from "@mui/icons-material/Inventory2Outlined";
import { api } from "../api";
import { AccessNotice } from "../components/AccessNotice";
import { AccessParity, type AccessParityParams } from "../components/AccessParity";
import { AppTemplates } from "../components/AppTemplates";
import { Contracts } from "../components/Contracts";
import { DeployWizard } from "../components/DeployWizard";
import { NewHire } from "../components/NewHire";
import { Offboard } from "../components/Offboard";
import { BackLink, PageHeader } from "../components/PageHeader";
import { Workflows } from "../components/Workflows";
import type { WorkflowLaunch } from "../components/UserSearch";
import { hasAnyTenantAccess } from "../permissions";
import type { WorkflowSummary } from "../types";
import { useWorkbench } from "../workbench";

const BACK = { label: "Operations", to: "/operations" };

function OperationCard({ to, icon, title, detail }: { to: string; icon: ReactNode; title: string; detail: string }) {
  return (
    <Card variant="outlined" sx={{ minWidth: 0 }}>
      <CardActionArea component={RouterLink} to={to} sx={{ height: "100%" }}>
        <CardContent sx={{ display: "flex", gap: 1.5, alignItems: "flex-start" }}>
          <Box sx={{ color: "primary.main", display: "flex", pt: 0.25 }}>{icon}</Box>
          <Box sx={{ minWidth: 0 }}>
            <Typography variant="subtitle1" component="span" sx={{ display: "block", fontWeight: 600 }}>{title}</Typography>
            <Typography variant="body2" color="text.secondary">{detail}</Typography>
          </Box>
        </CardContent>
      </CardActionArea>
    </Card>
  );
}

function Section({ title, subtitle, children }: { title: string; subtitle?: string; children: ReactNode }) {
  return (
    <Box component="section" sx={{ mb: 3 }}>
      <Typography variant="h6" component="h3">{title}</Typography>
      {subtitle && <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>{subtitle}</Typography>}
      <Box sx={{ display: "grid", gridTemplateColumns: { xs: "1fr", sm: "repeat(2, minmax(0, 1fr))", lg: "repeat(3, minmax(0, 1fr))" }, gap: 1.5, mt: subtitle ? 0 : 1 }}>
        {children}
      </Box>
    </Box>
  );
}

function KnownFixes() {
  const [catalog, setCatalog] = useState<WorkflowSummary[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    api.workflows.list().then(setCatalog).catch((e) => setError(e instanceof Error ? e.message : String(e)));
  }, []);

  const grouped = useMemo(() => (catalog ?? []).reduce<Record<string, WorkflowSummary[]>>((acc, w) => {
    (acc[w.category] ??= []).push(w);
    return acc;
  }, {}), [catalog]);

  return (
    <Box component="section" sx={{ mb: 3 }}>
      <Typography variant="h6" component="h3">Known fixes</Typography>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>
        Each one diagnoses first and shows what it found before it changes anything.
      </Typography>
      {error && <Alert severity="error">Known fixes couldn't load: {error}</Alert>}
      {!error && catalog === null && <Skeleton variant="rounded" height={120} />}
      {catalog && catalog.length === 0 && (
        <Typography variant="body2" color="text.secondary">This server has no known-fix workflows.</Typography>
      )}
      {catalog && catalog.length > 0 && (
        <Box sx={{ display: "grid", gridTemplateColumns: { xs: "1fr", md: "repeat(2, minmax(0, 1fr))" }, gap: 1.5 }}>
          {Object.entries(grouped).map(([category, items]) => (
            <Card key={category} variant="outlined" sx={{ minWidth: 0 }}>
              <CardContent sx={{ pb: 1 }}>
                <Typography variant="overline" color="text.secondary">{category}</Typography>
                <List dense disablePadding>
                  {items.map((w) => (
                    <ListItemButton key={w.id} component={RouterLink} to={`/operations/workflows/${w.id}`} sx={{ borderRadius: 1, px: 1 }}>
                      <ListItemText
                        primary={w.name}
                        secondary={w.description}
                        slotProps={{ secondary: { sx: { display: "-webkit-box", WebkitLineClamp: 2, WebkitBoxOrient: "vertical", overflow: "hidden" } } }}
                      />
                    </ListItemButton>
                  ))}
                </List>
              </CardContent>
            </Card>
          ))}
        </Box>
      )}
    </Box>
  );
}

/** /operations -- every operation PCB can run, grouped by the job it does. */
export function OperationsCatalog() {
  const { me } = useWorkbench();
  return (
    <Box>
      <PageHeader title="Operations" subtitle="Pick the outcome you need. Every operation shows you what it will do before it does it." />
      {!hasAnyTenantAccess(me) && (
        <AccessNotice title="No tenants shared with you yet">
          Operations run against a customer tenant. Ask a tenant Owner to share one with you from that
          tenant's Access tab.
        </AccessNotice>
      )}
      <Section title="People lifecycle">
        <OperationCard to="/operations/onboard" icon={<PersonAddAlt />} title="Onboard a new hire" detail="Create the account with the contract's licenses, groups and defaults." />
        <OperationCard to="/operations/offboard" icon={<PersonRemoveOutlined />} title="Offboard a leaver" detail="Block sign-in, revoke sessions, remove licenses and groups, hand over mail." />
        <OperationCard to="/operations/access-parity" icon={<CompareArrows />} title="Mirror access" detail="Give someone the same group memberships as a colleague, additively." />
      </Section>
      <KnownFixes />
      <Section title="Apps">
        <OperationCard to="/operations/deploy" icon={<CloudUploadOutlined />} title="Deploy an app" detail="Push an app template to Intune in one or more tenants." />
      </Section>
      <Section title="Standards" subtitle="What each customer should have. Set up once, changed rarely.">
        <OperationCard to="/operations/contracts" icon={<DescriptionOutlined />} title="Contracts" detail="Service tiers: desired apps and new-hire defaults per customer." />
        <OperationCard to="/operations/templates" icon={<Inventory2Outlined />} title="App templates" detail="Win32 packages, install commands and detection rules." />
      </Section>
    </Box>
  );
}

export function OnboardPage() {
  const [params] = useSearchParams();
  return (
    <Box>
      <BackLink {...BACK} />
      <NewHire initialTenantId={params.get("tenant") ?? undefined} />
    </Box>
  );
}

export function OffboardPage() {
  const [params] = useSearchParams();
  const tenantId = params.get("tenant");
  const user = params.get("user") ?? undefined;
  const prefill = useMemo(() => (tenantId ? { tenantId, user } : null), [tenantId, user]);
  return (
    <Box>
      <BackLink {...BACK} />
      <Offboard prefill={prefill} />
    </Box>
  );
}

export function DeployPage() {
  return (
    <Box>
      <BackLink {...BACK} />
      <DeployWizard />
    </Box>
  );
}

/**
 * /operations/workflows[/:workflowId]?tenant=&user= -- the known-fix runner. The workflow is in
 * the path and the context (tenant, target user) in the query, so a fix for a specific person is
 * a shareable, bookmarkable link.
 */
export function WorkflowsPage() {
  const { workflowId = "" } = useParams();
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const tenantId = params.get("tenant") ?? "";
  const user = params.get("user") ?? undefined;

  const prefill = useMemo<WorkflowLaunch | null>(
    () => (workflowId || tenantId ? { workflowId, tenantId, inputs: {}, user } : null),
    [workflowId, tenantId, user]
  );

  return (
    <Box>
      <BackLink {...BACK} />
      <Workflows
        prefill={prefill}
        onSelectWorkflow={(id) => {
          if (id !== workflowId) navigate(`/operations/workflows/${id}`);
        }}
      />
    </Box>
  );
}

/**
 * /operations/access-parity?tenant=&source=&target= -- additive group-membership mirroring. The
 * tenant and both people live in the URL, so a comparison is a shareable, refreshable link.
 */
export function AccessParityPage() {
  const { status } = useWorkbench();
  const [params, setParams] = useSearchParams();
  const value = useMemo<AccessParityParams>(() => ({
    tenant: params.get("tenant") ?? undefined,
    source: params.get("source") ?? undefined,
    target: params.get("target") ?? undefined
  }), [params]);
  // The component owns its state after mount; the URL only mirrors it.
  const [initial] = useState(value);
  return (
    <Box>
      <PageHeader
        parent={BACK}
        title="Mirror access"
        subtitle="Give one person the same group memberships as a colleague in the same tenant."
      />
      {status === null ? (
        <AccessNotice title="Not available on this server">
          Mirroring access needs a Partner Center Bridge 0.9.0 or later server with the Access Parity
          operation. Until the server is upgraded, add the groups in the Microsoft 365 admin center.
        </AccessNotice>
      ) : (
        <AccessParity
          params={initial}
          onParamsChange={(next) => {
            const q = new URLSearchParams();
            if (next.tenant) q.set("tenant", next.tenant);
            if (next.source) q.set("source", next.source);
            if (next.target) q.set("target", next.target);
            setParams(q, { replace: true });
          }}
        />
      )}
    </Box>
  );
}

export function ContractsPage() {
  const { me } = useWorkbench();
  return (
    <Box>
      <BackLink {...BACK} />
      <Contracts me={me} />
    </Box>
  );
}

export function TemplatesPage() {
  const { me } = useWorkbench();
  return (
    <Box>
      <BackLink {...BACK} />
      <AppTemplates me={me} />
    </Box>
  );
}
