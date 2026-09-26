import { useRef, useState, type ReactNode } from "react";
import { Link as RouterLink } from "react-router";
import Accordion from "@mui/material/Accordion";
import AccordionDetails from "@mui/material/AccordionDetails";
import AccordionSummary from "@mui/material/AccordionSummary";
import Alert from "@mui/material/Alert";
import AlertTitle from "@mui/material/AlertTitle";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Card from "@mui/material/Card";
import CardContent from "@mui/material/CardContent";
import Chip from "@mui/material/Chip";
import Link from "@mui/material/Link";
import Stack from "@mui/material/Stack";
import Typography from "@mui/material/Typography";
import ContentCopy from "@mui/icons-material/ContentCopy";
import Download from "@mui/icons-material/Download";
import ExpandMore from "@mui/icons-material/ExpandMore";
import { api } from "../api";
import { copyText, selectContents } from "../clipboard";
import { pluralize, Timestamp } from "../format";
import { useToast } from "../hooks/useToast";
import { personPath } from "../paths";
import type { ChangeResult, OperationEvidence } from "../types";
import { Fact, FindingList, outcomeMeta, planItemLabel, SentenceList } from "./operationUi";

type ChangeState = "done" | "failed" | "in-place" | "skipped";

export function changeState(c: ChangeResult): ChangeState {
  if (c.attempted) return c.succeeded ? "done" : "failed";
  return c.succeeded ? "in-place" : "skipped";
}

const CHANGE_CHIP: Record<ChangeState, { label: string; color: "success" | "error" | "default"; variant: "filled" | "outlined" }> = {
  done: { label: "Done", color: "success", variant: "filled" },
  failed: { label: "Failed", color: "error", variant: "filled" },
  "in-place": { label: "Already in place", color: "success", variant: "outlined" },
  skipped: { label: "Skipped", color: "default", variant: "outlined" }
};

function Section({
  title,
  count,
  defaultExpanded,
  children
}: {
  title: string;
  count?: string;
  defaultExpanded: boolean;
  children: ReactNode;
}) {
  return (
    <Accordion defaultExpanded={defaultExpanded} disableGutters variant="outlined" sx={{ "&::before": { display: "none" } }}>
      <AccordionSummary expandIcon={<ExpandMore />}>
        <Typography variant="subtitle1" component="h3" sx={{ fontWeight: 600 }}>
          {title}
          {count && (
            <Typography component="span" variant="body2" color="text.secondary" sx={{ ml: 1 }}>
              {count}
            </Typography>
          )}
        </Typography>
      </AccordionSummary>
      <AccordionDetails sx={{ pt: 0 }}>{children}</AccordionDetails>
    </Accordion>
  );
}

function RowList({ children, label }: { children: ReactNode; label: string }) {
  return (
    <Box component="ul" aria-label={label} sx={{ listStyle: "none", m: 0, p: 0 }}>
      {children}
    </Box>
  );
}

function Row({ chip, title, detail }: { chip: ReactNode; title: ReactNode; detail?: ReactNode }) {
  return (
    <Box
      component="li"
      sx={{ display: "flex", gap: 1, alignItems: "flex-start", py: 0.75, borderBottom: 1, borderColor: "divider", "&:last-child": { borderBottom: 0 } }}
    >
      <Box sx={{ flexShrink: 0 }}>{chip}</Box>
      <Box sx={{ minWidth: 0 }}>
        <Typography variant="body2" sx={{ overflowWrap: "anywhere" }}>{title}</Typography>
        {detail && (
          <Typography variant="body2" color="text.secondary" sx={{ overflowWrap: "anywhere" }}>{detail}</Typography>
        )}
      </Box>
    </Box>
  );
}

/**
 * The durable record of one operation run: outcome, who/what/when, what was checked, planned,
 * changed and verified, and ticket-ready notes. Used after an apply and at /activity/runs/:runId.
 * Wording follows the outcome exactly -- nothing here says "succeeded" unless the server did.
 */
export function EvidencePanel({ evidence, title }: { evidence: OperationEvidence; title?: string }) {
  const toast = useToast();
  const notesRef = useRef<HTMLPreElement>(null);
  const [downloading, setDownloading] = useState<"md" | "json" | null>(null);
  const meta = outcomeMeta(evidence.outcome);

  const changes = evidence.changes ?? [];
  const states = changes.map(changeState);
  const attempted = states.filter((s) => s === "done" || s === "failed").length;
  const completed = states.filter((s) => s === "done").length;
  const skipped = states.filter((s) => s === "skipped" || s === "in-place").length;
  const verification = evidence.verification ?? [];
  const failedChecks = verification.filter((v) => !v.passed).length;
  const preflight = evidence.preflight ?? [];
  const plan = evidence.plan ?? [];
  const warnings = evidence.warnings ?? [];
  const failures = evidence.failures ?? [];
  const limitations = evidence.limitations ?? [];
  const target = evidence.target;

  const copyNotes = async () => {
    if (await copyText(evidence.ticketNotes)) {
      toast("Ticket notes copied", "success");
    } else {
      selectContents(notesRef.current);
      toast("Couldn't copy automatically. The notes are selected below; press Ctrl+C to copy them.", "warning");
    }
  };

  const download = async (kind: "md" | "json") => {
    setDownloading(kind);
    try {
      if (kind === "md") await api.workflows.evidenceMarkdown(evidence.runId);
      else await api.workflows.evidenceJson(evidence.runId);
    } catch (e) {
      toast(`Download failed: ${e instanceof Error ? e.message : String(e)}`, "error");
    } finally {
      setDownloading(null);
    }
  };

  return (
    <Stack spacing={2} component="section" aria-label={title ?? "Operation result"}>
      <Alert severity={meta.severity} variant="outlined" data-testid="evidence-outcome">
        <AlertTitle>{title ? `${title}: ${meta.label}` : meta.label}</AlertTitle>
        {meta.summary}
      </Alert>

      <Card variant="outlined">
        <CardContent>
          <Box sx={{ display: "grid", gridTemplateColumns: { xs: "1fr", sm: "repeat(3, minmax(0, 1fr))" }, gap: 1.5 }}>
            <Fact label="Operation">{evidence.operationName || evidence.operationId}</Fact>
            <Fact label="Tenant">
              {evidence.tenant?.id ? (
                <Link component={RouterLink} to={`/tenants/${evidence.tenant.id}`} underline="hover">
                  {evidence.tenant.displayName || evidence.tenant.tenantId}
                </Link>
              ) : (evidence.tenant?.displayName ?? "--")}
            </Fact>
            <Fact label="Target">
              {target ? (
                target.kind === "user" && evidence.tenant?.id && target.id ? (
                  <Link component={RouterLink} to={personPath(evidence.tenant.id, target.id)} underline="hover">
                    {target.displayName || target.id}
                  </Link>
                ) : (target.displayName || target.id)
              ) : "--"}
            </Fact>
            <Fact label="Operator">{evidence.operator || "--"}</Fact>
            <Fact label="Started"><Timestamp value={evidence.startedAt} /></Fact>
            <Fact label="Completed"><Timestamp value={evidence.completedAt} /></Fact>
          </Box>
        </CardContent>
      </Card>

      {failures.length > 0 && (
        <Alert severity="error">
          <AlertTitle>Failures</AlertTitle>
          <SentenceList items={failures} label="Failures" />
        </Alert>
      )}
      {warnings.length > 0 && (
        <Alert severity="warning">
          <AlertTitle>Warnings</AlertTitle>
          <SentenceList items={warnings} label="Warnings" />
        </Alert>
      )}

      <Card variant="outlined">
        <CardContent>
          <Stack
            direction={{ xs: "column", sm: "row" }}
            spacing={1}
            sx={{ justifyContent: "space-between", alignItems: { xs: "stretch", sm: "center" }, mb: 1 }}
          >
            <Typography variant="subtitle1" component="h3" sx={{ fontWeight: 600 }}>Ticket notes</Typography>
            <Stack direction="row" spacing={1} useFlexGap sx={{ flexWrap: "wrap" }}>
              <Button variant="contained" size="small" startIcon={<ContentCopy />} onClick={() => void copyNotes()} disabled={!evidence.ticketNotes}>
                Copy ticket notes
              </Button>
              {evidence.runId && (
                <>
                  <Button size="small" startIcon={<Download />} onClick={() => void download("md")} disabled={downloading !== null}>
                    Download Markdown
                  </Button>
                  <Button size="small" startIcon={<Download />} onClick={() => void download("json")} disabled={downloading !== null}>
                    Download JSON
                  </Button>
                </>
              )}
            </Stack>
          </Stack>
          {evidence.ticketNotes ? (
            <Box
              component="pre"
              ref={notesRef}
              aria-label="Ticket notes text"
              tabIndex={0}
              sx={{
                m: 0, p: 1.5, bgcolor: "action.hover", borderRadius: 1, fontFamily: "monospace", fontSize: "0.8125rem",
                whiteSpace: "pre-wrap", overflowWrap: "anywhere", maxHeight: 320, overflowY: "auto"
              }}
            >
              {evidence.ticketNotes}
            </Box>
          ) : (
            <Typography variant="body2" color="text.secondary">This run has no ticket notes.</Typography>
          )}
        </CardContent>
      </Card>

      {changes.length > 0 && (
        <Section
          title="Changes"
          count={`${attempted} attempted, ${completed} completed, ${skipped} skipped`}
          defaultExpanded
        >
          <RowList label="Changes">
            {changes.map((c, i) => {
              const chip = CHANGE_CHIP[states[i]];
              return (
                <Row
                  key={`${c.planItemId}-${i}`}
                  chip={<Chip size="small" label={chip.label} color={chip.color} variant={chip.variant} />}
                  title={planItemLabel(c)}
                  detail={c.detail}
                />
              );
            })}
          </RowList>
        </Section>
      )}

      {verification.length > 0 && (
        <Section
          title="Verification"
          count={failedChecks > 0 ? `${failedChecks} of ${verification.length} checks failed` : `${pluralize(verification.length, "check")} passed`}
          defaultExpanded
        >
          <RowList label="Verification checks">
            {verification.map((v, i) => (
              <Row
                key={`${v.name}-${i}`}
                chip={<Chip size="small" label={v.passed ? "Passed" : "Failed"} color={v.passed ? "success" : "error"} />}
                title={v.name}
                detail={v.detail}
              />
            ))}
          </RowList>
        </Section>
      )}

      {preflight.length > 0 && (
        <Section
          title="Preflight"
          count={pluralize(preflight.length, "check")}
          defaultExpanded={preflight.some((f) => f.status === "Warning" || f.status === "Blocker")}
        >
          <FindingList findings={preflight} label="Preflight checks" />
        </Section>
      )}

      {plan.length > 0 && (
        <Section title="Plan" count={pluralize(plan.length, "item")} defaultExpanded={false}>
          <RowList label="Plan items">
            {plan.map((p) => (
              <Row
                key={p.id}
                chip={
                  <Chip
                    size="small"
                    variant="outlined"
                    label={p.eligible ? (p.destructive ? "Destructive" : "Planned") : "Not run"}
                    color={p.eligible ? (p.destructive ? "error" : "primary") : "default"}
                  />
                }
                title={planItemLabel(p)}
                detail={p.reason}
              />
            ))}
          </RowList>
        </Section>
      )}

      {limitations.length > 0 && (
        <Alert severity="info" variant="outlined">
          <AlertTitle>Limitations</AlertTitle>
          <SentenceList items={limitations} label="Limitations" />
        </Alert>
      )}
    </Stack>
  );
}
