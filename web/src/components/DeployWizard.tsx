import { useEffect, useState } from "react";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Checkbox from "@mui/material/Checkbox";
import Chip from "@mui/material/Chip";
import FormControl from "@mui/material/FormControl";
import FormControlLabel from "@mui/material/FormControlLabel";
import FormGroup from "@mui/material/FormGroup";
import FormLabel from "@mui/material/FormLabel";
import MenuItem from "@mui/material/MenuItem";
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
import { visuallyHidden } from "@mui/utils";
import { api } from "../api";
import { humanizeEnum, pluralize } from "../format";
import { useAsyncAction } from "../hooks/useAsyncAction";
import { useConfirm } from "../hooks/useConfirm";
import { useToast } from "../hooks/useToast";
import type { AppTemplate, Deployment, DeploymentStatus, Tenant } from "../types";

function statusColor(status: DeploymentStatus): "default" | "success" | "error" | "warning" {
  if (status === "Succeeded") return "success";
  if (status === "Failed") return "error";
  if (status === "UpdateAvailable") return "warning";
  return "default";
}

/** "installed v3" / "update available" / "failed" -- a quick read on where a tenant already
 * stands for the currently-chosen template, so picking targets doesn't require a trip to History.
 * Returns null when there's simply nothing deployed there yet (nothing to report). */
function existingDeployHint(d: Deployment | undefined): string | null {
  if (!d) return null;
  if (d.status === "Succeeded") return `installed v${d.deployedTemplateVersion}`;
  return humanizeEnum(d.status).toLowerCase();
}

export function DeployWizard() {
  const [templates, setTemplates] = useState<AppTemplate[] | null>(null);
  const [tenants, setTenants] = useState<Tenant[] | null>(null);
  const [deployments, setDeployments] = useState<Deployment[]>([]);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [templateId, setTemplateId] = useState("");
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const confirm = useConfirm();
  const showToast = useToast();

  useEffect(() => {
    Promise.all([api.templates.list(), api.tenants.list(), api.deployments.list()])
      .then(([tpl, tn, dep]) => { setTemplates(tpl); setTenants(tn); setDeployments(dep); })
      .catch((e) => setLoadError(e instanceof Error ? e.message : String(e)));
  }, []);

  const deployAction = useAsyncAction(() => api.deployments.deploy(templateId, [...selected]));

  const toggle = (id: string) => {
    const next = new Set(selected);
    next.has(id) ? next.delete(id) : next.add(id);
    setSelected(next);
  };

  const chosen = templates?.find((t) => t.id === templateId);
  const isRedeploy = Boolean(chosen && deployments.some((d) => d.appTemplateId === chosen.id));

  const deploy = async () => {
    if (!chosen?.hasPackage || selected.size === 0) return;
    const ok = await confirm({
      title: "Deploy template?",
      message: `Deploy "${chosen.displayName}" to ${pluralize(selected.size, "tenant")}?`,
      confirmLabel: "Deploy",
      destructive: true
    });
    if (!ok) return;

    const results = await deployAction.run();
    if (!results) return;
    void api.deployments.list().then(setDeployments).catch(() => {});

    const failed = results.filter((r) => r.status === "Failed").length;
    if (failed === 0) {
      showToast(`Deployed to ${pluralize(results.length, "tenant")}.`, "success");
    } else {
      showToast(`Deployed to ${results.length - failed} of ${pluralize(results.length, "tenant")} - ${pluralize(failed, "failure")}.`, "warning");
    }
  };

  if (loadError) {
    return (
      <Box>
        <Typography variant="h5" component="h2" gutterBottom>
          Deploy a template
        </Typography>
        <Alert severity="error">{loadError}</Alert>
      </Box>
    );
  }

  if (!templates || !tenants) {
    return (
      <Box aria-busy="true">
        <Typography variant="h5" component="h2" gutterBottom>
          Deploy a template
        </Typography>
        <Box component="span" sx={visuallyHidden}>Loading deploy wizard...</Box>
        <Skeleton variant="rounded" height={200} />
      </Box>
    );
  }

  return (
    <Box>
      <Typography variant="h5" component="h2" gutterBottom>
        Deploy a template
      </Typography>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 3, maxWidth: 640 }}>
        First deploy creates the Intune app and assigns it to the tenant. Redeploying an existing
        app pushes the new package together with the template's current name, publisher,
        install/uninstall commands and detection rules, and does not change the app's assignments
        — any group changes made directly in the Intune portal are kept.
      </Typography>

      <Stack spacing={3} sx={{ maxWidth: 480 }}>
        <TextField
          select
          label="Template"
          value={templateId}
          onChange={(e) => setTemplateId(e.target.value)}
        >
          <MenuItem value="">
            <em>Choose a template</em>
          </MenuItem>
          {templates.map((t) => (
            <MenuItem key={t.id} value={t.id} disabled={!t.hasPackage}>
              {t.displayName} v{t.contentVersion}{t.hasPackage ? "" : " (no package)"}
            </MenuItem>
          ))}
        </TextField>

        <FormControl component="fieldset" variant="standard">
          <Stack direction="row" sx={{ alignItems: "center", justifyContent: "space-between" }}>
            <FormLabel component="legend">Target tenants</FormLabel>
            {tenants.length > 0 && (
              <Stack direction="row" spacing={1}>
                <Button size="small" onClick={() => setSelected(new Set(tenants.map((t) => t.id)))} disabled={selected.size === tenants.length}>
                  Select all
                </Button>
                <Button size="small" onClick={() => setSelected(new Set())} disabled={selected.size === 0}>
                  Clear
                </Button>
              </Stack>
            )}
          </Stack>
          {tenants.length === 0 ? (
            <Typography variant="body2" color="text.secondary">
              No tenants. Sync first.
            </Typography>
          ) : (
            <FormGroup>
              {tenants.map((t) => {
                const hint = chosen ? existingDeployHint(deployments.find((d) => d.appTemplateId === chosen.id && d.tenantId === t.id)) : null;
                return (
                  <Stack key={t.id} direction="row" spacing={1} sx={{ alignItems: "center" }}>
                    <FormControlLabel
                      control={<Checkbox checked={selected.has(t.id)} onChange={() => toggle(t.id)} />}
                      label={t.displayName}
                    />
                    {hint && <Chip size="small" variant="outlined" label={hint} />}
                  </Stack>
                );
              })}
            </FormGroup>
          )}
        </FormControl>

        <Button
          variant="contained"
          onClick={() => void deploy()}
          disabled={deployAction.busy || !chosen?.hasPackage || selected.size === 0}
          sx={{ alignSelf: "flex-start" }}
        >
          {deployAction.busy
            ? "Deploying..."
            : selected.size === 0
            ? (isRedeploy ? "Select tenants to redeploy to" : "Select tenants to deploy to")
            : `${isRedeploy ? "Redeploy" : "Deploy"} to ${pluralize(selected.size, "tenant")}`}
        </Button>

        {deployAction.error && <Alert severity="error">{deployAction.error}</Alert>}
      </Stack>

      {deployAction.result && (
        <TableContainer sx={{ mt: 3, overflowX: "auto" }}>
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>Tenant</TableCell>
                <TableCell>Status</TableCell>
                <TableCell>Intune app id</TableCell>
                <TableCell>Error</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {deployAction.result.map((r) => {
                const t = tenants.find((x) => x.id === r.tenantId);
                return (
                  <TableRow key={r.id}>
                    <TableCell>{t?.displayName ?? r.tenantId}</TableCell>
                    <TableCell>
                      <Chip size="small" label={humanizeEnum(r.status)} color={statusColor(r.status)} />
                    </TableCell>
                    <TableCell>
                      <Typography variant="body2" sx={{ fontFamily: "monospace" }}>
                        {r.intuneAppId ?? "-"}
                      </Typography>
                    </TableCell>
                    <TableCell>{r.lastError ?? ""}</TableCell>
                  </TableRow>
                );
              })}
            </TableBody>
          </Table>
        </TableContainer>
      )}
    </Box>
  );
}
