import { Link as RouterLink } from "react-router";
import Box from "@mui/material/Box";
import Link from "@mui/material/Link";
import Table from "@mui/material/Table";
import TableBody from "@mui/material/TableBody";
import TableCell from "@mui/material/TableCell";
import TableContainer from "@mui/material/TableContainer";
import TableHead from "@mui/material/TableHead";
import TableRow from "@mui/material/TableRow";
import Typography from "@mui/material/Typography";
import { humanizeEnum, Timestamp } from "../format";
import { useIsPhone } from "../hooks/useIsPhone";
import { personPath, runPath } from "../paths";
import type { WorkflowRunRecord } from "../types";
import { RunResultChips } from "./operationUi";

function TargetLink({ run }: { run: WorkflowRunRecord }) {
  if (!run.targetId) return <>{run.targetDisplayName ?? "--"}</>;
  return (
    <Link component={RouterLink} to={personPath(run.tenantId, run.targetId)} underline="hover" color="inherit" sx={{ overflowWrap: "anywhere" }}>
      {run.targetDisplayName || run.targetId}
    </Link>
  );
}

/**
 * Recorded runs: a table on tablets and up, stacked rows on phones (no sideways scrolling). Each
 * run links to its evidence; the person it targeted links to their workspace.
 */
export function RunList({
  runs,
  label,
  showTenant = true,
  showTarget = true
}: {
  runs: WorkflowRunRecord[];
  label: string;
  showTenant?: boolean;
  showTarget?: boolean;
}) {
  const isPhone = useIsPhone();

  if (isPhone) {
    return (
      <Box component="ul" aria-label={label} sx={{ listStyle: "none", m: 0, p: 0 }}>
        {runs.map((r) => (
          <Box component="li" key={r.id} sx={{ py: 1.25, borderBottom: 1, borderColor: "divider" }}>
            <Link component={RouterLink} to={runPath(r.id)} underline="hover" sx={{ fontWeight: 500 }}>{r.workflowName}</Link>
            <Box sx={{ mt: 0.5 }}><RunResultChips run={r} /></Box>
            <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5, overflowWrap: "anywhere" }}>
              {showTarget && r.targetId && <><TargetLink run={r} />{" · "}</>}
              {showTenant && <>{r.tenantName}{" · "}</>}
              {humanizeEnum(r.kind)} by {r.operator} · <Timestamp value={r.startedAt} />
            </Typography>
            {!r.succeeded && r.error && (
              <Typography variant="body2" color="error" sx={{ mt: 0.5, overflowWrap: "anywhere" }}>{r.error}</Typography>
            )}
          </Box>
        ))}
      </Box>
    );
  }

  return (
    <TableContainer sx={{ overflowX: "auto" }}>
      <Table size="small" aria-label={label}>
        <TableHead>
          <TableRow>
            <TableCell>When</TableCell>
            <TableCell>Operation</TableCell>
            {showTarget && <TableCell>Person</TableCell>}
            {showTenant && <TableCell>Tenant</TableCell>}
            <TableCell>Kind</TableCell>
            <TableCell>Operator</TableCell>
            <TableCell>Result</TableCell>
          </TableRow>
        </TableHead>
        <TableBody>
          {runs.map((r) => (
            <TableRow key={r.id}>
              <TableCell sx={{ whiteSpace: "nowrap" }}><Timestamp value={r.startedAt} /></TableCell>
              <TableCell>
                <Link component={RouterLink} to={runPath(r.id)} underline="hover">{r.workflowName}</Link>
              </TableCell>
              {showTarget && <TableCell sx={{ overflowWrap: "anywhere" }}><TargetLink run={r} /></TableCell>}
              {showTenant && (
                <TableCell>
                  <Link component={RouterLink} to={`/tenants/${r.tenantId}`} underline="hover" color="inherit">{r.tenantName}</Link>
                </TableCell>
              )}
              <TableCell>{humanizeEnum(r.kind)}</TableCell>
              <TableCell sx={{ overflowWrap: "anywhere" }}>{r.operator}</TableCell>
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
  );
}
