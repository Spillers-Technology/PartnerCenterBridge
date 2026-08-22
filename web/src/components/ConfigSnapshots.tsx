import { useEffect, useRef, useState } from "react";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Card from "@mui/material/Card";
import CardContent from "@mui/material/CardContent";
import Chip from "@mui/material/Chip";
import List from "@mui/material/List";
import ListItem from "@mui/material/ListItem";
import MenuItem from "@mui/material/MenuItem";
import Skeleton from "@mui/material/Skeleton";
import Table from "@mui/material/Table";
import TableBody from "@mui/material/TableBody";
import TableCell from "@mui/material/TableCell";
import TableContainer from "@mui/material/TableContainer";
import TableHead from "@mui/material/TableHead";
import TableRow from "@mui/material/TableRow";
import TextField from "@mui/material/TextField";
import Typography from "@mui/material/Typography";
import { visuallyHidden } from "@mui/utils";
import { api } from "../api";
import { useAsyncAction } from "../hooks/useAsyncAction";
import { useToast } from "../hooks/useToast";
import type { ConfigChangeKind, ConfigSnapshotRun, MeProfile, SectionDiff, Tenant } from "../types";

function changeChip(kind: ConfigChangeKind) {
  const color = kind === "Added" ? "success" : kind === "Removed" ? "error" : "warning";
  return <Chip size="small" label={kind} color={color} />;
}

function runLabel(r: ConfigSnapshotRun) {
  return `${new Date(r.startedAt).toLocaleString()} -- ${r.operator}${r.imported ? " (imported)" : ""}`;
}

type LastAction = "runs" | "capture" | "viewDiff" | "exportPatch" | "exportRun" | "import" | null;

export function ConfigSnapshots({ me }: { me: MeProfile | null }) {
  const [tenantId, setTenantId] = useState("");
  const [beforeRunId, setBeforeRunId] = useState("");
  const [afterRunId, setAfterRunId] = useState("");
  const [diffs, setDiffs] = useState<SectionDiff[] | null>(null);
  const [importFile, setImportFile] = useState<File | null>(null);
  const [exportingRunId, setExportingRunId] = useState<string | null>(null);
  // Tracks which async action's error should be shown -- six independent useAsyncAction instances
  // each keep their own error state, and none of them clear a sibling's, so without this a stale
  // error from an earlier, unrelated action would keep showing after a later action succeeds
  // (mirrors Login.tsx's lastAttempt pattern for the same reason).
  const [lastAction, setLastAction] = useState<LastAction>(null);
  const toast = useToast();

  // Always current (updated every render, not just in a change handler) so capture/import below
  // can tell, after their own async work finishes, whether the tenant they were issued for is
  // still the one on screen.
  const currentTenantRef = useRef("");
  currentTenantRef.current = tenantId;

  const tenantsAction = useAsyncAction(() => api.tenants.list());
  const runsAction = useAsyncAction((id: string) => api.configSnapshots.list(id));
  const captureAction = useAsyncAction(async () => {
    const capturedFor = tenantId;
    await api.configSnapshots.capture(capturedFor);
    // The capture itself already succeeded at this point -- a failed (or single-flight-dropped)
    // refresh afterward must not make the whole action look like it failed, but it also must not
    // be reported as a plain, unqualified success (see handleCapture below).
    let refreshed = (await runsAction.run(capturedFor)) !== undefined;
    if (currentTenantRef.current !== capturedFor) {
      // The user switched tenants while this capture (or its own refresh) was still in flight --
      // whatever runsAction.run just fetched (if it fetched at all) belongs to capturedFor, not
      // whichever tenant is now selected. Re-fetch for the tenant actually on screen instead of
      // leaving that tenant's UI showing a different tenant's runs.
      void runsAction.run(currentTenantRef.current);
      refreshed = false;
    }
    return { refreshed };
  });
  const viewDiffAction = useAsyncAction((before: string, after: string) => api.configSnapshots.diff(tenantId, before, after));
  const exportPatchAction = useAsyncAction(async () => {
    await api.configSnapshots.exportDiff(tenantId, beforeRunId, afterRunId);
    return true;
  });
  const exportRunAction = useAsyncAction(async (runId: string) => {
    await api.configSnapshots.exportRun(tenantId, runId);
    return true;
  });
  const importAction = useAsyncAction(async () => {
    if (!importFile) return null;
    const capturedFor = tenantId;
    const workbook = JSON.parse(await importFile.text());
    await api.configSnapshots.import(capturedFor, workbook.sections);
    setImportFile(null);
    let refreshed = (await runsAction.run(capturedFor)) !== undefined;
    if (currentTenantRef.current !== capturedFor) {
      void runsAction.run(currentTenantRef.current);
      refreshed = false;
    }
    return { refreshed };
  });

  useEffect(() => {
    void tenantsAction.run();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const tenants = tenantsAction.result;

  useEffect(() => {
    if (tenants && tenants.length > 0 && !tenantId) {
      setTenantId(tenants[0].id);
    }
  }, [tenants, tenantId]);

  // Tracks the tenant we've actually issued a runs request for. A rapid tenant switch (A -> B
  // before A's request finishes) used to silently drop B's fetch entirely -- useAsyncAction.run()
  // is single-flight and no-ops while busy, so B's runs list never loaded and A's response could
  // still land and render under B's now-selected tenant. Re-checking once the in-flight request
  // settles (runsAction.busy is a dependency below) makes the fetch always converge on whichever
  // tenant is selected once the previous request is done, never mid-flight.
  const requestedTenantRef = useRef("");

  useEffect(() => {
    if (!tenantId || runsAction.busy || requestedTenantRef.current === tenantId) return;
    requestedTenantRef.current = tenantId;
    setLastAction("runs");
    void runsAction.run(tenantId);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tenantId, runsAction.busy]);

  const canOperate = me === null || me.tenantAccess.some((a) => a.tenantId === tenantId && a.role !== "Viewer");

  const operationError =
    lastAction === "runs" ? runsAction.error :
    lastAction === "capture" ? captureAction.error :
    lastAction === "viewDiff" ? viewDiffAction.error :
    lastAction === "exportPatch" ? exportPatchAction.error :
    lastAction === "exportRun" ? exportRunAction.error :
    lastAction === "import" ? importAction.error :
    null;

  const handleCapture = async () => {
    setLastAction("capture");
    const outcome = await captureAction.run();
    if (!outcome) return;
    if (outcome.refreshed) toast("Snapshot captured");
    else toast("Snapshot captured, but the list couldn't refresh -- reload to see it.", "warning");
  };

  const handleViewDiff = async () => {
    if (!beforeRunId || !afterRunId) return;
    setLastAction("viewDiff");
    const result = await viewDiffAction.run(beforeRunId, afterRunId);
    if (result) setDiffs(result);
  };

  const handleExportPatch = async () => {
    if (!beforeRunId || !afterRunId) return;
    setLastAction("exportPatch");
    const ok = await exportPatchAction.run();
    if (ok) toast("Exported");
  };

  const handleExportRun = async (runId: string) => {
    setLastAction("exportRun");
    setExportingRunId(runId);
    const ok = await exportRunAction.run(runId);
    setExportingRunId(null);
    if (ok) toast("Exported");
  };

  const handleImport = async () => {
    setLastAction("import");
    const outcome = await importAction.run();
    if (!outcome) return;
    if (outcome.refreshed) toast("Workbook imported");
    else toast("Workbook imported, but the list couldn't refresh -- reload to see it.", "warning");
  };

  if (tenantsAction.status === "error") {
    return (
      <Box>
        <Typography variant="h5" component="h2" gutterBottom>
          Config Snapshots
        </Typography>
        <Alert severity="error">{tenantsAction.error}</Alert>
      </Box>
    );
  }

  if (tenants === null) {
    return (
      <Box aria-busy="true">
        <Typography variant="h5" component="h2" gutterBottom>
          Config Snapshots
        </Typography>
        <Box component="span" sx={visuallyHidden}>
          Loading config snapshots...
        </Box>
        <Skeleton variant="rounded" height={40} sx={{ mb: 2 }} />
        <Skeleton variant="rounded" height={200} />
      </Box>
    );
  }

  const runs = runsAction.result ?? [];

  return (
    <Box>
      <Box sx={{ display: "flex", flexWrap: "wrap", alignItems: "center", gap: 2, mb: 2 }}>
        <Typography variant="h5" component="h2" sx={{ mr: "auto" }}>
          Config Snapshots
        </Typography>
        <TextField
          select
          label="Tenant"
          size="small"
          value={tenantId}
          onChange={(e) => {
            setTenantId(e.target.value);
            // These are scoped to the previously selected tenant's runs -- leaving them set would
            // let a diff/export/comparison request pair a stale run ID from the old tenant with
            // the newly selected tenant, or keep a since-invalid run's export button enabled.
            setBeforeRunId("");
            setAfterRunId("");
            setDiffs(null);
            setExportingRunId(null);
          }}
          sx={{ minWidth: 220 }}
        >
          {tenants.map((t: Tenant) => (
            <MenuItem key={t.id} value={t.id}>
              {t.displayName}
            </MenuItem>
          ))}
        </TextField>
        {canOperate && (
          <Button variant="contained" onClick={() => void handleCapture()} disabled={captureAction.busy || !tenantId}>
            {captureAction.busy ? "Working..." : "Take Snapshot"}
          </Button>
        )}
      </Box>

      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
        Point-in-time backups of this tenant's configuration (Conditional Access, Named Locations,
        Device Compliance Policies), diffable against each other. There is no "apply" button --
        making changes stays the job of the Deploy wizard and known-fix Workflows, where every
        write is a single reviewed action.
      </Typography>

      {operationError && (
        <Alert severity="error" sx={{ mb: 2 }}>
          {operationError}
        </Alert>
      )}

      <TableContainer sx={{ mb: 3, overflowX: "auto" }}>
        <Table size="small">
          <TableHead>
            <TableRow>
              <TableCell>Captured</TableCell>
              <TableCell>Sections</TableCell>
              <TableCell>Status</TableCell>
              <TableCell>Git</TableCell>
              <TableCell></TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {runs.map((r) => (
              <TableRow key={r.id}>
                <TableCell>{runLabel(r)}</TableCell>
                <TableCell>{r.sections.map((s) => `${s.sectionName} (${s.failed ? "failed" : s.itemCount})`).join(", ")}</TableCell>
                <TableCell>
                  <Chip size="small" label={r.succeeded ? "ok" : "partial failure"} color={r.succeeded ? "success" : "error"} />
                </TableCell>
                <TableCell sx={{ fontFamily: "monospace" }}>{r.gitCommitSha ? r.gitCommitSha.slice(0, 8) : "--"}</TableCell>
                <TableCell>
                  <Button size="small" onClick={() => void handleExportRun(r.id)} disabled={exportingRunId === r.id}>
                    Export
                  </Button>
                </TableCell>
              </TableRow>
            ))}
            {runs.length === 0 && (
              <TableRow>
                <TableCell colSpan={5}>
                  <Typography variant="body2" color="text.secondary">
                    No snapshots yet for this tenant.
                  </Typography>
                </TableCell>
              </TableRow>
            )}
          </TableBody>
        </Table>
      </TableContainer>

      <Card variant="outlined" sx={{ mb: 3 }}>
        <CardContent>
          <Typography variant="subtitle1" gutterBottom>
            Diff two snapshots
          </Typography>
          <Box sx={{ display: "flex", flexWrap: "wrap", alignItems: "center", gap: 2, mb: diffs ? 2 : 0 }}>
            <TextField select label="Before" size="small" value={beforeRunId} onChange={(e) => setBeforeRunId(e.target.value)} sx={{ minWidth: 220 }}>
              <MenuItem value="">before...</MenuItem>
              {runs.map((r) => (
                <MenuItem key={r.id} value={r.id}>
                  {runLabel(r)}
                </MenuItem>
              ))}
            </TextField>
            <TextField select label="After" size="small" value={afterRunId} onChange={(e) => setAfterRunId(e.target.value)} sx={{ minWidth: 220 }}>
              <MenuItem value="">after...</MenuItem>
              {runs.map((r) => (
                <MenuItem key={r.id} value={r.id}>
                  {runLabel(r)}
                </MenuItem>
              ))}
            </TextField>
            <Button onClick={() => void handleViewDiff()} disabled={viewDiffAction.busy || !beforeRunId || !afterRunId}>
              View diff
            </Button>
            <Button onClick={() => void handleExportPatch()} disabled={exportPatchAction.busy || !beforeRunId || !afterRunId}>
              Export as patch
            </Button>
          </Box>

          {diffs && (
            <Box sx={{ overflowX: "auto" }}>
              {diffs.every((d) => d.changes.length === 0) && (
                <Typography variant="body2" color="text.secondary">
                  No changes between these two snapshots.
                </Typography>
              )}
              {diffs
                .filter((d) => d.changes.length > 0)
                .map((d) => (
                  <Box key={d.sectionId} sx={{ mb: 2 }}>
                    <Typography variant="subtitle2" gutterBottom>
                      {d.sectionName}
                    </Typography>
                    <List dense disablePadding>
                      {d.changes.map((c) => (
                        <ListItem key={c.itemId} disableGutters sx={{ display: "block" }}>
                          <Box sx={{ display: "flex", alignItems: "center", gap: 1, flexWrap: "wrap" }}>
                            {changeChip(c.kind)}
                            <Typography component="span" sx={{ wordBreak: "break-word" }}>
                              {c.label ?? c.itemId}
                            </Typography>
                          </Box>
                          {c.fieldChanges.length > 0 && (
                            <List dense disablePadding sx={{ pl: 3 }}>
                              {c.fieldChanges.map((f) => (
                                <ListItem key={f.field} disableGutters>
                                  <Typography
                                    component="span"
                                    variant="body2"
                                    sx={{ fontFamily: "monospace", wordBreak: "break-word" }}
                                  >
                                    {f.field}: {f.before ?? "(none)"} -&gt; {f.after ?? "(none)"}
                                  </Typography>
                                </ListItem>
                              ))}
                            </List>
                          )}
                        </ListItem>
                      ))}
                    </List>
                  </Box>
                ))}
            </Box>
          )}
        </CardContent>
      </Card>

      {canOperate && (
        <Card variant="outlined">
          <CardContent>
            <Typography variant="subtitle1" gutterBottom>
              Import a workbook
            </Typography>
            <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
              Bring in a snapshot exported from elsewhere for comparison. Never writes to the tenant.
            </Typography>
            <Box sx={{ display: "flex", flexWrap: "wrap", alignItems: "center", gap: 2 }}>
              <Box>
                <Typography component="label" htmlFor="config-snapshot-import-file" variant="body2" sx={{ display: "block", mb: 0.5 }}>
                  Workbook file
                </Typography>
                <input
                  id="config-snapshot-import-file"
                  type="file"
                  accept="application/json"
                  onChange={(e) => setImportFile(e.target.files?.[0] ?? null)}
                />
              </Box>
              <Button onClick={() => void handleImport()} disabled={importAction.busy || !importFile}>
                Import
              </Button>
            </Box>
          </CardContent>
        </Card>
      )}
    </Box>
  );
}
