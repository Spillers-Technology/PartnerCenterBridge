import { useEffect, useState, type ReactNode } from "react";
import { Link as RouterLink, useNavigate } from "react-router";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Card from "@mui/material/Card";
import CardActionArea from "@mui/material/CardActionArea";
import CardContent from "@mui/material/CardContent";
import Stack from "@mui/material/Stack";
import TextField from "@mui/material/TextField";
import Typography from "@mui/material/Typography";
import SearchIcon from "@mui/icons-material/Search";
import PersonAddAlt from "@mui/icons-material/PersonAddAlt";
import PersonRemoveOutlined from "@mui/icons-material/PersonRemoveOutlined";
import CompareArrows from "@mui/icons-material/CompareArrows";
import BuildOutlined from "@mui/icons-material/BuildOutlined";
import { api } from "../api";
import { Dashboard } from "../components/Dashboard";
import { DiagnosticsList, needsAttention } from "../components/DiagnosticsList";
import { PageHeader } from "../components/PageHeader";
import { useDiagnostics } from "../hooks/useDiagnostics";
import { hasAnyTenantAccess } from "../permissions";
import { useWorkbench } from "../workbench";

function EntryCard({ to, icon, title, detail }: { to: string; icon: ReactNode; title: string; detail: string }) {
  return (
    <Card variant="outlined" sx={{ flex: "1 1 200px", minWidth: 0 }}>
      <CardActionArea component={RouterLink} to={to} sx={{ height: "100%" }}>
        <CardContent sx={{ display: "flex", gap: 1.5, alignItems: "flex-start" }}>
          <Box sx={{ color: "primary.main", display: "flex", pt: 0.25 }}>{icon}</Box>
          <Box sx={{ minWidth: 0 }}>
            <Typography variant="subtitle1" component="span" sx={{ display: "block", fontWeight: 600 }}>
              {title}
            </Typography>
            <Typography variant="body2" color="text.secondary">
              {detail}
            </Typography>
          </Box>
        </CardContent>
      </CardActionArea>
    </Card>
  );
}

/** The concise "finish setting up" card: only the checks that still need something, each with its fix. */
function SetupChecklist() {
  const diagnostics = useDiagnostics();
  if (diagnostics.status !== "ready") return null;
  const open = (diagnostics.data.checks ?? []).filter(needsAttention);
  if (open.length === 0) return null;
  return (
    <Card variant="outlined" sx={{ mb: 3, borderColor: "warning.main" }} component="section" aria-labelledby="setup-heading">
      <CardContent>
        <Stack direction="row" sx={{ alignItems: "baseline", justifyContent: "space-between", gap: 1, flexWrap: "wrap" }}>
          <Typography id="setup-heading" variant="h6" component="h3">
            Finish setting up
          </Typography>
          <Button component={RouterLink} to="/settings/workbench" size="small">
            All checks
          </Button>
        </Stack>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>
          {open.length === 1 ? "One thing still needs" : `${open.length} things still need`} attention before
          everything in the workbench can work.
        </Typography>
        <DiagnosticsList checks={open} />
      </CardContent>
    </Card>
  );
}

function PendingApprovalsNotice() {
  const [pending, setPending] = useState(0);
  useEffect(() => {
    api.pendingActions.list()
      .then((items) => setPending(items.filter((i) => i.status === "Pending").length))
      .catch(() => {});
  }, []);
  if (pending === 0) return null;
  return (
    <Alert
      severity="warning"
      sx={{ mb: 2 }}
      action={<Button component={RouterLink} to="/activity/approvals" color="inherit" size="small">Review</Button>}
    >
      {pending === 1 ? "1 action is" : `${pending} actions are`} waiting for approval.
    </Alert>
  );
}

export default function Home() {
  const navigate = useNavigate();
  const { me } = useWorkbench();
  const [query, setQuery] = useState("");

  return (
    <Box>
      <PageHeader title="Home" subtitle="Start from the person, the tenant, or the outcome you need." />

      {!hasAnyTenantAccess(me) && (
        <Alert severity="info" variant="outlined" sx={{ mb: 2 }}>
          No tenants have been shared with you yet. Ask a tenant Owner to grant you access from the
          tenant's Access tab; everything tenant-scoped appears here once they do.
        </Alert>
      )}

      <Card variant="outlined" sx={{ mb: 2 }}>
        <CardContent>
          <Stack
            component="form"
            role="search"
            direction={{ xs: "column", sm: "row" }}
            spacing={1}
            onSubmit={(ev) => {
              ev.preventDefault();
              const q = query.trim();
              navigate(q ? `/people?q=${encodeURIComponent(q)}` : "/people");
            }}
          >
            <TextField
              label="Find a person"
              placeholder="Name or UPN, across every tenant"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              size="small"
              fullWidth
            />
            <Button type="submit" variant="contained" startIcon={<SearchIcon />} sx={{ flexShrink: 0 }}>
              Search
            </Button>
          </Stack>
        </CardContent>
      </Card>

      {/* Search stays first so a technician can start typing immediately; setup follows. */}
      <SetupChecklist />

      <Box sx={{ display: "flex", flexWrap: "wrap", gap: 1.5, mb: 3 }}>
        <EntryCard to="/operations/onboard" icon={<PersonAddAlt />} title="Onboard" detail="Create a new hire with the contract's licenses and groups." />
        <EntryCard to="/operations/offboard" icon={<PersonRemoveOutlined />} title="Offboard" detail="Block, revoke, unlicense and hand over a leaver." />
        <EntryCard to="/operations/access-parity" icon={<CompareArrows />} title="Mirror access" detail="Give someone the same groups as a colleague." />
        <EntryCard to="/operations/workflows" icon={<BuildOutlined />} title="Run a known fix" detail="Diagnose, then fix MFA, passwords, licenses, mailboxes." />
      </Box>

      <PendingApprovalsNotice />

      <Dashboard
        heading={null}
        onNavigate={(t) => navigate(t === "tenants" ? "/tenants" : t === "history" ? "/activity?kind=deployments" : "/activity?kind=runs")}
        onOpenTenant={(id) => navigate(`/tenants/${id}`)}
        onOpenRun={(id) => navigate(`/activity/runs/${id}`)}
      />
    </Box>
  );
}
