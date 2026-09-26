// Shared vocabulary for the operation/evidence model (spec section B): how outcomes, plan items,
// group categories and findings read on screen. One place, so Activity, the person workspace,
// Access Parity, offboarding and the evidence view never describe the same fact differently.
import type { ReactNode } from "react";
import Box from "@mui/material/Box";
import Chip from "@mui/material/Chip";
import Stack from "@mui/material/Stack";
import Typography from "@mui/material/Typography";
import { humanizeEnum } from "../format";
import type { Finding, Outcome, PlanItem, WorkflowRunRecord } from "../types";

type ChipColor = "success" | "warning" | "error" | "info" | "default";

interface OutcomeMeta {
  label: string;
  color: ChipColor;
  severity: "success" | "info" | "warning" | "error";
  /** One honest sentence: what this outcome does and does not mean. */
  summary: string;
}

export const OUTCOME_META: Record<Outcome, OutcomeMeta> = {
  Succeeded: {
    label: "Succeeded",
    color: "success",
    severity: "success",
    summary: "Every requested change was made and a re-read of the tenant confirmed it."
  },
  NoChangeNeeded: {
    label: "No change needed",
    color: "success",
    severity: "success",
    summary: "Everything requested was already in place. PCB changed nothing."
  },
  PartiallySucceeded: {
    label: "Partially succeeded",
    color: "warning",
    severity: "warning",
    summary: "Some changes were made and verified; others were not. Review the failures and skipped items before closing the ticket."
  },
  VerificationFailed: {
    label: "Verification failed",
    color: "error",
    severity: "error",
    summary: "Changes were reported as made, but re-reading the tenant did not confirm them. Check the tenant before telling anyone it is done."
  },
  Failed: {
    label: "Failed",
    color: "error",
    severity: "error",
    summary: "No requested change was verified. Review the failures below."
  },
  CompletedUnverified: {
    label: "Completed, not verified",
    color: "warning",
    severity: "warning",
    summary: "Microsoft accepted the requested changes, but PCB could not confirm their effect yet (for example a password value, or a device retire that is requested but not yet complete). Check the result yourself before closing the ticket."
  },
  Planned: {
    label: "Planned only",
    color: "info",
    severity: "info",
    summary: "A plan was produced. Nothing was changed."
  }
};

export function outcomeMeta(outcome: string | null | undefined): OutcomeMeta {
  return (outcome && OUTCOME_META[outcome as Outcome]) || {
    // A value from a newer server: show it as-is, neutrally, rather than guess what it means.
    label: outcome || "Unknown",
    color: "default",
    severity: "info",
    summary: "PCB did not record a recognised outcome for this run."
  };
}

export function OutcomeChip({ outcome, size = "small" }: { outcome: Outcome | string; size?: "small" | "medium" }) {
  const meta = outcomeMeta(outcome);
  return <Chip size={size} label={meta.label} color={meta.color} />;
}

/** Outcome (or the legacy ok/failed flag on older servers) plus the post-run health hint. */
export function RunResultChips({ run }: { run: WorkflowRunRecord }) {
  return (
    <Stack direction="row" spacing={0.5} useFlexGap sx={{ flexWrap: "wrap" }}>
      {run.outcome ? (
        <OutcomeChip outcome={run.outcome} />
      ) : (
        <Chip size="small" label={run.succeeded ? "ok" : "failed"} color={run.succeeded ? "success" : "error"} />
      )}
      {run.healthy !== null && run.healthy !== undefined && (
        <Chip size="small" label={run.healthy ? "healthy" : "needs fixing"} color={run.healthy ? "success" : "warning"} variant="outlined" />
      )}
    </Stack>
  );
}

// --- Group categories (Access Parity + person workspace) --------------------------------------

const CATEGORY_LABEL: Record<string, string> = {
  Security: "Security",
  Microsoft365: "Microsoft 365",
  MailEnabledSecurity: "Mail-enabled security",
  Distribution: "Distribution list",
  Dynamic: "Dynamic",
  RoleAssignable: "Role-assignable",
  OnPremSynced: "Synced from on-prem",
  DirectoryRole: "Directory role",
  AlreadyMember: "Already a member",
  Other: "Other"
};

export function categoryLabel(category: string): string {
  return CATEGORY_LABEL[category] ?? humanizeEnum(category);
}

/** Security and Microsoft 365 cloud groups are the ones PCB can change; the rest read as "hands off". */
export function CategoryChip({ category }: { category: string }) {
  const changeable = category === "Security" || category === "Microsoft365";
  return (
    <Chip
      size="small"
      variant="outlined"
      color={changeable ? "primary" : "default"}
      label={categoryLabel(category)}
      sx={{ maxWidth: "100%" }}
    />
  );
}

// --- Plan items ---------------------------------------------------------------------------------

const ACTION_LABEL: Record<string, string> = {
  AddMember: "Add to",
  AssignRole: "Assign role",
  BlockSignIn: "Block sign-in",
  RevokeSessions: "Revoke sessions",
  ConvertToShared: "Convert to shared mailbox",
  SetForwarding: "Forward mail to",
  HideFromGal: "Hide from address list",
  GrantManagerAccess: "Grant manager access",
  RemoveMember: "Remove from",
  RemoveLicense: "Remove license",
  RetireDevice: "Retire device",
  FollowUpReminder: "Follow-up reminder"
};

/** "Remove from: Finance Team", "Block sign-in: Ada Lovelace". */
export function planItemLabel(item: Pick<PlanItem, "action" | "objectName">): string {
  const action = ACTION_LABEL[item.action] ?? humanizeEnum(item.action);
  return item.objectName ? `${action}: ${item.objectName}` : action;
}

// --- Findings -----------------------------------------------------------------------------------

export const FINDING_COLOR: Record<Finding["status"], "success" | "info" | "warning" | "error"> = {
  Ok: "success", Info: "info", Warning: "warning", Blocker: "error"
};

/** Stacked (not tabular) so long details wrap on a phone instead of widening the page. */
export function FindingList({ findings, label }: { findings: Finding[]; label?: string }) {
  if (findings.length === 0) return null;
  return (
    <Box component="ul" aria-label={label} sx={{ listStyle: "none", m: 0, p: 0 }}>
      {findings.map((f, i) => (
        <Box
          component="li"
          key={`${f.name}-${i}`}
          sx={{ display: "flex", gap: 1, alignItems: "flex-start", py: 0.75, borderBottom: 1, borderColor: "divider", "&:last-child": { borderBottom: 0 } }}
        >
          <Chip size="small" label={f.status} color={FINDING_COLOR[f.status] ?? "default"} sx={{ flexShrink: 0, minWidth: 64 }} />
          <Box sx={{ minWidth: 0 }}>
            <Typography variant="body2" sx={{ fontWeight: 500 }}>{f.name}</Typography>
            {f.detail && (
              <Typography variant="body2" color="text.secondary" sx={{ overflowWrap: "anywhere" }}>{f.detail}</Typography>
            )}
          </Box>
        </Box>
      ))}
    </Box>
  );
}

/** A plain bulleted list of server-supplied sentences (warnings, limitations, failures). */
export function SentenceList({ items, label }: { items: string[]; label?: string }) {
  if (items.length === 0) return null;
  return (
    <Box component="ul" aria-label={label} sx={{ m: 0, pl: 2.5 }}>
      {items.map((s, i) => (
        <Typography component="li" variant="body2" key={i} sx={{ overflowWrap: "anywhere" }}>{s}</Typography>
      ))}
    </Box>
  );
}

/** Small label/value pair used in fact grids. */
export function Fact({ label, children }: { label: string; children: ReactNode }) {
  return (
    <Box sx={{ minWidth: 0 }}>
      <Typography variant="caption" color="text.secondary" component="div">{label}</Typography>
      <Typography variant="body2" component="div" sx={{ overflowWrap: "anywhere" }}>{children}</Typography>
    </Box>
  );
}
