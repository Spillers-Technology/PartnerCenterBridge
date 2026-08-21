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
import TextField from "@mui/material/TextField";
import Typography from "@mui/material/Typography";
import { api } from "../api";
import { useAsyncAction } from "../hooks/useAsyncAction";
import { useToast } from "../hooks/useToast";
import type { Contract } from "../types";

type PlanItem = Awaited<ReturnType<typeof api.contracts.plan>>[number];

function planActionColor(action: string): "default" | "success" | "warning" {
  switch (action.toLowerCase()) {
    case "install": return "success";
    case "remove": return "warning";
    default: return "default";
  }
}

export function Contracts() {
  const [contracts, setContracts] = useState<Contract[]>([]);
  const [name, setName] = useState("");
  const [notes, setNotes] = useState("");
  const [plan, setPlan] = useState<PlanItem[] | null>(null);
  const [lastAction, setLastAction] = useState<"load" | "create" | "plan" | null>(null);
  const toast = useToast();
  const loadAction = useAsyncAction(async () => { setContracts(await api.contracts.list()); });
  const createAction = useAsyncAction(async () => {
    const addedName = name;
    await api.contracts.create(name, notes || undefined);
    setName(""); setNotes("");
    await loadAction.run();
    toast(`${addedName} added`, "success");
  });
  const planAction = useAsyncAction(async (id: string) => { setPlan(await api.contracts.plan(id)); });

  useEffect(() => {
    setLastAction("load");
    void loadAction.run();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const error =
    lastAction === "load" ? loadAction.error :
    lastAction === "create" ? createAction.error :
    lastAction === "plan" ? planAction.error :
    null;

  return (
    <Box component="section">
      <Typography variant="h5" component="h2" gutterBottom>Contracts</Typography>
      <Stack component="form" direction={{ xs: "column", sm: "row" }} spacing={1} sx={{ mb: 2 }} onSubmit={(ev) => {
        ev.preventDefault();
        if (!name.trim()) return;
        setLastAction("create");
        void createAction.run();
      }}>
        <TextField size="small" label="Contract name" value={name} onChange={(e) => setName(e.target.value)} />
        <TextField size="small" label="Notes (optional)" value={notes} onChange={(e) => setNotes(e.target.value)} />
        <Button type="submit" variant="contained" disabled={createAction.busy}>{createAction.busy ? "Adding..." : "Add contract"}</Button>
      </Stack>
      {error && <Alert severity="error" sx={{ mb: 2 }}>{error}</Alert>}
      <TableContainer sx={{ mb: 3, overflowX: "auto" }}>
        <Table size="small">
          <TableHead><TableRow><TableCell>Name</TableCell><TableCell>Tenants</TableCell><TableCell>Desired apps</TableCell><TableCell></TableCell></TableRow></TableHead>
          <TableBody>{contracts.map((contract) => (
            <TableRow key={contract.id}>
              <TableCell>{contract.name}</TableCell><TableCell>{contract.tenantCount}</TableCell><TableCell>{contract.desiredAppCount}</TableCell>
              <TableCell><Button size="small" onClick={() => { setLastAction("plan"); setPlan(null); void planAction.run(contract.id); }} disabled={planAction.busy}>{planAction.busy ? "Loading plan..." : "Preview plan"}</Button></TableCell>
            </TableRow>
          ))}</TableBody>
        </Table>
      </TableContainer>
      {plan && (
        <Box>
          <Typography variant="h6" component="h3" gutterBottom>Reconcile plan (dry run)</Typography>
          <TableContainer sx={{ overflowX: "auto" }}>
            <Table size="small">
              <TableHead><TableRow><TableCell>Tenant</TableCell><TableCell>Template</TableCell><TableCell>Action</TableCell></TableRow></TableHead>
              <TableBody>
                {plan.map((item) => (
                  <TableRow key={`${item.tenantId}-${item.templateId}`}>
                    <TableCell>{item.tenantName}</TableCell><TableCell>{item.templateName}</TableCell>
                    <TableCell><Chip size="small" label={item.action} color={planActionColor(item.action)} /></TableCell>
                  </TableRow>
                ))}
                {plan.length === 0 && <TableRow><TableCell colSpan={3}><Typography variant="body2" color="text.secondary">Nothing to do.</Typography></TableCell></TableRow>}
              </TableBody>
            </Table>
          </TableContainer>
        </Box>
      )}
    </Box>
  );
}
