import { useEffect, useMemo, useState, type ReactNode } from "react";
import { Link as RouterLink, Outlet, useLocation, useParams, useSearchParams } from "react-router";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Card from "@mui/material/Card";
import CardContent from "@mui/material/CardContent";
import Chip from "@mui/material/Chip";
import Link from "@mui/material/Link";
import MenuItem from "@mui/material/MenuItem";
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
import TextField from "@mui/material/TextField";
import ToggleButton from "@mui/material/ToggleButton";
import ToggleButtonGroup from "@mui/material/ToggleButtonGroup";
import Typography from "@mui/material/Typography";
import { api } from "../api";
import { humanizeEnum, Timestamp } from "../format";
import { AccessNotice } from "../components/AccessNotice";
import { Approvals } from "../components/Approvals";
import { Deployments } from "../components/Deployments";
import { PageHeader } from "../components/PageHeader";
import { StepList } from "../components/StepList";
import type { Finding, Outcome, Tenant, WorkflowRunRecord } from "../types";

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

const OUTCOME_COLOR: Record<Outcome, "success" | "warning" | "error" | "info" | "default"> = {
  Succeeded: "success",
  NoChangeNeeded: "success",
  PartiallySucceeded: "warning",
  VerificationFailed: "error",
  Failed: "error",
  Planned: "info"
};

export function RunResultChips({ run }: { run: WorkflowRunRecord }) {
  return (
    <Stack direction="row" spacing={0.5} useFlexGap sx={{ flexWrap: "wrap" }}>
      {run.outcome ? (
        <Chip size="small" label={humanizeEnum(run.outcome)} color={OUTCOME_COLOR[run.outcome] ?? "default"} />
      ) : (
        <Chip size="small" label={run.succeeded ? "ok" : "failed"} color={run.succeeded ? "success" : "error"} />
      )}
      {run.healthy !== null && run.healthy !== undefined && (
        <Chip size="small" label={run.healthy ? "healthy" : "needs fixing"} color={run.healthy ? "success" : "warning"} variant="outlined" />
      )}
    </Stack>
  );
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
          {runs && runs.length > 0 && (
            <TableContainer sx={{ overflowX: "auto" }}>
              <Table size="small" aria-label="Workflow runs">
                <TableHead>
                  <TableRow>
                    <TableCell>When</TableCell>
                    <TableCell>Operation</TableCell>
                    <TableCell>Tenant</TableCell>
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
                      <TableCell>
                        <Link component={RouterLink} to={`/tenants/${r.tenantId}`} underline="hover" color="inherit">{r.tenantName}</Link>
                      </TableCell>
                      <TableCell>{r.kind}</TableCell>
                      <TableCell>{r.operator}</TableCell>
                      <TableCell>
                        <RunResultChips run={r} />
                        {!r.succeeded && r.error && (
                          <Typography variant="body2" color="error" sx={{ mt: 0.5, wordBreak: "break-word" }}>{r.error}</Typography>
                        )}
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </TableContainer>
          )}
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

function Fact({ label, children }: { label: string; children: ReactNode }) {
  return (
    <Box sx={{ minWidth: 0 }}>
      <Typography variant="caption" color="text.secondary" component="div">{label}</Typography>
      <Typography variant="body2" component="div" sx={{ overflowWrap: "anywhere" }}>{children}</Typography>
    </Box>
  );
}

const FINDING_COLOR: Record<Finding["status"], "success" | "info" | "warning" | "error"> = {
  Ok: "success", Info: "info", Warning: "warning", Blocker: "error"
};

/**
 * /activity/runs/:runId -- one recorded run: what was run where, by whom, what it found and what
 * it changed. Built from the run history (no per-run endpoint on older servers).
 */
export function RunDetailPage() {
  const { runId = "" } = useParams();
  const [runs, setRuns] = useState<WorkflowRunRecord[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let alive = true;
    api.workflows.runs({ take: 200 })
      .then((r) => { if (alive) setRuns(r); })
      .catch((e) => { if (alive) setError(e instanceof Error ? e.message : String(e)); });
    return () => { alive = false; };
  }, [runId]);

  const run = useMemo(() => runs?.find((r) => r.id === runId), [runs, runId]);
  const parent = { label: "Activity", to: "/activity" };

  if (error) {
    return (
      <Box>
        <PageHeader title="Run" parent={parent} />
        <Alert severity="error">{error}</Alert>
      </Box>
    );
  }
  if (runs === null) {
    return (
      <Box aria-busy="true">
        <PageHeader title="Run" parent={parent} />
        <Skeleton variant="rounded" height={160} />
      </Box>
    );
  }
  if (!run) {
    return (
      <Box>
        <PageHeader title="Run not found" parent={parent} />
        <AccessNotice title="This run isn't in the recent history">
          It may be older than the last 200 runs, in a tenant that isn't shared with you, or the link
          is wrong. <Link component={RouterLink} to="/activity">Browse all activity</Link>.
        </AccessNotice>
      </Box>
    );
  }

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

        {run.error && <Alert severity="error">{run.error}</Alert>}

        {run.findings.length > 0 && (
          <Box component="section">
            <Typography variant="h6" component="h3" gutterBottom>Findings</Typography>
            <TableContainer sx={{ overflowX: "auto" }}>
              <Table size="small">
                <TableHead>
                  <TableRow>
                    <TableCell>Check</TableCell>
                    <TableCell>Status</TableCell>
                    <TableCell>Detail</TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {run.findings.map((f, i) => (
                    <TableRow key={i}>
                      <TableCell>{f.name}</TableCell>
                      <TableCell><Chip size="small" label={f.status} color={FINDING_COLOR[f.status]} /></TableCell>
                      <TableCell sx={{ color: "text.secondary", overflowWrap: "anywhere" }}>{f.detail ?? ""}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </TableContainer>
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
