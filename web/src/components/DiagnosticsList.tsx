import Box from "@mui/material/Box";
import Chip from "@mui/material/Chip";
import Stack from "@mui/material/Stack";
import Typography from "@mui/material/Typography";
import type { DiagnosticCheck, DiagnosticStatus } from "../types";
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
export function DiagnosticsList({ checks }: { checks: DiagnosticCheck[] }) {
  return (
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
          {c.detail && (
            <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5, overflowWrap: "anywhere" }}>
              {c.detail}
            </Typography>
          )}
          {c.fix && needsAttention(c) && (
            <Box sx={{ mt: 1 }}>
              <QuestChip fix={c.fix} />
            </Box>
          )}
        </Box>
      ))}
    </Stack>
  );
}
