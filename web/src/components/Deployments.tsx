import { useEffect, useState } from "react";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Chip from "@mui/material/Chip";
import Skeleton from "@mui/material/Skeleton";
import Table from "@mui/material/Table";
import TableBody from "@mui/material/TableBody";
import TableCell from "@mui/material/TableCell";
import TableContainer from "@mui/material/TableContainer";
import TableHead from "@mui/material/TableHead";
import TableRow from "@mui/material/TableRow";
import Tooltip from "@mui/material/Tooltip";
import Typography from "@mui/material/Typography";
import { visuallyHidden } from "@mui/utils";
import { api } from "../api";
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

export function Deployments() {
  const [deployments, setDeployments] = useState<Deployment[] | null>(null);
  const [tenants, setTenants] = useState<Tenant[] | null>(null);
  const [templates, setTemplates] = useState<AppTemplate[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const confirm = useConfirm();
  const toast = useToast();

  const load = () =>
    Promise.all([api.deployments.list(), api.tenants.list(), api.templates.list()])
      .then(([d, tn, tpl]) => { setDeployments(d); setTenants(tn); setTemplates(tpl); })
      .catch((e) => setError(e instanceof Error ? e.message : String(e)));

  useEffect(() => { void load(); }, []);

  const name = (id: string, list: { id: string; displayName: string }[]) =>
    list.find((x) => x.id === id)?.displayName ?? id;

  // A failed or update-available deployment previously had no path forward from this screen
  // beyond manually reselecting the same template+tenant in the Deploy wizard -- this re-runs
  // exactly what the wizard would (the same underlying deploy call, one tenant), directly from
  // the row it applies to.
  const retryAction = useAsyncAction((templateId: string, tenantId: string) => api.deployments.deploy(templateId, [tenantId]));
  const [retryingId, setRetryingId] = useState<string | null>(null);

  const retry = async (d: Deployment) => {
    const templateName = name(d.appTemplateId, templates ?? []);
    const tenantName = name(d.tenantId, tenants ?? []);
    const verb = d.status === "Failed" ? "Retry" : "Update";
    const ok = await confirm({
      title: `${verb} this deployment?`,
      message: `Re-deploy "${templateName}" to ${tenantName}?`,
      confirmLabel: verb,
      destructive: true
    });
    if (!ok) return;
    setRetryingId(d.id);
    const results = await retryAction.run(d.appTemplateId, d.tenantId);
    setRetryingId(null);
    if (!results) return;
    const result = results[0];
    if (result?.status === "Succeeded") {
      toast(`${templateName} redeployed to ${tenantName}.`, "success");
    } else {
      toast(`Redeploy to ${tenantName} did not succeed${result?.lastError ? ` -- ${result.lastError}` : ""}.`, "warning");
    }
    void load();
  };

  if (error) {
    return (
      <Box>
        <Typography variant="h5" component="h2" gutterBottom>
          Deployment history
        </Typography>
        <Alert severity="error">{error}</Alert>
      </Box>
    );
  }

  if (!deployments || !tenants || !templates) {
    return (
      <Box aria-busy="true">
        <Typography variant="h5" component="h2" gutterBottom>
          Deployment history
        </Typography>
        <Box component="span" sx={visuallyHidden}>Loading deployment history...</Box>
        <Skeleton variant="rounded" height={200} />
      </Box>
    );
  }

  return (
    <Box>
      <Typography variant="h5" component="h2" gutterBottom>
        Deployment history
      </Typography>

      {retryAction.error && <Alert severity="error" sx={{ mb: 2 }}>{retryAction.error}</Alert>}

      {deployments.length === 0 ? (
        <Typography variant="body2" color="text.secondary">
          No deployments yet.
        </Typography>
      ) : (
        <TableContainer sx={{ overflowX: "auto" }}>
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>Template</TableCell>
                <TableCell>Tenant</TableCell>
                <TableCell>Version</TableCell>
                <TableCell>Status</TableCell>
                <TableCell>Last synced</TableCell>
                <TableCell></TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {deployments.map((d) => {
                const templateName = name(d.appTemplateId, templates);
                const tenantName = name(d.tenantId, tenants);
                const needsAction = d.status === "Failed" || d.status === "UpdateAvailable";
                const template = templates.find((t) => t.id === d.appTemplateId);
                const canRetry = needsAction && Boolean(template?.hasPackage);
                return (
                  <TableRow key={d.id}>
                    <TableCell sx={{ maxWidth: 160 }}>
                      <Tooltip title={templateName}>
                        <Typography variant="body2" noWrap>
                          {templateName}
                        </Typography>
                      </Tooltip>
                    </TableCell>
                    <TableCell sx={{ maxWidth: 160 }}>
                      <Tooltip title={tenantName}>
                        <Typography variant="body2" noWrap>
                          {tenantName}
                        </Typography>
                      </Tooltip>
                    </TableCell>
                    <TableCell>v{d.deployedTemplateVersion}</TableCell>
                    <TableCell sx={{ maxWidth: 240 }}>
                      <Chip size="small" label={d.status} color={statusColor(d.status)} />
                      {d.status === "Failed" && d.lastError && (
                        <Typography variant="body2" color="error" sx={{ mt: 0.5, wordBreak: "break-word" }}>
                          {d.lastError}
                        </Typography>
                      )}
                    </TableCell>
                    <TableCell>{d.lastSyncedAt ? new Date(d.lastSyncedAt).toLocaleString() : "-"}</TableCell>
                    <TableCell>
                      {needsAction && (
                        <Button
                          size="small"
                          variant="outlined"
                          disabled={!canRetry || retryingId === d.id}
                          onClick={() => void retry(d)}
                        >
                          {retryingId === d.id ? "Working..." : d.status === "Failed" ? "Retry" : "Update"}
                        </Button>
                      )}
                    </TableCell>
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
