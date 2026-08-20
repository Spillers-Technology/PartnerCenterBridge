import { useEffect, useState } from "react";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Chip from "@mui/material/Chip";
import Dialog from "@mui/material/Dialog";
import DialogActions from "@mui/material/DialogActions";
import DialogContent from "@mui/material/DialogContent";
import DialogTitle from "@mui/material/DialogTitle";
import IconButton from "@mui/material/IconButton";
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
import DeleteIcon from "@mui/icons-material/Delete";
import EditIcon from "@mui/icons-material/Edit";
import UploadFileIcon from "@mui/icons-material/UploadFile";
import { api } from "../api";
import { useAsyncAction } from "../hooks/useAsyncAction";
import { useConfirm } from "../hooks/useConfirm";
import { useIsPhone } from "../hooks/useIsPhone";
import { useToast } from "../hooks/useToast";
import type { AppTemplate, MeProfile } from "../types";

const emptyForm = { displayName: "", description: "", publisher: "", installCommandLine: "", uninstallCommandLine: "" };
type TemplateForm = typeof emptyForm;

function TemplateFields({ form, onChange }: { form: TemplateForm; onChange: (next: TemplateForm) => void }) {
  return (
    <Stack spacing={2} sx={{ mt: 1 }}>
      <TextField label="Display name" value={form.displayName} onChange={(e) => onChange({ ...form, displayName: e.target.value })} autoFocus />
      <TextField label="Description" value={form.description} onChange={(e) => onChange({ ...form, description: e.target.value })} />
      <TextField label="Publisher" value={form.publisher} onChange={(e) => onChange({ ...form, publisher: e.target.value })} />
      <TextField
        label="Install command line"
        value={form.installCommandLine}
        onChange={(e) => onChange({ ...form, installCommandLine: e.target.value })}
      />
      <TextField
        label="Uninstall command line"
        value={form.uninstallCommandLine}
        onChange={(e) => onChange({ ...form, uninstallCommandLine: e.target.value })}
      />
    </Stack>
  );
}

function isFormValid(form: TemplateForm): boolean {
  return Boolean(form.displayName && form.installCommandLine && form.uninstallCommandLine);
}

export function AppTemplates({ me }: { me: MeProfile | null }) {
  const [templates, setTemplates] = useState<AppTemplate[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [form, setForm] = useState<TemplateForm>(emptyForm);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editForm, setEditForm] = useState<TemplateForm>(emptyForm);
  const canManage = !me || me.isSystemAdmin;
  const confirm = useConfirm();
  const showToast = useToast();
  const isPhone = useIsPhone();

  const load = () =>
    api.templates.list()
      .then((list) => { setTemplates(list); setLoadError(null); })
      .catch((e) => setLoadError(e instanceof Error ? e.message : String(e)));
  useEffect(() => { void load(); }, []);

  const createAction = useAsyncAction(async () => {
    await api.templates.create({ ...form });
    await load();
    setForm(emptyForm);
    showToast("Template created.", "success");
  });

  const updateAction = useAsyncAction(async (id: string, next: TemplateForm) => {
    await api.templates.update(id, { ...next });
    await load();
    setEditingId(null);
    showToast("Template updated.", "success");
  });

  const removeAction = useAsyncAction(async (id: string) => {
    await api.templates.remove(id);
    await load();
    showToast("Template deleted.", "success");
  });

  const uploadAction = useAsyncAction(async (id: string, file: File) => {
    await api.templates.uploadPackage(id, file);
    await load();
    showToast("Package uploaded.", "success");
  });

  const startEdit = (t: AppTemplate) => {
    setEditingId(t.id);
    setEditForm({
      displayName: t.displayName,
      description: t.description ?? "",
      publisher: t.publisher ?? "",
      installCommandLine: t.installCommandLine,
      uninstallCommandLine: t.uninstallCommandLine
    });
  };

  const remove = async (t: AppTemplate) => {
    const ok = await confirm({
      title: "Delete template?",
      message: `"${t.displayName}" will be removed. This cannot be undone.`,
      confirmLabel: "Delete",
      destructive: true
    });
    if (!ok) return;
    await removeAction.run(t.id);
  };

  const upload = (id: string, file?: File) => {
    if (!file) return;
    void uploadAction.run(id, file);
  };

  const status: "loading" | "empty" | "ready" =
    templates === null ? "loading" : templates.length === 0 ? "empty" : "ready";

  return (
    <Box>
      <Typography variant="h5" component="h2" gutterBottom>
        App Templates
      </Typography>

      {canManage && (
        <Stack
          component="form"
          spacing={2}
          sx={{ mb: 3, maxWidth: 480 }}
          onSubmit={(ev) => {
            ev.preventDefault();
            if (!isFormValid(form)) return;
            void createAction.run();
          }}
        >
          <TemplateFields form={form} onChange={setForm} />
          <Button type="submit" variant="contained" disabled={createAction.busy || !isFormValid(form)} sx={{ alignSelf: "flex-start" }}>
            {createAction.busy ? "Creating..." : "Create template"}
          </Button>
          {createAction.error && <Alert severity="error">{createAction.error}</Alert>}
        </Stack>
      )}

      {loadError && <Alert severity="error" sx={{ mb: 2 }}>{loadError}</Alert>}

      {!loadError && status === "loading" && <Skeleton variant="rounded" height={200} />}

      {!loadError && status === "empty" && (
        <Typography variant="body2" color="text.secondary">
          No templates yet.
        </Typography>
      )}

      {!loadError && status === "ready" && templates && (
        <TableContainer sx={{ overflowX: "auto" }}>
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>Name</TableCell>
                <TableCell>Publisher</TableCell>
                <TableCell>Version</TableCell>
                <TableCell>Package</TableCell>
                {canManage && <TableCell>Actions</TableCell>}
              </TableRow>
            </TableHead>
            <TableBody>
              {templates.map((t) => (
                <TableRow key={t.id}>
                  <TableCell>
                    <Typography variant="body2">{t.displayName}</Typography>
                    {t.description && (
                      <Typography variant="caption" color="text.secondary">
                        {t.description}
                      </Typography>
                    )}
                  </TableCell>
                  <TableCell>{t.publisher ?? "-"}</TableCell>
                  <TableCell>v{t.contentVersion}</TableCell>
                  <TableCell>
                    <Chip
                      size="small"
                      label={t.hasPackage ? "Uploaded" : "No package"}
                      color={t.hasPackage ? "success" : "warning"}
                    />
                  </TableCell>
                  {canManage && (
                    <TableCell>
                      <Stack direction="row" spacing={0.5} sx={{ alignItems: "center" }}>
                        <IconButton aria-label={`Edit ${t.displayName}`} size="small" onClick={() => startEdit(t)}>
                          <EditIcon fontSize="small" />
                        </IconButton>
                        <IconButton
                          aria-label={`Delete ${t.displayName}`}
                          size="small"
                          onClick={() => void remove(t)}
                          disabled={removeAction.busy}
                        >
                          <DeleteIcon fontSize="small" />
                        </IconButton>
                        <IconButton
                          aria-label={`Upload package for ${t.displayName}`}
                          size="small"
                          component="label"
                          disabled={uploadAction.busy}
                        >
                          <UploadFileIcon fontSize="small" />
                          <input
                            type="file"
                            accept=".intunewin"
                            hidden
                            onChange={(e) => { upload(t.id, e.target.files?.[0]); e.target.value = ""; }}
                          />
                        </IconButton>
                      </Stack>
                    </TableCell>
                  )}
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </TableContainer>
      )}

      {removeAction.error && <Alert severity="error" sx={{ mt: 2 }}>{removeAction.error}</Alert>}
      {uploadAction.error && <Alert severity="error" sx={{ mt: 2 }}>{uploadAction.error}</Alert>}

      <Dialog open={editingId !== null} onClose={() => setEditingId(null)} fullWidth maxWidth="sm" fullScreen={isPhone}>
        <DialogTitle>Edit template</DialogTitle>
        <DialogContent>
          <TemplateFields form={editForm} onChange={setEditForm} />
          {updateAction.error && <Alert severity="error" sx={{ mt: 2 }}>{updateAction.error}</Alert>}
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setEditingId(null)}>Cancel</Button>
          <Button
            variant="contained"
            disabled={updateAction.busy || !isFormValid(editForm)}
            onClick={() => { if (editingId) void updateAction.run(editingId, editForm); }}
          >
            {updateAction.busy ? "Saving..." : "Save"}
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
}
