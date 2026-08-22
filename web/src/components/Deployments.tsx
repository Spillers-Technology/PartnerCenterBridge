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

/**
 * Each row owns its own useAsyncAction instance so two rows can be retried independently -- a
 * single shared instance across the whole table would silently no-op a second row's confirmed
 * retry while the first row's deploy call was still in flight (useAsyncAction is single-flight by
 * design; the caller gets undefined back with no error, no toast, nothing).
 */
function DeploymentRow({
  d, templateName, tenantName, canRetry, needsAction, onRetried
}: {
  d: Deployment; templateName: string; tenantName: string; canRetry: boolean; needsAction: boolean;
  onRetried: () => Promise<void>;
}) {
  const confirm = useConfirm();
  const toast = useToast();
  // The refresh (onRetried) runs inside this action's own closure, not after retryAction.run()
  // resolves in the caller -- useAsyncAction's busy flag only covers the function passed to it,
  // so awaiting the refresh from outside would let busy (and the row's disabled state) clear as
  // soon as the deploy call itself returned, before the refreshed list had actually landed.
  const retryAction = useAsyncAction(async () => {
    const results = await api.deployments.deploy(d.appTemplateId, [d.tenantId]);
    const result = results[0];
    if (result?.status === "Succeeded") {
      toast(`${templateName} redeployed to ${tenantName}.`, "success");
    } else {
      toast(`Redeploy to ${tenantName} did not succeed${result?.lastError ? ` -- ${result.lastError}` : ""}.`, "warning");
    }
    await onRetried();
    return results;
  });

  const retry = async () => {
    const verb = d.status === "Failed" ? "Retry" : "Update";
    const ok = await confirm({
      title: `${verb} this deployment?`,
      message: `Re-deploy "${templateName}" to ${tenantName}?`,
      confirmLabel: verb,
      destructive: true
    });
    if (!ok) return;
    void retryAction.run();
  };

  return (
    <TableRow>
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
        {retryAction.error && (
          <Typography variant="body2" color="error" sx={{ mt: 0.5, wordBreak: "break-word" }}>
            {retryAction.error}
          </Typography>
        )}
      </TableCell>
      <TableCell>{d.lastSyncedAt ? new Date(d.lastSyncedAt).toLocaleString() : "-"}</TableCell>
      <TableCell>
        {needsAction && (
          <Button size="small" variant="outlined" disabled={!canRetry || retryAction.busy} onClick={() => void retry()}>
            {retryAction.busy ? "Working..." : d.status === "Failed" ? "Retry" : "Update"}
          </Button>
        )}
      </TableCell>
    </TableRow>
  );
}

export function Deployments() {
  const [deployments, setDeployments] = useState<Deployment[] | null>(null);
  const [tenants, setTenants] = useState<Tenant[] | null>(null);
  const [templates, setTemplates] = useState<AppTemplate[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const toast = useToast();

  const load = () =>
    Promise.all([api.deployments.list(), api.tenants.list(), api.templates.list()])
      .then(([d, tn, tpl]) => { setDeployments(d); setTenants(tn); setTemplates(tpl); setError(null); })
      .catch((e) => {
        const message = e instanceof Error ? e.message : String(e);
        if (deployments === null) {
          // Nothing has ever loaded -- there's nothing else useful to show in place of the error.
          setError(message);
        } else {
          // A refresh (e.g. after a retry) failed after real data was already on screen -- don't
          // replace the whole table with an error banner over it; the table itself already has
          // its own retry-error display per row, and the data still on screen is still usable.
          toast(`Couldn't refresh the deployment list: ${message}`, "warning");
        }
      });

  useEffect(() => { void load(); }, []);

  const name = (id: string, list: { id: string; displayName: string }[]) =>
    list.find((x) => x.id === id)?.displayName ?? id;

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
                const needsAction = d.status === "Failed" || d.status === "UpdateAvailable";
                const template = templates.find((t) => t.id === d.appTemplateId);
                return (
                  <DeploymentRow
                    key={d.id}
                    d={d}
                    templateName={name(d.appTemplateId, templates)}
                    tenantName={name(d.tenantId, tenants)}
                    needsAction={needsAction}
                    canRetry={needsAction && Boolean(template?.hasPackage)}
                    onRetried={load}
                  />
                );
              })}
            </TableBody>
          </Table>
        </TableContainer>
      )}
    </Box>
  );
}
