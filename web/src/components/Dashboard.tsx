import { useEffect, useState } from "react";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Card from "@mui/material/Card";
import CardContent from "@mui/material/CardContent";
import Chip from "@mui/material/Chip";
import Skeleton from "@mui/material/Skeleton";
import Table from "@mui/material/Table";
import TableBody from "@mui/material/TableBody";
import TableCell from "@mui/material/TableCell";
import TableContainer from "@mui/material/TableContainer";
import TableHead from "@mui/material/TableHead";
import TableRow from "@mui/material/TableRow";
import Typography from "@mui/material/Typography";
import { visuallyHidden } from "@mui/utils";
import { api } from "../api";
import type { Dashboard as DashboardData } from "../types";

type Tone = "default" | "success" | "warning" | "error";

function Stat({ label, value, tone = "default" }: { label: string; value: number; tone?: Tone }) {
  return (
    <Card variant="outlined" sx={{ minWidth: 140, flex: "1 1 140px" }}>
      <CardContent>
        <Typography variant="h4" color={tone === "default" ? "text.primary" : `${tone}.main`}>
          {value}
        </Typography>
        <Typography variant="body2" color="text.secondary">
          {label}
        </Typography>
      </CardContent>
    </Card>
  );
}

export function Dashboard() {
  const [data, setData] = useState<DashboardData | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api.dashboard().then(setData).catch((e) => setError(e instanceof Error ? e.message : String(e)));
  }, []);

  if (error) {
    return (
      <Box>
        <Typography variant="h5" component="h2" gutterBottom>
          Dashboard
        </Typography>
        <Alert severity="error">{error}</Alert>
      </Box>
    );
  }

  if (!data) {
    return (
      <Box aria-busy="true">
        <Typography variant="h5" component="h2" gutterBottom>
          Dashboard
        </Typography>
        <Box component="span" sx={visuallyHidden}>Loading dashboard...</Box>
        <Skeleton variant="rounded" height={96} sx={{ mb: 2 }} />
        <Skeleton variant="rounded" height={200} />
      </Box>
    );
  }

  const s = data.stats;
  return (
    <Box>
      <Typography variant="h5" component="h2" gutterBottom>
        Dashboard
      </Typography>

      <Box sx={{ display: "flex", flexWrap: "wrap", gap: 1.5, mb: 3 }}>
        <Stat label="Tenants" value={s.tenants} />
        <Stat label="No delegation" value={s.tenantsNoDelegation} tone={s.tenantsNoDelegation > 0 ? "warning" : "success"} />
        <Stat label="Deployments" value={s.deployments} />
        <Stat label="Failed deployments" value={s.deploymentsFailed} tone={s.deploymentsFailed > 0 ? "error" : "success"} />
        <Stat label="Updates available" value={s.deploymentsUpdateAvailable} tone={s.deploymentsUpdateAvailable > 0 ? "warning" : "success"} />
        <Stat label="Runs (24h)" value={s.runsLast24h} />
        <Stat label="Failed runs (7d)" value={s.runsFailedLast7d} tone={s.runsFailedLast7d > 0 ? "error" : "success"} />
      </Box>

      <Typography variant="h6" component="h3" gutterBottom>
        Needs attention
      </Typography>
      {data.needsAttention.length === 0 ? (
        <Typography variant="body2" color="text.secondary" sx={{ mb: 3 }}>
          Nothing - all quiet.
        </Typography>
      ) : (
        <TableContainer sx={{ mb: 3, overflowX: "auto" }}>
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>What</TableCell>
                <TableCell>Tenant</TableCell>
                <TableCell>Subject</TableCell>
                <TableCell>Detail</TableCell>
                <TableCell>When</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {data.needsAttention.map((a, i) => (
                <TableRow key={i}>
                  <TableCell>
                    <Chip size="small" label={a.kind} color={a.kind === "No delegation" ? "warning" : "error"} />
                  </TableCell>
                  <TableCell>{a.tenantName}</TableCell>
                  <TableCell>{a.subject}</TableCell>
                  <TableCell sx={{ color: "text.secondary" }}>{a.detail}</TableCell>
                  <TableCell>{a.when ? new Date(a.when).toLocaleString() : ""}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </TableContainer>
      )}

      <Typography variant="h6" component="h3" gutterBottom>
        Recent workflow runs
      </Typography>
      {data.recentRuns.length === 0 ? (
        <Typography variant="body2" color="text.secondary">
          No runs recorded yet.
        </Typography>
      ) : (
        <TableContainer sx={{ overflowX: "auto" }}>
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
              {data.recentRuns.map((r) => (
                <TableRow key={r.id}>
                  <TableCell>{new Date(r.startedAt).toLocaleString()}</TableCell>
                  <TableCell>{r.workflowName}</TableCell>
                  <TableCell>{r.tenantName}</TableCell>
                  <TableCell>{r.kind}</TableCell>
                  <TableCell>{r.operator}</TableCell>
                  <TableCell sx={{ maxWidth: 280 }}>
                    <Chip size="small" label={r.succeeded ? "ok" : "failed"} color={r.succeeded ? "success" : "error"} />
                    {/* A native title attribute only shows on mouse hover -- invisible to keyboard
                        and touch users. Plain visible text is accessible to everyone. */}
                    {!r.succeeded && r.error && (
                      <Typography variant="body2" color="error" sx={{ mt: 0.5, wordBreak: "break-word" }}>
                        {r.error}
                      </Typography>
                    )}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </TableContainer>
      )}
    </Box>
  );
}
