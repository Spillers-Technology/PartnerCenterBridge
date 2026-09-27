import { useEffect, useState, type ReactNode } from "react";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import FormControlLabel from "@mui/material/FormControlLabel";
import FormHelperText from "@mui/material/FormHelperText";
import MenuItem from "@mui/material/MenuItem";
import Skeleton from "@mui/material/Skeleton";
import Stack from "@mui/material/Stack";
import Switch from "@mui/material/Switch";
import TextField from "@mui/material/TextField";
import Typography from "@mui/material/Typography";
import { api, errorText, isApiStatus } from "../api";
import { useToast } from "../hooks/useToast";
import type { OffboardingPolicy } from "../types";

type BoolKey = "blockSignIn" | "revokeSessions" | "convertMailboxToShared" | "removeLicenses" | "hideFromGal";

const SWITCHES: { key: BoolKey; label: string; help: string }[] = [
  { key: "blockSignIn", label: "Block sign-in", help: "Stops new sign-ins right away. A synced account also has to be disabled on-premises." },
  { key: "revokeSessions", label: "Revoke sessions", help: "Signs the person out of every app and device." },
  { key: "convertMailboxToShared", label: "Convert mailbox to shared", help: "Keeps the mail without a license. Needs Exchange Online on this workbench; license removal and group cleanup wait until the conversion is verified." },
  { key: "removeLicenses", label: "Remove licenses", help: "Frees the licenses. Group-based licenses go with the group membership instead." },
  { key: "hideFromGal", label: "Hide from the address list", help: "Listed in the plan for a person to do: PCB can't change this yet." }
];

const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

function clientErrors(p: OffboardingPolicy): Partial<Record<"forwardTo" | "followUpDays", string>> {
  const errors: Partial<Record<"forwardTo" | "followUpDays", string>> = {};
  if (p.forwardTo && p.forwardTo.trim() && !EMAIL.test(p.forwardTo.trim())) errors.forwardTo = "Enter a plain address like manager@contoso.com.";
  if (!Number.isInteger(p.followUpDays) || p.followUpDays < 0 || p.followUpDays > 3650) errors.followUpDays = "Use a whole number of days from 0 to 3650.";
  return errors;
}

function Field({ children, help }: { children: ReactNode; help: string }) {
  return (
    <Box sx={{ minWidth: 0 }}>
      {children}
      <FormHelperText sx={{ mt: 0, mx: 0 }}>{help}</FormHelperText>
    </Box>
  );
}

/**
 * A contract's offboarding policy: what offboarding does for every leaver in tenants under this
 * contract. Editable with the catalog manager role; read-only (with the reason) otherwise. The
 * server validates too, and its messages are shown as-is.
 */
export function OffboardingPolicyEditor({
  contractId,
  contractName,
  canEdit
}: {
  contractId: string;
  contractName: string;
  canEdit: boolean;
}) {
  const toast = useToast();
  const [saved, setSaved] = useState<OffboardingPolicy | null>(null);
  const [draft, setDraft] = useState<OffboardingPolicy | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let alive = true;
    setLoadError(null);
    api.contracts.getOffboardingPolicy(contractId)
      .then((p) => { if (alive) { setSaved(p); setDraft(p); } })
      .catch((e) => {
        if (!alive) return;
        setLoadError(isApiStatus(e, 404)
          ? "This server doesn't support offboarding policies (it needs Partner Center Bridge 0.9.0 or later)."
          : errorText(e));
      });
    return () => { alive = false; };
  }, [contractId, attempt]);

  if (loadError) {
    return (
      <Alert severity="error" action={<Button color="inherit" size="small" onClick={() => setAttempt((n) => n + 1)}>Retry</Button>}>
        {loadError}
      </Alert>
    );
  }
  if (!draft || !saved) return <Skeleton variant="rounded" height={160} aria-label="Loading offboarding policy" />;

  const errors = clientErrors(draft);
  const dirty = JSON.stringify(draft) !== JSON.stringify(saved);
  const set = <K extends keyof OffboardingPolicy>(key: K, value: OffboardingPolicy[K]) => {
    setSaveError(null);
    setDraft({ ...draft, [key]: value });
  };

  const save = async () => {
    if (Object.keys(errors).length > 0 || saving) return;
    setSaving(true);
    setSaveError(null);
    try {
      const next = await api.contracts.putOffboardingPolicy(contractId, {
        ...draft,
        forwardTo: draft.forwardTo?.trim() ? draft.forwardTo.trim() : null
      });
      setSaved(next);
      setDraft(next);
      toast(`Offboarding policy for ${contractName} saved`, "success");
    } catch (e) {
      setSaveError(errorText(e));
    } finally {
      setSaving(false);
    }
  };

  return (
    <Box component="section" aria-label={`Offboarding policy for ${contractName}`}>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
        What offboarding does for every leaver in tenants on this contract. A technician can still
        change the sign-in, session, license, group and mailbox steps for one offboarding.
      </Typography>
      {!canEdit && (
        <Alert severity="info" variant="outlined" sx={{ mb: 2 }}>
          Read-only: changing a contract's offboarding policy needs the catalog manager role.
        </Alert>
      )}
      <Box sx={{ display: "grid", gridTemplateColumns: { xs: "1fr", md: "repeat(2, minmax(0, 1fr))" }, columnGap: 3, rowGap: 2 }}>
        {SWITCHES.map((s) => (
          <Field key={s.key} help={s.help}>
            <FormControlLabel
              control={<Switch checked={draft[s.key]} onChange={(e) => set(s.key, e.target.checked)} disabled={!canEdit} />}
              label={s.label}
            />
          </Field>
        ))}
        <Field help="Which of the leaver's direct memberships to remove.">
          <TextField
            select
            fullWidth
            size="small"
            label="Group cleanup"
            value={draft.groupCleanup}
            onChange={(e) => set("groupCleanup", e.target.value as OffboardingPolicy["groupCleanup"])}
            disabled={!canEdit}
          >
            <MenuItem value="None">Keep every membership</MenuItem>
            <MenuItem value="RemoveAssignable">Remove cloud groups; leave role-assignable ones</MenuItem>
            <MenuItem value="RemoveAll">Remove every membership PCB can change</MenuItem>
          </TextField>
        </Field>
        <Field help="Optional. Set together with the shared mailbox conversion.">
          <TextField
            fullWidth
            size="small"
            label="Forward mail to"
            placeholder="manager@contoso.com"
            value={draft.forwardTo ?? ""}
            onChange={(e) => set("forwardTo", e.target.value)}
            error={Boolean(errors.forwardTo)}
            helperText={errors.forwardTo}
            disabled={!canEdit}
          />
        </Field>
        <Field help="Listed in the plan for a person to do: PCB doesn't grant mailbox permissions yet.">
          <TextField
            select
            fullWidth
            size="small"
            label="Manager access to the mailbox"
            value={draft.managerAccess}
            onChange={(e) => set("managerAccess", e.target.value as OffboardingPolicy["managerAccess"])}
            disabled={!canEdit}
          >
            <MenuItem value="None">None</MenuItem>
            <MenuItem value="FullAccess">Full access</MenuItem>
          </TextField>
        </Field>
        <Field help="Retire removes company data and management from the leaver's Intune devices; personal data stays.">
          <TextField
            select
            fullWidth
            size="small"
            label="Managed devices"
            value={draft.wipeDevices}
            onChange={(e) => set("wipeDevices", e.target.value as OffboardingPolicy["wipeDevices"])}
            disabled={!canEdit}
          >
            <MenuItem value="None">Leave devices alone</MenuItem>
            <MenuItem value="Retire">Retire devices</MenuItem>
          </TextField>
        </Field>
        <Field help="Days until the account should be reviewed for deletion; noted in the evidence, not scheduled. 0 = no reminder.">
          <TextField
            fullWidth
            size="small"
            type="number"
            label="Follow-up after (days)"
            value={Number.isNaN(draft.followUpDays) ? "" : draft.followUpDays}
            onChange={(e) => set("followUpDays", e.target.value === "" ? Number.NaN : Number(e.target.value))}
            error={Boolean(errors.followUpDays)}
            helperText={errors.followUpDays}
            disabled={!canEdit}
            slotProps={{ htmlInput: { min: 0, max: 3650, step: 1 } }}
          />
        </Field>
      </Box>
      {saveError && <Alert severity="error" sx={{ mt: 2 }}>{saveError}</Alert>}
      {canEdit && (
        <Stack direction="row" spacing={1} sx={{ mt: 2 }}>
          <Button variant="contained" onClick={() => void save()} disabled={!dirty || saving || Object.keys(errors).length > 0}>
            {saving ? "Saving..." : "Save policy"}
          </Button>
          <Button onClick={() => { setDraft(saved); setSaveError(null); }} disabled={!dirty || saving}>Discard changes</Button>
        </Stack>
      )}
    </Box>
  );
}
