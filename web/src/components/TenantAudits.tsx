import { useEffect, useMemo, useState } from "react";
import { Link as RouterLink } from "react-router";
import Accordion from "@mui/material/Accordion";
import AccordionDetails from "@mui/material/AccordionDetails";
import AccordionSummary from "@mui/material/AccordionSummary";
import Alert from "@mui/material/Alert";
import AlertTitle from "@mui/material/AlertTitle";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Card from "@mui/material/Card";
import CardContent from "@mui/material/CardContent";
import Checkbox from "@mui/material/Checkbox";
import Chip from "@mui/material/Chip";
import FormControl from "@mui/material/FormControl";
import FormControlLabel from "@mui/material/FormControlLabel";
import FormGroup from "@mui/material/FormGroup";
import InputLabel from "@mui/material/InputLabel";
import LinearProgress from "@mui/material/LinearProgress";
import Link from "@mui/material/Link";
import MenuItem from "@mui/material/MenuItem";
import Select from "@mui/material/Select";
import Skeleton from "@mui/material/Skeleton";
import Stack from "@mui/material/Stack";
import Table from "@mui/material/Table";
import TableBody from "@mui/material/TableBody";
import TableCell from "@mui/material/TableCell";
import TableContainer from "@mui/material/TableContainer";
import TableHead from "@mui/material/TableHead";
import TableRow from "@mui/material/TableRow";
import TextField from "@mui/material/TextField";
import Typography from "@mui/material/Typography";
import Download from "@mui/icons-material/Download";
import ExpandMore from "@mui/icons-material/ExpandMore";
import { api, errorText } from "../api";
import { formatTimestamp, pluralize, Timestamp } from "../format";
import { useToast } from "../hooks/useToast";
import { offboardPath, personPath, workflowLink } from "../paths";
import type {
  AuditCatalog, AuditCheckResult, AuditExportFormat, AuditFinding, AuditHealth, AuditParameterDefinition,
  AuditSeverity, AuditSubject, Tenant, TenantAuditReport, TenantAuditRunSummary
} from "../types";

type ChipColor = "default" | "success" | "info" | "warning" | "error";

export const SEVERITY_META: Record<AuditSeverity, { label: string; color: ChipColor }> = {
  Fail: { label: "Fail", color: "error" },
  Warn: { label: "Warn", color: "warning" },
  Unknown: { label: "Unknown", color: "default" },
  Info: { label: "Info", color: "info" },
  Pass: { label: "Pass", color: "success" }
};
const SEVERITY_ORDER: AuditSeverity[] = ["Fail", "Warn", "Unknown", "Info", "Pass"];

export const HEALTH_META: Record<AuditHealth, { label: string; color: ChipColor }> = {
  Healthy: { label: "Healthy", color: "success" },
  AttentionNeeded: { label: "Attention needed", color: "warning" },
  HighRisk: { label: "High-risk findings", color: "error" },
  InsufficientAccess: { label: "Insufficient access", color: "default" }
};

/** Rows rendered per finding; exports always carry every row. */
const MAX_ROWS = 100;
/** "Not run" filter key: checks that were unavailable or failed. */
const NOT_RUN = "NotRun";

function worst(check: AuditCheckResult): AuditSeverity {
  return check.findings.reduce<AuditSeverity>(
    (w, f) => (SEVERITY_ORDER.indexOf(f.severity) < SEVERITY_ORDER.indexOf(w) ? f.severity : w), "Pass");
}

function CheckStatusChip({ check }: { check: AuditCheckResult }) {
  if (check.status === "Unavailable") return <Chip size="small" variant="outlined" label="Unavailable" />;
  if (check.status === "Error") return <Chip size="small" color="error" variant="outlined" label="Failed to run" />;
  const s = SEVERITY_META[worst(check)];
  return <Chip size="small" color={s.color} label={s.label} />;
}

// --- Run panel ----------------------------------------------------------------------------

const CUSTOM = "custom";

function ParameterField({ def, value, onChange }: { def: AuditParameterDefinition; value: number; onChange: (v: number) => void }) {
  const preset = def.suggested.includes(value);
  const [custom, setCustom] = useState(!preset);
  const id = `audit-param-${def.key}`;
  return (
    <Stack direction={{ xs: "column", sm: "row" }} spacing={1} sx={{ alignItems: { sm: "center" } }}>
      <FormControl size="small" sx={{ minWidth: 220, maxWidth: "100%" }}>
        <InputLabel id={`${id}-label`}>{def.label}</InputLabel>
        <Select
          labelId={`${id}-label`}
          label={def.label}
          value={custom ? CUSTOM : String(value)}
          onChange={(e) => {
            if (e.target.value === CUSTOM) setCustom(true);
            else { setCustom(false); onChange(Number(e.target.value)); }
          }}
        >
          {def.suggested.map((n) => (
            <MenuItem key={n} value={String(n)}>{n} days{n === def.default ? " (default)" : ""}</MenuItem>
          ))}
          <MenuItem value={CUSTOM}>Custom...</MenuItem>
        </Select>
      </FormControl>
      {custom && (
        <TextField
          size="small"
          type="number"
          label="Days"
          value={Number.isFinite(value) ? value : ""}
          onChange={(e) => onChange(Number(e.target.value))}
          slotProps={{ htmlInput: { min: def.min, max: def.max, "aria-label": `${def.label} (custom days)` } }}
          error={!(value >= def.min && value <= def.max)}
          helperText={`${def.min}-${def.max}`}
          sx={{ width: 120 }}
        />
      )}
    </Stack>
  );
}

function RunPanel({
  catalog, busy, onRun
}: {
  catalog: AuditCatalog;
  busy: boolean;
  onRun: (categories: string[], parameters: Record<string, number>) => void;
}) {
  const available = catalog.categories.filter((c) => catalog.checks.some((x) => x.category === c.id));
  const [selected, setSelected] = useState<string[]>(available.map((c) => c.id));
  const [params, setParams] = useState<Record<string, number>>(
    Object.fromEntries(catalog.parameters.map((p) => [p.key, p.default])));

  const all = selected.length === available.length;
  const toggle = (id: string) => setSelected((s) => (s.includes(id) ? s.filter((x) => x !== id) : [...s, id]));
  const used = new Set(catalog.checks.filter((c) => selected.includes(c.category)).flatMap((c) => c.parameterKeys));
  const visibleParams = catalog.parameters.filter((p) => used.has(p.key));
  const valid = selected.length > 0 && visibleParams.every((p) => params[p.key] >= p.min && params[p.key] <= p.max);
  const countIn = (category: string) => catalog.checks.filter((c) => c.category === category).length;

  return (
    <Card variant="outlined">
      <CardContent>
        <Typography variant="h6" component="h3" gutterBottom>Run a health check</Typography>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 1.5 }}>
          Read-only: PCB reads this tenant's configuration and changes nothing. Checks PCB can't run here are
          listed as unavailable, with the reason.
        </Typography>
        <FormGroup>
          <FormControlLabel
            control={
              <Checkbox
                checked={all}
                indeterminate={!all && selected.length > 0}
                onChange={() => setSelected(all ? [] : available.map((c) => c.id))}
              />
            }
            label={<strong>Full tenant health check</strong>}
          />
          <Box sx={{ display: "grid", gridTemplateColumns: { xs: "1fr", sm: "repeat(2, minmax(0, 1fr))", md: "repeat(3, minmax(0, 1fr))" }, pl: { xs: 2, sm: 3 } }}>
            {available.map((c) => (
              <FormControlLabel
                key={c.id}
                control={<Checkbox checked={selected.includes(c.id)} onChange={() => toggle(c.id)} />}
                label={`${c.label} (${countIn(c.id)})`}
              />
            ))}
          </Box>
        </FormGroup>
        {visibleParams.length > 0 && (
          <Stack spacing={1.5} sx={{ mt: 2 }}>
            {visibleParams.map((p) => (
              <ParameterField key={p.key} def={p} value={params[p.key]} onChange={(v) => setParams((s) => ({ ...s, [p.key]: v }))} />
            ))}
          </Stack>
        )}
        <Stack direction="row" spacing={2} sx={{ mt: 2, alignItems: "center", flexWrap: "wrap" }}>
          <Button
            variant="contained"
            disabled={!valid || busy}
            onClick={() => onRun(all ? [] : selected, Object.fromEntries(visibleParams.map((p) => [p.key, params[p.key]])))}
          >
            {busy ? "Running..." : "Run audit"}
          </Button>
          {busy && (
            <Typography variant="body2" color="text.secondary">
              This can take a minute; Exchange checks start a PowerShell session.
            </Typography>
          )}
        </Stack>
        {busy && <LinearProgress sx={{ mt: 2 }} aria-label="Audit running" />}
      </CardContent>
    </Card>
  );
}

// --- Report -------------------------------------------------------------------------------

function SubjectTable({ finding, tenantId }: { finding: AuditFinding; tenantId: string }) {
  const rows = finding.subjects.slice(0, MAX_ROWS);
  const hasActions = finding.subjects.some((s) => s.remediation);
  const nameCell = (s: AuditSubject) => {
    const label = (
      <>
        {s.name}
        {s.upn && s.upn !== s.name && (
          <Typography variant="caption" color="text.secondary" component="div" sx={{ wordBreak: "break-all" }}>{s.upn}</Typography>
        )}
      </>
    );
    return s.type === "user"
      ? <Link component={RouterLink} to={personPath(tenantId, s.id)} underline="hover">{label}</Link>
      : label;
  };
  const action = (s: AuditSubject) => {
    const r = s.remediation;
    if (!r) return null;
    const to = r.kind === "offboarding" ? offboardPath(tenantId, r.identity ?? r.target)
      : r.kind === "workflow" ? workflowLink(r.target, tenantId, r.identity ?? undefined) : null;
    return to ? <Button component={RouterLink} to={to} size="small">{r.label}</Button> : null;
  };
  return (
    <>
      <TableContainer sx={{ overflowX: "auto", mt: 1 }}>
        <Table size="small" aria-label={`${finding.title} details`}>
          <TableHead>
            <TableRow>
              <TableCell>Name</TableCell>
              {finding.columns.map((c) => <TableCell key={c.key}>{c.label}</TableCell>)}
              {hasActions && <TableCell>Next step</TableCell>}
            </TableRow>
          </TableHead>
          <TableBody>
            {rows.map((s) => (
              <TableRow key={s.id} title={s.evidence ?? undefined}>
                {/* break-word (not anywhere) keeps each column's minimum width readable; the table
                    scrolls inside its container instead of splitting words mid-letter. */}
                <TableCell sx={{ overflowWrap: "break-word", minWidth: 180 }}>{nameCell(s)}</TableCell>
                {finding.columns.map((c) => (
                  <TableCell
                    key={c.key}
                    // Short values (dates, counts, Yes/No) stay on one line; long ones wrap at word breaks.
                    sx={{ overflowWrap: "break-word", minWidth: 96, whiteSpace: (s.properties[c.key]?.length ?? 0) <= 12 ? "nowrap" : undefined }}
                  >
                    {s.properties[c.key] ?? ""}
                  </TableCell>
                ))}
                {hasActions && <TableCell sx={{ whiteSpace: "nowrap" }}>{action(s)}</TableCell>}
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </TableContainer>
      {finding.subjectCount > rows.length && (
        <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>
          Showing {rows.length} of {finding.subjectCount}. Export CSV for the full list.
        </Typography>
      )}
    </>
  );
}

function FindingBlock({ finding, tenantId }: { finding: AuditFinding; tenantId: string }) {
  const s = SEVERITY_META[finding.severity];
  return (
    <Box sx={{ py: 1.5, "&:not(:first-of-type)": { borderTop: 1, borderColor: "divider" } }}>
      <Stack direction="row" spacing={1} sx={{ alignItems: "center", flexWrap: "wrap" }}>
        <Chip size="small" color={s.color} label={s.label} />
        <Typography variant="subtitle2" component="h5">{finding.title}</Typography>
      </Stack>
      <Typography variant="body2" sx={{ mt: 0.5 }}>{finding.summary}</Typography>
      {finding.businessImpact && (
        <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
          <strong>Business impact:</strong> {finding.businessImpact}
        </Typography>
      )}
      {finding.recommendation && (
        <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
          <strong>Recommendation:</strong> {finding.recommendation}
        </Typography>
      )}
      {finding.subjects.length > 0 && <SubjectTable finding={finding} tenantId={tenantId} />}
    </Box>
  );
}

function CheckBlock({ check, tenantId, findings }: { check: AuditCheckResult; tenantId: string; findings: AuditFinding[] }) {
  const notRun = check.status !== "Completed";
  const flagged = notRun || findings.some((f) => f.severity === "Fail" || f.severity === "Warn");
  return (
    <Accordion defaultExpanded={flagged} disableGutters variant="outlined" sx={{ "&::before": { display: "none" } }}>
      <AccordionSummary expandIcon={<ExpandMore />}>
        <Stack direction="row" spacing={1} sx={{ alignItems: "center", flexWrap: "wrap", minWidth: 0 }}>
          <CheckStatusChip check={check} />
          <Typography variant="subtitle1" component="h4" sx={{ fontWeight: 600 }}>{check.checkName}</Typography>
          {!notRun && (
            <Typography variant="body2" color="text.secondary">
              {pluralize(check.findings.filter((f) => f.severity !== "Pass").length, "finding")}
            </Typography>
          )}
        </Stack>
      </AccordionSummary>
      <AccordionDetails>
        {notRun ? (
          <Alert severity={check.status === "Error" ? "error" : "info"} variant="outlined">
            <AlertTitle>{check.status === "Error" ? "This check failed to run" : "PCB can't run this check here"}</AlertTitle>
            {check.statusReason}
            {check.missingRequirements.length > 0 && (
              <Box component="ul" sx={{ m: 0, mt: 1, pl: 2.5 }}>
                {check.missingRequirements.map((m) => <li key={m}>{m}</li>)}
              </Box>
            )}
          </Alert>
        ) : (
          findings.map((f) => <FindingBlock key={f.id} finding={f} tenantId={tenantId} />)
        )}
        {check.notes.length > 0 && (
          <Box sx={{ mt: 1 }}>
            <Typography variant="caption" color="text.secondary" component="div">Scope and notes</Typography>
            <Box component="ul" sx={{ m: 0, pl: 2.5 }}>
              {check.notes.map((n) => (
                <Typography key={n} component="li" variant="caption" color="text.secondary">{n}</Typography>
              ))}
            </Box>
          </Box>
        )}
      </AccordionDetails>
    </Accordion>
  );
}

export function AuditReportView({
  report, catalog, tenantId
}: {
  report: TenantAuditReport;
  catalog: AuditCatalog | null;
  tenantId: string;
}) {
  const toast = useToast();
  // Default view leads with what needs attention; Pass is one click away.
  const [severities, setSeverities] = useState<string[]>(["Fail", "Warn", "Unknown", "Info", NOT_RUN]);
  const [category, setCategory] = useState("all");
  const [exporting, setExporting] = useState<AuditExportFormat | null>(null);
  const s = report.summary;
  const label = (id: string) => catalog?.categories.find((c) => c.id === id)?.label ?? id;

  const visible = useMemo(() => report.checks
    .filter((c) => category === "all" || c.category === category)
    .map((c) => ({
      check: c,
      findings: c.findings.filter((f) => severities.includes(f.severity))
    }))
    .filter(({ check, findings }) => (check.status === "Completed" ? findings.length > 0 : severities.includes(NOT_RUN))),
  [report, severities, category]);

  const categories = [...new Set(report.checks.map((c) => c.category))];
  const toggle = (key: string) => setSeverities((x) => (x.includes(key) ? x.filter((k) => k !== key) : [...x, key]));

  const exportAs = async (format: AuditExportFormat) => {
    setExporting(format);
    try {
      await api.tenantAudits.export(tenantId, report.runId, format);
      toast("Exported");
    } catch (e) {
      toast(`Export failed: ${errorText(e)}`, "error");
    } finally {
      setExporting(null);
    }
  };

  const counts: { key: string; label: string; count: number; color: ChipColor }[] = [
    { key: "Fail", label: "Fail", count: s.fail, color: "error" },
    { key: "Warn", label: "Warn", count: s.warn, color: "warning" },
    { key: "Unknown", label: "Unknown", count: s.unknown, color: "default" },
    { key: "Info", label: "Info", count: s.info, color: "info" },
    { key: "Pass", label: "Pass", count: s.pass, color: "success" },
    { key: NOT_RUN, label: "Not run", count: s.checksUnavailable + s.checksErrored, color: "default" }
  ];

  return (
    <Stack spacing={2}>
      <Card variant="outlined">
        <CardContent>
          <Stack direction={{ xs: "column", md: "row" }} spacing={2} sx={{ justifyContent: "space-between" }}>
            <Box sx={{ minWidth: 0 }}>
              <Stack direction="row" spacing={1} sx={{ alignItems: "center", flexWrap: "wrap" }}>
                <Typography variant="h6" component="h3">{report.auditName}</Typography>
                <Chip size="small" color={HEALTH_META[s.health].color} label={HEALTH_META[s.health].label} />
              </Stack>
              <Typography variant="body2" color="text.secondary" sx={{ overflowWrap: "anywhere" }}>
                {report.tenant.displayName} - <Timestamp value={report.startedAt} /> - by {report.operator}
                {Object.keys(report.parameters).length > 0 && (
                  <> - {Object.entries(report.parameters).map(([k, v]) => {
                    const def = catalog?.parameters.find((p) => p.key === k);
                    return `${def?.label ?? k}: ${v}`;
                  }).join(", ")}</>
                )}
              </Typography>
              <Typography variant="body2" sx={{ mt: 1 }}>
                {s.checksCompleted} of {pluralize(s.checksRequested, "check")} ran
                {s.checksUnavailable + s.checksErrored > 0 && ` (${s.checksUnavailable} unavailable, ${s.checksErrored} failed)`}
                {s.affectedSubjects > 0 && ` - ${pluralize(s.affectedSubjects, "item")} flagged Warn or Fail`}.
              </Typography>
            </Box>
            <Stack direction="row" spacing={1} sx={{ alignItems: "flex-start", flexWrap: "wrap" }}>
              {(["csv", "json", "markdown"] as AuditExportFormat[]).map((f) => (
                <Button key={f} size="small" variant={f === "csv" ? "contained" : "outlined"} startIcon={<Download />}
                  disabled={exporting !== null} onClick={() => void exportAs(f)}>
                  {f === "markdown" ? "Markdown" : f.toUpperCase()}
                </Button>
              ))}
            </Stack>
          </Stack>
          <Stack direction="row" spacing={1} sx={{ mt: 2, flexWrap: "wrap", rowGap: 1 }} role="group" aria-label="Filter by severity">
            {counts.map((c) => (
              <Chip
                key={c.key}
                label={`${c.label} ${c.count}`}
                color={severities.includes(c.key) ? c.color : "default"}
                variant={severities.includes(c.key) ? "filled" : "outlined"}
                onClick={() => toggle(c.key)}
                aria-pressed={severities.includes(c.key)}
              />
            ))}
            <FormControl size="small" sx={{ minWidth: 180 }}>
              <InputLabel id="audit-category-filter">Category</InputLabel>
              <Select labelId="audit-category-filter" label="Category" value={category} onChange={(e) => setCategory(e.target.value)}>
                <MenuItem value="all">All categories</MenuItem>
                {categories.map((c) => <MenuItem key={c} value={c}>{label(c)}</MenuItem>)}
              </Select>
            </FormControl>
          </Stack>
        </CardContent>
      </Card>

      {visible.length === 0 && (
        <Typography variant="body2" color="text.secondary">Nothing matches these filters.</Typography>
      )}
      {categories.filter((c) => visible.some((v) => v.check.category === c)).map((c) => (
        <Box key={c}>
          <Typography variant="overline" component="h3" color="text.secondary">{label(c)}</Typography>
          <Stack spacing={1}>
            {visible.filter((v) => v.check.category === c).map(({ check, findings }) => (
              <CheckBlock key={check.checkId} check={check} findings={findings} tenantId={tenantId} />
            ))}
          </Stack>
        </Box>
      ))}
      <Typography variant="caption" color="text.secondary">
        A read-only health check, not a compliance certification. Results reflect what Microsoft APIs reported
        at the time of the run.
      </Typography>
    </Stack>
  );
}

// --- Screen -------------------------------------------------------------------------------

function runLabel(r: TenantAuditRunSummary) {
  return `${formatTimestamp(r.startedAt).text} - ${r.auditName} - ${HEALTH_META[r.summary.health].label}`;
}

/** Tenant workspace "Audits" tab: run a read-only health check, review results, export, and revisit history. */
export function TenantAudits({ tenant }: { tenant: Tenant }) {
  const toast = useToast();
  const [catalog, setCatalog] = useState<AuditCatalog | null>(null);
  const [runs, setRuns] = useState<TenantAuditRunSummary[] | null>(null);
  const [report, setReport] = useState<TenantAuditReport | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [runError, setRunError] = useState<string | null>(null);
  const [running, setRunning] = useState(false);
  const [opening, setOpening] = useState(false);

  useEffect(() => {
    let alive = true;
    setLoadError(null);
    Promise.all([api.tenantAudits.catalog(), api.tenantAudits.list(tenant.id)])
      .then(async ([c, r]) => {
        if (!alive) return;
        setCatalog(c);
        setRuns(r);
        if (r.length > 0) {
          const latest = await api.tenantAudits.get(tenant.id, r[0].id);
          if (alive) setReport(latest);
        }
      })
      .catch((e) => { if (alive) setLoadError(errorText(e)); });
    return () => { alive = false; };
  }, [tenant.id]);

  const run = async (categories: string[], parameters: Record<string, number>) => {
    setRunning(true);
    setRunError(null);
    try {
      const result = await api.tenantAudits.run(tenant.id, { categories, parameters });
      setReport(result);
      toast("Audit complete");
      setRuns(await api.tenantAudits.list(tenant.id).catch(() => runs));
    } catch (e) {
      setRunError(errorText(e));
    } finally {
      setRunning(false);
    }
  };

  const open = async (runId: string) => {
    setOpening(true);
    try { setReport(await api.tenantAudits.get(tenant.id, runId)); }
    catch (e) { toast(`Couldn't open that run: ${errorText(e)}`, "error"); }
    finally { setOpening(false); }
  };

  if (loadError) return <Alert severity="error">{loadError}</Alert>;
  if (!catalog || runs === null) return <Skeleton variant="rounded" height={200} aria-busy="true" />;

  return (
    <Stack spacing={2}>
      <RunPanel catalog={catalog} busy={running} onRun={(c, p) => void run(c, p)} />
      {runError && <Alert severity="error">{runError}</Alert>}
      {runs.length > 0 && (
        <FormControl size="small" sx={{ maxWidth: 520 }}>
          <InputLabel id="audit-run-label">Audit run</InputLabel>
          <Select
            labelId="audit-run-label"
            label="Audit run"
            value={report?.runId ?? ""}
            disabled={opening || running}
            onChange={(e) => void open(e.target.value)}
          >
            {runs.map((r) => <MenuItem key={r.id} value={r.id}>{runLabel(r)}</MenuItem>)}
          </Select>
        </FormControl>
      )}
      {report ? (
        <AuditReportView report={report} catalog={catalog} tenantId={tenant.id} />
      ) : (
        <Typography variant="body2" color="text.secondary">No audits have been run for this tenant yet.</Typography>
      )}
    </Stack>
  );
}
