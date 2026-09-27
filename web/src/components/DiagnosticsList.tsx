import { useState } from "react";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Chip from "@mui/material/Chip";
import Stack from "@mui/material/Stack";
import Typography from "@mui/material/Typography";
import type { DiagnosticCheck, DiagnosticStatus } from "../types";
import { api, errorText } from "../api";
import { useConfirm } from "../hooks/useConfirm";
import { QuestChip } from "./QuestChip";

const STATUS_CHIP: Record<DiagnosticStatus, { label: string; color: "success" | "warning" | "error" | "default" }> = {
  Ok: { label: "OK", color: "success" },
  Warning: { label: "Warning", color: "warning" },
  Error: { label: "Error", color: "error" },
  NotConfigured: { label: "Not configured", color: "default" }
};

export function DiagnosticStatusChip({ status }: { status: DiagnosticStatus }) {
  const chip = STATUS_CHIP[status] ?? { label: status, color: "default" as const };
  return <Chip size="small" label={chip.label} color={chip.color} variant={status === "NotConfigured" ? "outlined" : "filled"} />;
}

/** A check needs the operator when it is anything but OK. */
export function needsAttention(check: DiagnosticCheck) {
  return check.status !== "Ok";
}

/**
 * One row per diagnostics check: what it is, its status, the detail the server reported and --
 * when it isn't OK -- the fix as a quest chip (command to copy and/or a link to the right screen).
 */
export function DiagnosticsList({ checks, onChanged }: { checks: DiagnosticCheck[]; onChanged?: () => Promise<unknown> }) {
  const confirm = useConfirm();
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const install = async (id: "pwsh" | "exchange-module") => {
    const title = id === "pwsh" ? "Install PowerShell 7?" : "Install ExchangeOnlineManagement?";
    const message = id === "pwsh"
      ? "WinGet will download and install Microsoft PowerShell for the current Windows user. Package and source agreements will be accepted for this installation."
      : "PowerShell will download ExchangeOnlineManagement from the configured PowerShell repository and install it for the current user.";
    if (!await confirm({ title, message, confirmLabel: "Install", mutating: true })) return;
    setBusy(id);
    setError(null);
    try {
      const result = await api.system.installDependency(id);
      if (!result.installed) setError(result.detail);
      await onChanged?.();
    } catch (e) { setError(errorText(e)); }
    finally { setBusy(null); }
  };

  const decline = async (id: "pwsh" | "exchange-module") => {
    setBusy(id);
    setError(null);
    try { await api.system.declineDependency(id); await onChanged?.(); }
    catch (e) { setError(errorText(e)); }
    finally { setBusy(null); }
  };

  return (
    <>
      {error && <Alert severity="error" onClose={() => setError(null)} sx={{ mb: 1 }}>{error}</Alert>}
      <Stack component="ul" spacing={0} sx={{ listStyle: "none", m: 0, p: 0 }} aria-label="Diagnostics checks">
        {checks.map((c, i) => (
          <Box
            component="li"
            key={c.id}
            sx={{ py: 1.5, borderTop: i === 0 ? 0 : 1, borderColor: "divider", minWidth: 0 }}
          >
            <Stack direction="row" spacing={1} useFlexGap sx={{ alignItems: "center", flexWrap: "wrap" }}>
              <Typography variant="subtitle2" component="span">
                {c.label}
              </Typography>
              <DiagnosticStatusChip status={c.status} />
            </Stack>
            {c.detail && (c.status === "Error" && c.fix?.installId
              ? <Alert severity="error" sx={{ mt: 1, overflowWrap: "anywhere" }}>{c.detail}</Alert>
              : <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5, overflowWrap: "anywhere" }}>
                  {c.detail}
                </Typography>)}
            {c.fix && needsAttention(c) && (
              <Box sx={{ mt: 1 }}>
                {c.fix.installId && (
                  <Stack direction="row" spacing={1} sx={{ mb: 1, flexWrap: "wrap" }}>
                    <Button variant="contained" size="small" disabled={busy !== null}
                      onClick={() => void install(c.fix!.installId!)}>
                      {busy === c.id ? "Installing..." : c.id === "pwsh" ? "Install PowerShell 7" : "Install Exchange module"}
                    </Button>
                    {c.status !== "Error" && (
                      <Button size="small" disabled={busy !== null} onClick={() => void decline(c.fix!.installId!)}>
                        Not now
                      </Button>
                    )}
                  </Stack>
                )}
                <QuestChip fix={c.fix} />
              </Box>
            )}
          </Box>
        ))}
      </Stack>
    </>
  );
}
