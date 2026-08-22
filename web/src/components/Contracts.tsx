import { Fragment, useEffect, useRef, useState } from "react";
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
import { hasInstancePermission } from "../permissions";
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

function PackageQuestRow({
  template,
  desired,
  uploading,
  removing,
  onUpload,
  onRemove
}: {
  template: AppTemplate;
  desired: boolean;
  uploading: boolean;
  removing: boolean;
  onUpload: (file: File) => void;
  onRemove: () => void;
}) {
  const inputRef = useRef<HTMLInputElement>(null);

  return (
    <Stack direction="row" spacing={1} sx={{ alignItems: "center" }}>
      <Checkbox
        checked={desired}
        disabled={!desired || uploading || removing}
        onChange={() => onRemove()}
        slotProps={{ input: { "aria-label": template.displayName } }}
      />
      <Typography variant="body2">{template.displayName}</Typography>
      <Chip
        component="button"
        size="small"
        color="warning"
        disabled={uploading || removing}
        label={uploading ? "Uploading package..." : "So close! Attach a package to unlock \u2192"}
        onClick={() => inputRef.current?.click()}
        sx={{ cursor: uploading || removing ? "default" : "pointer" }}
      />
      <input
        ref={inputRef}
        type="file"
        accept=".intunewin"
        hidden
        disabled={uploading || removing}
        aria-label={`Upload package for ${template.displayName}`}
        onChange={(event) => {
          const file = event.target.files?.[0];
          event.target.value = "";
          if (file) onUpload(file);
        }}
      />
    </Stack>
  );
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
  const [pendingUploadIds, setPendingUploadIds] = useState<Set<string>>(new Set());
  const contractsLoadGeneration = useRef(0);
  const planGeneration = useRef(0);
  const canManage = hasInstancePermission(me, "instance.catalog.manage");
  const toast = useToast();

  const loadAction = useAsyncAction(async (preserve: Contract[] = []) => {
    const generation = ++contractsLoadGeneration.current;
    const loaded = await api.contracts.list();
    if (generation === contractsLoadGeneration.current) {
      setContracts([
        ...loaded,
        ...preserve.filter((contract) => !loaded.some((item) => item.id === contract.id))
      ]);
    }
  });
  const loadTemplatesAction = useAsyncAction(async () => {
    setTemplates(await api.templates.list());
  });
  const createAction = useAsyncAction(async () => {
    const addedName = name;
    const created = await api.contracts.create(name, notes || undefined);
    setName(""); setNotes("");
    setContracts((prev) => prev.some((contract) => contract.id === created.id)
      ? prev
      : [...prev, created]);
    await loadAction.run([created]);
    toast(`${addedName} added`, "success");
  });
  const planAction = useAsyncAction(async (id: string) => {
    const generation = ++planGeneration.current;
    const nextPlan = await api.contracts.plan(id);
    if (generation === planGeneration.current) setPlan(nextPlan);
  });

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

  const mergeMembership = (contractId: string, templateId: string, updated: Contract) => {
    setContracts((prev) => prev.map((contract) => {
      if (contract.id !== contractId) return contract;

      // Each endpoint returns a full contract snapshot, but requests for different templates can
      // overlap. Applying the whole snapshot here would let whichever response arrives last erase
      // a sibling toggle from local state. Merge only this request's membership result instead.
      const desiredAppIds = new Set(contract.desiredAppIds ?? []);
      if ((updated.desiredAppIds ?? []).includes(templateId)) desiredAppIds.add(templateId);
      else desiredAppIds.delete(templateId);
      const mergedIds = [...desiredAppIds];
      return { ...updated, desiredAppIds: mergedIds, desiredAppCount: mergedIds.length };
    }));
  };

  const invalidatePlan = () => {
    planGeneration.current++;
    setPlan(null);
    setLastAction(null);
  };

  const toggle = async (contractId: string, templateId: string, checked: boolean) => {
    invalidatePlan();
    setPendingIds((prev) => new Set(prev).add(templateId));
    try {
      const updated = checked
        ? await api.contracts.addDesiredApp(contractId, templateId)
        : await api.contracts.removeDesiredApp(contractId, templateId);
      contractsLoadGeneration.current++;
      mergeMembership(contractId, templateId, updated);
    } catch (e) {
      // A connection can fail after the server commits. Reconcile just this checkbox from a fresh
      // list without letting that full response overwrite sibling toggles already in local state.
      contractsLoadGeneration.current++;
      try {
        const refreshed = (await api.contracts.list()).find((contract) => contract.id === contractId);
        if (refreshed) mergeMembership(contractId, templateId, refreshed);
      } catch {
        // Preserve and report the mutation error; the user can retry the idempotent operation.
      }
      toast(e instanceof Error ? e.message : String(e), "error");
    } finally {
      setPendingIds((prev) => { const next = new Set(prev); next.delete(templateId); return next; });
    }
  };

  const uploadFromChip = async (templateId: string, file: File) => {
    setPendingUploadIds((prev) => new Set(prev).add(templateId));
    try {
      const updated = await api.templates.uploadPackage(templateId, file);
      setTemplates((prev) => prev.map((template) => template.id === templateId ? updated : template));
      toast("Package uploaded.", "success");
    } catch (e) {
      toast(e instanceof Error ? e.message : String(e), "error");
    } finally {
      setPendingUploadIds((prev) => {
        const next = new Set(prev);
        next.delete(templateId);
        return next;
      });
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
          <TableBody>{contracts.map((contract) => {
            const isManaging = managingId === contract.id;
            const desiredAppIds = contract.desiredAppIds ?? [];
            const visibleTemplates = templates.filter((template) => template.hasPackage || showNoPackage);
            return (
              <Fragment key={contract.id}>
                <TableRow>
                  <TableCell>{contract.name}</TableCell><TableCell>{contract.tenantCount}</TableCell><TableCell>{contract.desiredAppCount}</TableCell>
                  <TableCell>
                    <Stack direction="row" spacing={1}>
                      <Button size="small" onClick={() => { setLastAction("plan"); setPlan(null); void planAction.run(contract.id); }} disabled={planAction.busy}>{planAction.busy ? "Loading plan..." : "Preview plan"}</Button>
                      {canManage && (
                        <Button
                          size="small"
                          aria-expanded={isManaging}
                          aria-controls={isManaging ? `manage-apps-${contract.id}` : undefined}
                          onClick={() => setManagingId(isManaging ? null : contract.id)}
                        >
                          Manage apps
                        </Button>
                      )}
                    </Stack>
                  </TableCell>
                </TableRow>
                {isManaging && (
                  <TableRow>
                    <TableCell colSpan={4}>
                      <Box id={`manage-apps-${contract.id}`} sx={{ py: 1 }}>
                        <Typography variant="h6" component="h3" gutterBottom>Manage apps -- {contract.name}</Typography>
                        <FormControlLabel
                          control={<Switch checked={showNoPackage} onChange={(event) => setShowNoPackage(event.target.checked)} />}
                          label="Show templates without a package"
                          sx={{ mb: 1 }}
                        />
                        {loadTemplatesAction.error ? (
                          <Alert severity="error">{loadTemplatesAction.error}</Alert>
                        ) : loadTemplatesAction.busy ? (
                          <Typography variant="body2" color="text.secondary">Loading templates...</Typography>
                        ) : (
                          <Stack spacing={1}>
                            {visibleTemplates.map((template) => (
                              template.hasPackage ? (
                                <FormControlLabel
                                  key={template.id}
                                  control={
                                    <Checkbox
                                      checked={desiredAppIds.includes(template.id)}
                                      disabled={pendingIds.has(template.id)}
                                      onChange={(event) => void toggle(contract.id, template.id, event.target.checked)}
                                    />
                                  }
                                  label={template.displayName}
                                />
                              ) : (
                                <PackageQuestRow
                                  key={template.id}
                                  template={template}
                                  desired={desiredAppIds.includes(template.id)}
                                  uploading={pendingUploadIds.has(template.id)}
                                  removing={pendingIds.has(template.id)}
                                  onUpload={(file) => void uploadFromChip(template.id, file)}
                                  onRemove={() => void toggle(contract.id, template.id, false)}
                                />
                              )
                            ))}
                            {visibleTemplates.length === 0 && (
                              <Typography variant="body2" color="text.secondary">
                                {templates.length === 0
                                  ? "No templates yet."
                                  : "No package-ready templates. Show templates without a package to attach one."}
                              </Typography>
                            )}
                          </Stack>
                        )}
                      </Box>
                    </TableCell>
                  </TableRow>
                )}
              </Fragment>
            );
          })}</TableBody>
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
