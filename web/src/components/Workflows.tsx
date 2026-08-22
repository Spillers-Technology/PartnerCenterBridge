import { useEffect, useMemo, useRef, useState } from "react";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Checkbox from "@mui/material/Checkbox";
import Chip from "@mui/material/Chip";
import FormControl from "@mui/material/FormControl";
import FormControlLabel from "@mui/material/FormControlLabel";
import InputLabel from "@mui/material/InputLabel";
import List from "@mui/material/List";
import ListItemButton from "@mui/material/ListItemButton";
import ListItemText from "@mui/material/ListItemText";
import ListSubheader from "@mui/material/ListSubheader";
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
import type { DiagnosisResult, Finding, Tenant, WorkflowRunRecord, WorkflowRunResult, WorkflowSummary } from "../types";
import { useAsyncAction } from "../hooks/useAsyncAction";
import { useConfirm } from "../hooks/useConfirm";
import { useToast } from "../hooks/useToast";
import { StepList } from "./StepList";
import type { WorkflowLaunch } from "./UserSearch";

type FindingChipColor = "success" | "info" | "warning" | "error";
const findingColor: Record<Finding["status"], FindingChipColor> = {
  Ok: "success", Info: "info", Warning: "warning", Blocker: "error"
};

function Findings({ result, title }: { result: DiagnosisResult; title: string }) {
  return (
    <Box sx={{ mb: 3 }}>
      <Stack direction="row" spacing={1} sx={{ alignItems: "center", mb: 1 }}>
        <Typography variant="h6" component="h3">
          {title}
        </Typography>
        <Chip
          size="small"
          label={result.healthy ? "healthy" : "needs fixing"}
          color={result.healthy ? "success" : "error"}
        />
      </Stack>
      <TableContainer>
        <Table size="small">
          <TableHead>
            <TableRow>
              <TableCell>Check</TableCell>
              <TableCell>Status</TableCell>
              <TableCell>Detail</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {result.findings.map((f, i) => (
              <TableRow key={i}>
                <TableCell>{f.name}</TableCell>
                <TableCell>
                  <Chip size="small" label={f.status} color={findingColor[f.status]} />
                </TableCell>
                <TableCell sx={{ fontFamily: "monospace", color: "text.secondary" }}>{f.detail ?? ""}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </TableContainer>
    </Box>
  );
}

export function Workflows({ prefill }: { prefill?: WorkflowLaunch | null }) {
  const [catalog, setCatalog] = useState<WorkflowSummary[]>([]);
  const [tenants, setTenants] = useState<Tenant[]>([]);
  const [tenantId, setTenantId] = useState("");
  const [selectedId, setSelectedId] = useState("");
  const [inputs, setInputs] = useState<Record<string, string>>({});
  const [diagnosis, setDiagnosis] = useState<DiagnosisResult | null>(null);
  const [run, setRun] = useState<WorkflowRunResult | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [runs, setRuns] = useState<WorkflowRunRecord[]>([]);
  const [lastAction, setLastAction] = useState<"diagnose" | "fix" | null>(null);

  const confirm = useConfirm();
  const toast = useToast();

  const loadRuns = () => api.workflows.runs({ take: 25 }).then(setRuns).catch(() => {});

  useEffect(() => {
    Promise.all([api.workflows.list(), api.tenants.list()])
      .then(([w, t]) => { setCatalog(w); setTenants(t); })
      .catch((e) => setLoadError(e instanceof Error ? e.message : String(e)));
    loadRuns();
  }, []);

  const selected = useMemo(() => catalog.find((w) => w.id === selectedId), [catalog, selectedId]);
  const selectedTenant = useMemo(() => tenants.find((t) => t.id === tenantId), [tenants, tenantId]);

  // Tracks the workflow+tenant+inputs this render is showing, so an in-flight diagnose/fix
  // response that resolves after the user has since switched tenant, workflow, or input values
  // can be told apart from one that's still describing what's currently on screen. Without this,
  // a diagnosis started against Tenant A could still land and render as if it described Tenant B.
  // Assigned every render (not in an effect) so it's always current by the time an async response
  // needs to check it.
  const currentContextRef = useRef("");
  currentContextRef.current = JSON.stringify({ selectedId, tenantId, inputs });

  // Arriving from Find User: select the workflow, tenant, and inputs in one go.
  useEffect(() => {
    if (!prefill || catalog.length === 0) return;
    const w = catalog.find((x) => x.id === prefill.workflowId);
    if (!w) return;
    setSelectedId(w.id);
    setTenantId(prefill.tenantId);
    setInputs({ ...Object.fromEntries(w.inputs.map((i) => [i.key, i.default ?? ""])), ...prefill.inputs });
    setDiagnosis(null); setRun(null);
  }, [prefill, catalog]);

  // Diagnosis/fix output describes a specific workflow+tenant+inputs combination -- any change to
  // that combination makes the currently displayed output stale, not just a still-in-flight one.
  const clearOutput = () => { setDiagnosis(null); setRun(null); };

  const pick = (id: string) => {
    setSelectedId(id);
    clearOutput();
    const w = catalog.find((x) => x.id === id);
    setInputs(Object.fromEntries((w?.inputs ?? []).map((i) => [i.key, i.default ?? ""])));
  };

  const ready = Boolean(tenantId && selected &&
    selected.inputs.filter((i) => i.required).every((i) => (inputs[i.key] ?? "").trim()));

  const diagnoseAction = useAsyncAction(async () => {
    const requestContext = currentContextRef.current;
    setRun(null);
    const d = await api.workflows.diagnose(selected!.id, tenantId, inputs);
    loadRuns();
    if (currentContextRef.current === requestContext) setDiagnosis(d);
    return d;
  });

  const fixAction = useAsyncAction(async () => {
    const requestContext = currentContextRef.current;
    const r = await api.workflows.remediate(selected!.id, tenantId, inputs);
    loadRuns();
    if (currentContextRef.current === requestContext) {
      setRun(r);
      if (r.postState) setDiagnosis(r.postState);
    }
    toast(r.succeeded ? "Fix applied successfully." : "Fix ran but did not fully succeed - see the step detail.", r.succeeded ? "success" : "warning");
    return r;
  });

  const busy = diagnoseAction.busy || fixAction.busy;
  const actionError = lastAction === "diagnose" ? diagnoseAction.error : lastAction === "fix" ? fixAction.error : null;

  const diagnose = () => { setLastAction("diagnose"); void diagnoseAction.run(); };
  const fix = async () => {
    if (!(await confirm({
      title: "Apply this fix?",
      message: `Apply "${selected?.name}" for ${selectedTenant?.displayName ?? "this tenant"}? This will make real changes.`,
      destructive: true
    }))) return;
    setLastAction("fix");
    void fixAction.run();
  };

  const grouped = catalog.reduce<Record<string, WorkflowSummary[]>>((acc, w) => {
    (acc[w.category] ??= []).push(w); return acc;
  }, {});

  return (
    <Box>
      <Typography variant="h5" component="h2" gutterBottom>
        Workflows
      </Typography>

      {loadError && <Alert severity="error" sx={{ mb: 2 }}>{loadError}</Alert>}

      <Box sx={{ display: "flex", flexDirection: { xs: "column", md: "row" }, gap: 2, mb: 3 }}>
        <Box sx={{ width: { xs: "100%", md: 260 }, flexShrink: 0 }}>
          {catalog.length === 0 ? (
            <Typography variant="body2" color="text.secondary">No workflows.</Typography>
          ) : (
            <List dense disablePadding subheader={<li />}>
              {Object.entries(grouped).map(([cat, items]) => (
                <li key={cat}>
                  <ul style={{ padding: 0 }}>
                    <ListSubheader disableSticky>{cat}</ListSubheader>
                    {items.map((w) => (
                      <ListItemButton key={w.id} selected={selectedId === w.id} onClick={() => pick(w.id)}>
                        <ListItemText primary={w.name} />
                      </ListItemButton>
                    ))}
                  </ul>
                </li>
              ))}
            </List>
          )}
        </Box>

        <Box sx={{ flex: 1, minWidth: 0 }}>
          {!selected && <Typography variant="body2" color="text.secondary">Pick a workflow.</Typography>}
          {selected && (
            <Stack spacing={2}>
              <Typography variant="body2" color="text.secondary">{selected.description}</Typography>

              <FormControl size="small" sx={{ maxWidth: 360 }}>
                <InputLabel id="wf-tenant-label">Tenant</InputLabel>
                <Select
                  labelId="wf-tenant-label"
                  label="Tenant"
                  value={tenantId}
                  onChange={(e) => { setTenantId(e.target.value); clearOutput(); }}
                >
                  <MenuItem value="">
                    <em>choose</em>
                  </MenuItem>
                  {tenants.map((t) => <MenuItem key={t.id} value={t.id}>{t.displayName}</MenuItem>)}
                </Select>
              </FormControl>

              {selected.inputs.map((i) => i.type === "bool" ? (
                <FormControlLabel
                  key={i.key}
                  control={
                    <Checkbox
                      checked={(inputs[i.key] ?? "true") === "true"}
                      onChange={(e) => { setInputs({ ...inputs, [i.key]: String(e.target.checked) }); clearOutput(); }}
                    />
                  }
                  label={i.label}
                />
              ) : (
                <TextField
                  key={i.key}
                  label={i.label + (i.required ? "" : " (optional)")}
                  placeholder={i.placeholder}
                  value={inputs[i.key] ?? ""}
                  onChange={(e) => { setInputs({ ...inputs, [i.key]: e.target.value }); clearOutput(); }}
                  size="small"
                  sx={{ maxWidth: 360 }}
                />
              ))}

              <Stack direction="row" spacing={1} useFlexGap sx={{ flexWrap: "wrap" }}>
                <Button variant="outlined" onClick={diagnose} disabled={!ready || busy}>
                  {diagnoseAction.busy ? "Checking..." : "Diagnose"}
                </Button>
                <Button variant="contained" color="warning" onClick={fix} disabled={!ready || busy}>
                  {fixAction.busy ? "Applying..." : "Apply fix"}
                </Button>
              </Stack>

              {actionError && <Alert severity="error">{actionError}</Alert>}
              {diagnosis && <Findings result={diagnosis} title="Diagnosis" />}
              {run && (
                <Box sx={{ overflowX: "auto" }}>
                  <StepList result={{ steps: run.steps, succeeded: run.succeeded }} />
                </Box>
              )}
              {run?.ephemeral && Object.entries(run.ephemeral).map(([k, v]) => (
                <Typography key={k} variant="body2">
                  {k}:{" "}
                  <Box component="span" sx={{ fontFamily: "monospace", overflowWrap: "break-word", wordBreak: "break-all" }}>
                    {v}
                  </Box>{" "}
                  (shown once - not recorded)
                </Typography>
              ))}
            </Stack>
          )}
        </Box>
      </Box>

      <Box>
        <Typography variant="h6" component="h3" gutterBottom>
          Recent runs
        </Typography>
        {runs.length === 0 ? (
          <Typography variant="body2" color="text.secondary">No runs recorded yet.</Typography>
        ) : (
          <TableContainer>
            <Table size="small">
              <TableHead>
                <TableRow>
                  <TableCell>When</TableCell>
                  <TableCell>Workflow</TableCell>
                  <TableCell>Tenant</TableCell>
                  <TableCell>Kind</TableCell>
                  <TableCell>Operator</TableCell>
                  <TableCell>Result</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {runs.map((r) => (
                  <TableRow key={r.id} title={r.error ?? undefined}>
                    <TableCell>{new Date(r.startedAt).toLocaleString()}</TableCell>
                    <TableCell>{r.workflowName}</TableCell>
                    <TableCell>{r.tenantName}</TableCell>
                    <TableCell>{r.kind}</TableCell>
                    <TableCell>{r.operator}</TableCell>
                    <TableCell>
                      <Stack direction="row" spacing={0.5} useFlexGap sx={{ flexWrap: "wrap" }}>
                        <Chip size="small" label={r.succeeded ? "ok" : "failed"} color={r.succeeded ? "success" : "error"} />
                        {r.healthy !== null && r.healthy !== undefined && (
                          <Chip size="small" label={r.healthy ? "healthy" : "needs fixing"} color={r.healthy ? "success" : "warning"} />
                        )}
                      </Stack>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </TableContainer>
        )}
      </Box>
    </Box>
  );
}
