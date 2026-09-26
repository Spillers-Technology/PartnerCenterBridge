import { useEffect, useState } from "react";
import { Link as RouterLink, Outlet, useLocation, useParams, useSearchParams } from "react-router";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Card from "@mui/material/Card";
import CardContent from "@mui/material/CardContent";
import Link from "@mui/material/Link";
import MenuItem from "@mui/material/MenuItem";
import Skeleton from "@mui/material/Skeleton";
import Stack from "@mui/material/Stack";
import Tab from "@mui/material/Tab";
import Tabs from "@mui/material/Tabs";
import TextField from "@mui/material/TextField";
import ToggleButton from "@mui/material/ToggleButton";
import ToggleButtonGroup from "@mui/material/ToggleButtonGroup";
import Typography from "@mui/material/Typography";
import { api, isApiStatus } from "../api";
import { Timestamp } from "../format";
import { AccessNotice } from "../components/AccessNotice";
import { Approvals } from "../components/Approvals";
import { Deployments } from "../components/Deployments";
import { EvidencePanel } from "../components/EvidencePanel";
import { Fact, FindingList, OutcomeChip, RunResultChips } from "../components/operationUi";
import { PageHeader } from "../components/PageHeader";
import { RunList } from "../components/RunList";
import { StepList } from "../components/StepList";
import type { OperationEvidence, Tenant, WorkflowRunRecord } from "../types";

export { RunResultChips };

type Kind = "all" | "runs" | "deployments";

/** /activity -- a History | Approvals switcher over the unified record of what PCB did. */
export function ActivityLayout() {
  const location = useLocation();
  const onApprovals = location.pathname.startsWith("/activity/approvals");
  return (
    <Box>
      <PageHeader title="Activity" subtitle="Everything PCB ran, deployed, or is waiting on approval for." />
      <Tabs value={onApprovals ? "approvals" : "history"} aria-label="Activity sections" sx={{ mb: 2, borderBottom: 1, borderColor: "divider" }}>
        <Tab value="history" label="History" component={RouterLink} to="/activity" />
        <Tab value="approvals" label="Approvals" component={RouterLink} to="/activity/approvals" />
      </Tabs>
      <Outlet />
    </Box>
  );
}

export function ApprovalsPage() {
  return <Approvals />;
}

/** /activity?tenant=&kind= -- workflow runs and deployments, filterable, filters kept in the URL. */
export function ActivityHistory() {
  const [params, setParams] = useSearchParams();
  const tenantId = params.get("tenant") ?? "";
  const rawKind = params.get("kind");
  const kind: Kind = rawKind === "runs" || rawKind === "deployments" ? rawKind : "all";

  const [tenants, setTenants] = useState<Tenant[]>([]);
  const [runs, setRuns] = useState<WorkflowRunRecord[] | null>(null);
  const [runsError, setRunsError] = useState<string | null>(null);

  useEffect(() => {
    api.tenants.list().then(setTenants).catch(() => {});
  }, []);

  useEffect(() => {
    if (kind === "deployments") return;
    let alive = true;
    setRuns(null);
    setRunsError(null);
    api.workflows.runs({ tenantId: tenantId || undefined, take: 50 })
      .then((r) => { if (alive) setRuns(r); })
      .catch((e) => { if (alive) setRunsError(e instanceof Error ? e.message : String(e)); });
    return () => { alive = false; };
  }, [tenantId, kind]);

  const update = (next: { tenant?: string; kind?: Kind }) => {
    const p = new URLSearchParams(params);
    if (next.tenant !== undefined) { if (next.tenant) p.set("tenant", next.tenant); else p.delete("tenant"); }
    if (next.kind !== undefined) { if (next.kind !== "all") p.set("kind", next.kind); else p.delete("kind"); }
    setParams(p);
  };

  // A tenant id from a link that isn't in the list (not shared, or removed) still filters; name it honestly.
  const tenantKnown = !tenantId || tenants.length === 0 || tenants.some((t) => t.id === tenantId);

  return (
    <Box>
      <Stack direction={{ xs: "column", sm: "row" }} spacing={1.5} sx={{ mb: 2, alignItems: { sm: "center" } }}>
        <TextField
          select
          size="small"
          label="Tenant"
          value={tenantKnown ? tenantId : ""}
          onChange={(e) => update({ tenant: e.target.value })}
          sx={{ minWidth: 220 }}
          slotProps={{ select: { displayEmpty: true }, inputLabel: { shrink: true } }}
        >
          <MenuItem value=""><em>All tenants</em></MenuItem>
          {tenants.map((t) => <MenuItem key={t.id} value={t.id}>{t.displayName}</MenuItem>)}
        </TextField>
        <ToggleButtonGroup
          size="small"
          exclusive
          value={kind}
          onChange={(_, v: Kind | null) => { if (v) update({ kind: v }); }}
          aria-label="Kind of activity"
        >
          <ToggleButton value="all">All</ToggleButton>
          <ToggleButton value="runs">Workflow runs</ToggleButton>
          <ToggleButton value="deployments">Deployments</ToggleButton>
        </ToggleButtonGroup>
      </Stack>
      {!tenantKnown && (
        <Alert severity="info" sx={{ mb: 2 }}>
          Filtered to a tenant that isn't in your list. <Link component="button" onClick={() => update({ tenant: "" })}>Show all tenants</Link>
        </Alert>
      )}

      {kind !== "deployments" && (
        <Box component="section" sx={{ mb: 3 }}>
          <Typography variant="h6" component="h3" gutterBottom>Workflow runs</Typography>
          {runsError && <Alert severity="error">{runsError}</Alert>}
          {!runsError && runs === null && <Skeleton variant="rounded" height={120} />}
          {runs && runs.length === 0 && (
            <Typography variant="body2" color="text.secondary">No runs recorded{tenantId ? " for this tenant" : ""} yet.</Typography>
          )}
          {runs && runs.length > 0 && <RunList runs={runs} label="Workflow runs" />}
        </Box>
      )}

      {kind !== "runs" && (
        <Box component="section">
          <Deployments tenantId={tenantId || undefined} heading="Deployments" />
        </Box>
      )}
    </Box>
  );
}

/**
 * /activity/runs/:runId -- one recorded run as evidence: outcome, who/what/where, what was
 * checked, changed and verified, and ticket notes. Servers before 0.9.0 have no evidence endpoint;
 * there the run is found in the recent history instead and shown as findings and steps.
 */
export function RunDetailPage() {
  const { runId = "" } = useParams();
  const [evidence, setEvidence] = useState<OperationEvidence | null>(null);
  const [legacyRun, setLegacyRun] = useState<WorkflowRunRecord | null>(null);
  const [state, setState] = useState<"loading" | "ready" | "missing" | "error">("loading");
  const [error, setError] = useState<string | null>(null);
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let alive = true;
    setState("loading");
    setEvidence(null);
    setLegacyRun(null);
    setError(null);
    const fallback = async () => {
      const runs = await api.workflows.runs({ take: 200 });
      if (!alive) return;
      const run = runs.find((r) => r.id === runId) ?? null;
      setLegacyRun(run);
      setState(run ? "ready" : "missing");
    };
    api.workflows.evidence(runId)
      .then((e) => { if (alive) { setEvidence(e); setState("ready"); } })
      .catch(async (e) => {
        if (!alive) return;
        if (isApiStatus(e, 403)) {
          // The run's tenant isn't shared with this user.
          setState("missing");
          return;
        }
        // 404 is an unknown run -- or an older server without the endpoint; the history decides.
        // Any other failure also falls back, so a flaky evidence read still shows what's known.
        try {
          await fallback();
        } catch (inner) {
          if (!alive) return;
          setError(isApiStatus(e, 404) ? (inner instanceof Error ? inner.message : String(inner)) : (e instanceof Error ? e.message : String(e)));
          setState("error");
        }
      });
    return () => { alive = false; };
  }, [runId, attempt]);

  const parent = { label: "Activity", to: "/activity" };

  if (state === "error") {
    return (
      <Box>
        <PageHeader title="Run" parent={parent} />
        <Alert severity="error" action={<Button color="inherit" size="small" onClick={() => setAttempt((n) => n + 1)}>Retry</Button>}>
          {error}
        </Alert>
      </Box>
    );
  }
  if (state === "loading") {
    return (
      <Box aria-busy="true">
        <PageHeader title="Run" parent={parent} />
        <Skeleton variant="rounded" height={160} />
      </Box>
    );
  }
  if (state === "missing") {
    return (
      <Box>
        <PageHeader title="Run not found" parent={parent} />
        <AccessNotice title="This run isn't available to you">
          It may be in a tenant that isn't shared with you, older than the recent history on this
          server, or the link is wrong. <Link component={RouterLink} to="/activity">Browse all activity</Link>.
        </AccessNotice>
      </Box>
    );
  }

  if (evidence) {
    const who = evidence.target ? evidence.target.displayName || evidence.target.id : null;
    return (
      <Box>
        <PageHeader
          parent={parent}
          title={evidence.operationName || "Run"}
          meta={<OutcomeChip outcome={evidence.outcome} />}
          subtitle={who ? `${who} in ${evidence.tenant.displayName}` : evidence.tenant.displayName}
        />
        <EvidencePanel evidence={evidence} />
      </Box>
    );
  }

  const run = legacyRun!;
  const inputs = Object.entries(run.inputs ?? {});
  return (
    <Box>
      <PageHeader parent={parent} title={run.workflowName} meta={<RunResultChips run={run} />} />
      <Stack spacing={2}>
        <Card variant="outlined">
          <CardContent>
            <Box sx={{ display: "grid", gridTemplateColumns: { xs: "1fr", sm: "repeat(3, minmax(0, 1fr))" }, gap: 1.5 }}>
              <Fact label="Tenant">
                <Link component={RouterLink} to={`/tenants/${run.tenantId}`} underline="hover">{run.tenantName}</Link>
              </Fact>
              <Fact label="Kind">{run.kind}</Fact>
              <Fact label="Operator">{run.operator}</Fact>
              <Fact label="Started"><Timestamp value={run.startedAt} /></Fact>
              <Fact label="Duration">{(run.durationMs / 1000).toFixed(1)} s</Fact>
              <Fact label="Run id"><Box component="span" sx={{ fontFamily: "monospace" }}>{run.id}</Box></Fact>
            </Box>
            {inputs.length > 0 && (
              <Box sx={{ mt: 2 }}>
                <Typography variant="subtitle2" gutterBottom>Inputs</Typography>
                {inputs.map(([k, v]) => (
                  <Typography key={k} variant="body2" sx={{ fontFamily: "monospace", overflowWrap: "anywhere" }}>
                    {k}: {v}
                  </Typography>
                ))}
              </Box>
            )}
          </CardContent>
        </Card>

        <Typography variant="body2" color="text.secondary">
          This server doesn't provide structured evidence for this run; showing what the run history recorded.
        </Typography>

        {run.error && <Alert severity="error">{run.error}</Alert>}

        {run.findings.length > 0 && (
          <Box component="section">
            <Typography variant="h6" component="h3" gutterBottom>Findings</Typography>
            <FindingList findings={run.findings} label="Findings" />
          </Box>
        )}

        {run.steps.length > 0 && (
          <Box component="section" sx={{ overflowX: "auto" }}>
            <Typography variant="h6" component="h3" gutterBottom>Changes</Typography>
            <StepList result={{ steps: run.steps, succeeded: run.succeeded }} />
          </Box>
        )}

        {run.findings.length === 0 && run.steps.length === 0 && !run.error && (
          <Typography variant="body2" color="text.secondary">
            This run recorded no findings or steps.
          </Typography>
        )}
      </Stack>
    </Box>
  );
}
