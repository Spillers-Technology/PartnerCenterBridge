import { useEffect, useState } from "react";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Chip from "@mui/material/Chip";
import Stack from "@mui/material/Stack";
import Table from "@mui/material/Table";
import TableBody from "@mui/material/TableBody";
import TableCell from "@mui/material/TableCell";
import TableContainer from "@mui/material/TableContainer";
import TableHead from "@mui/material/TableHead";
import TableRow from "@mui/material/TableRow";
import Typography from "@mui/material/Typography";
import { api } from "../api";
import type { PendingAction } from "../types";
import { useAsyncAction } from "../hooks/useAsyncAction";
import { useConfirm } from "../hooks/useConfirm";
import { useToast } from "../hooks/useToast";

type Action = "approve" | "reject" | "retry";

/**
 * Each row owns its own useAsyncAction instance so two rows can be decided independently --
 * a single shared action instance across the whole table would silently no-op a second row's
 * confirmed action while the first row's mutation was still in flight (useAsyncAction is
 * single-flight by design).
 */
function ApprovalRow({ item, onDecided }: { item: PendingAction; onDecided: () => void }) {
  const confirm = useConfirm();
  const toast = useToast();
  const failed = item.status === "Approved" && Boolean(item.executionError);

  const decideAction = useAsyncAction(async (action: Action) => {
    if (action === "approve") await api.pendingActions.approve(item.id);
    else if (action === "reject") await api.pendingActions.reject(item.id);
    else await api.pendingActions.retry(item.id);
    onDecided();
    toast(action === "approve" ? "Approved." : action === "reject" ? "Rejected." : "Retried.");
  });

  const decide = async (action: Action) => {
    const options =
      action === "approve"
        ? {
            title: "Approve this action?",
            message: `Approve "${item.actionType}" for ${item.tenantName}? This will let it run.`,
            destructive: true
          }
        : action === "reject"
        ? {
            title: "Reject this action?",
            message: `Reject "${item.actionType}" for ${item.tenantName}? It will not run.`,
            destructive: true
          }
        : {
            title: "Retry this action?",
            message: `Retry "${item.actionType}" for ${item.tenantName}?`,
            destructive: true
          };
    if (!(await confirm(options))) return;
    void decideAction.run(action);
  };

  return (
    <TableRow>
      <TableCell>{item.tenantName}</TableCell>
      <TableCell>{item.actionType}</TableCell>
      <TableCell>
        <Chip size="small" label={failed ? "Failed" : "Pending"} color={failed ? "error" : "warning"} />
      </TableCell>
      <TableCell>
        {item.previewSummary}
        {failed && (
          <Typography variant="body2" color="error" sx={{ mt: 0.5 }}>
            {item.executionError}
          </Typography>
        )}
        {decideAction.error && (
          <Typography variant="body2" color="error" sx={{ mt: 0.5 }}>
            {decideAction.error}
          </Typography>
        )}
      </TableCell>
      <TableCell>{new Date(item.createdAt).toLocaleString()}</TableCell>
      <TableCell>{new Date(item.expiresAt).toLocaleString()}</TableCell>
      <TableCell>
        <Stack direction="row" spacing={1} useFlexGap sx={{ flexWrap: "wrap" }}>
          {failed ? (
            <Button size="small" variant="outlined" disabled={decideAction.busy} onClick={() => decide("retry")}>
              {decideAction.busy ? "Retrying..." : "Retry"}
            </Button>
          ) : (
            <>
              <Button size="small" variant="contained" disabled={decideAction.busy} onClick={() => decide("approve")}>
                {decideAction.busy ? "Approving..." : "Approve"}
              </Button>
              <Button size="small" variant="outlined" color="error" disabled={decideAction.busy} onClick={() => decide("reject")}>
                {decideAction.busy ? "Rejecting..." : "Reject"}
              </Button>
            </>
          )}
        </Stack>
      </TableCell>
    </TableRow>
  );
}

export function Approvals() {
  const [items, setItems] = useState<PendingAction[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);

  const load = () =>
    api.pendingActions.list().then(setItems).catch((e) => setLoadError(e instanceof Error ? e.message : String(e)));
  useEffect(() => { load(); }, []);

  return (
    <Box>
      <Typography variant="h5" component="h2" gutterBottom>
        Approvals
      </Typography>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
        Mutating actions requested through MCP land here for tenants in the default Queue approval
        mode. Nothing runs until you approve it.
      </Typography>

      {loadError && <Alert severity="error" sx={{ mb: 2 }}>{loadError}</Alert>}

      {items && items.length === 0 && (
        <Typography variant="body2" color="text.secondary">No pending approvals.</Typography>
      )}

      {items && items.length > 0 && (
        <TableContainer>
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>Tenant</TableCell>
                <TableCell>Action</TableCell>
                <TableCell>Status</TableCell>
                <TableCell>Preview</TableCell>
                <TableCell>Requested</TableCell>
                <TableCell>Expires</TableCell>
                <TableCell></TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {items.map((item) => (
                <ApprovalRow key={item.id} item={item} onDecided={load} />
              ))}
            </TableBody>
          </Table>
        </TableContainer>
      )}
    </Box>
  );
}
