import { useEffect, useState } from "react";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
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

  useEffect(() => {
    Promise.all([api.deployments.list(), api.tenants.list(), api.templates.list()])
      .then(([d, tn, tpl]) => { setDeployments(d); setTenants(tn); setTemplates(tpl); })
      .catch((e) => setError(e instanceof Error ? e.message : String(e)));
  }, []);

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
              </TableRow>
            </TableHead>
            <TableBody>
              {deployments.map((d) => {
                const templateName = name(d.appTemplateId, templates);
                const tenantName = name(d.tenantId, tenants);
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
                    <TableCell>
                      <Chip size="small" label={d.status} color={statusColor(d.status)} />
                    </TableCell>
                    <TableCell>{d.lastSyncedAt ? new Date(d.lastSyncedAt).toLocaleString() : "-"}</TableCell>
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
