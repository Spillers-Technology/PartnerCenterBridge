import { useEffect, useState } from "react";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Checkbox from "@mui/material/Checkbox";
import Chip from "@mui/material/Chip";
import FormControlLabel from "@mui/material/FormControlLabel";
import Stack from "@mui/material/Stack";
import Switch from "@mui/material/Switch";
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
import type { AppTemplate, Contract, MeProfile } from "../types";

type PlanItem = Awaited<ReturnType<typeof api.contracts.plan>>[number];

function planActionColor(action: string): "default" | "success" | "warning" {
  switch (action.toLowerCase()) {
    case "install": return "success";
    case "remove": return "warning";
    default: return "default";
  }
}

export function Contracts({ me }: { me: MeProfile | null }) {
  const [contracts, setContracts] = useState<Contract[]>([]);
  const [templates, setTemplates] = useState<AppTemplate[]>([]);
  const [name, setName] = useState("");
  const [notes, setNotes] = useState("");
  const [plan, setPlan] = useState<PlanItem[] | null>(null);
  const [lastAction, setLastAction] = useState<"load" | "create" | "plan" | null>(null);
  const [managingId, setManagingId] = useState<string | null>(null);
  const [showNoPackage, setShowNoPackage] = useState(false);
  const [pendingIds, setPendingIds] = useState<Set<string>>(new Set());
  const canManage = !me || me.isSystemAdmin;
  const toast = useToast();

  const loadAction = useAsyncAction(async () => {
    setContracts(await api.contracts.list());
  });
  const loadTemplatesAction = useAsyncAction(async () => {
    setTemplates(await api.templates.list());
  });
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
    void loadTemplatesAction.run();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const error =
    lastAction === "load" ? loadAction.error :
    lastAction === "create" ? createAction.error :
    lastAction === "plan" ? planAction.error :
    null;

  const toggle = async (contractId: string, templateId: string, checked: boolean) => {
    setPendingIds((prev) => new Set(prev).add(templateId));
    try {
      const updated = checked
        ? await api.contracts.addDesiredApp(contractId, templateId)
        : await api.contracts.removeDesiredApp(contractId, templateId);
      setContracts((prev) => prev.map((c) => (c.id === contractId ? updated : c)));
    } catch (e) {
      toast(e instanceof Error ? e.message : String(e), "error");
    } finally {
      setPendingIds((prev) => { const next = new Set(prev); next.delete(templateId); return next; });
    }
  };

  const uploadFromChip = async (templateId: string, file: File) => {
    try {
      await api.templates.uploadPackage(templateId, file);
      setTemplates(await api.templates.list());
      toast("Package uploaded.", "success");
    } catch (e) {
      toast(e instanceof Error ? e.message : String(e), "error");
    }
  };

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
              <TableCell>
                <Stack direction="row" spacing={1}>
                  <Button size="small" onClick={() => { setLastAction("plan"); setPlan(null); void planAction.run(contract.id); }} disabled={planAction.busy}>{planAction.busy ? "Loading plan..." : "Preview plan"}</Button>
                  {canManage && (
                    <Button size="small" onClick={() => setManagingId(managingId === contract.id ? null : contract.id)}>
                      Manage apps
                    </Button>
                  )}
                </Stack>
              </TableCell>
            </TableRow>
          ))}</TableBody>
        </Table>
      </TableContainer>
      {managingId && (() => {
        const contract = contracts.find((c) => c.id === managingId);
        if (!contract) return null;
        const visible = templates.filter((t) => t.hasPackage || showNoPackage);
        return (
          <Box sx={{ mb: 3 }}>
            <Typography variant="h6" component="h3" gutterBottom>Manage apps -- {contract.name}</Typography>
            <FormControlLabel
              control={<Switch checked={showNoPackage} onChange={(e) => setShowNoPackage(e.target.checked)} />}
              label="Show templates without a package"
              sx={{ mb: 1 }}
            />
            <Stack spacing={1}>
              {visible.map((t) => (
                t.hasPackage ? (
                  <FormControlLabel
                    key={t.id}
                    control={
                      <Checkbox
                        checked={contract.desiredAppIds.includes(t.id)}
                        disabled={pendingIds.has(t.id)}
                        onChange={(e) => void toggle(contract.id, t.id, e.target.checked)}
                      />
                    }
                    label={t.displayName}
                  />
                ) : (
                  <Stack key={t.id} direction="row" spacing={1} sx={{ alignItems: "center" }}>
                    <Checkbox checked={false} disabled slotProps={{ input: { "aria-label": t.displayName } }} />
                    <Typography variant="body2">{t.displayName}</Typography>
                    <Box component="label" sx={{ cursor: "pointer" }}>
                      <Chip
                        size="small"
                        color="warning"
                        label={"So close! Attach a package to unlock \u2192"}
                        sx={{ cursor: "pointer" }}
                      />
                      <input
                        type="file"
                        accept=".intunewin"
                        hidden
                        aria-label={`Upload package for ${t.displayName}`}
                        onChange={(e) => {
                          const file = e.target.files?.[0];
                          e.target.value = "";
                          if (file) void uploadFromChip(t.id, file);
                        }}
                      />
                    </Box>
                  </Stack>
                )
              ))}
              {visible.length === 0 && (
                <Typography variant="body2" color="text.secondary">No templates yet.</Typography>
              )}
            </Stack>
          </Box>
        );
      })()}
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
