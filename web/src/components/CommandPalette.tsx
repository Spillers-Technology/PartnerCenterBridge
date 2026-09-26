import { useEffect, useId, useMemo, useRef, useState, type KeyboardEvent } from "react";
import { useNavigate } from "react-router";
import Box from "@mui/material/Box";
import ButtonBase from "@mui/material/ButtonBase";
import Dialog from "@mui/material/Dialog";
import IconButton from "@mui/material/IconButton";
import InputBase from "@mui/material/InputBase";
import Typography from "@mui/material/Typography";
import { visuallyHidden } from "@mui/utils";
import SearchIcon from "@mui/icons-material/Search";
import { api } from "../api";
import { Timestamp } from "../format";
import { useIsPhone } from "../hooks/useIsPhone";
import { accessParityPath, offboardPath, onboardPath, peopleSearchPath, runPath } from "../paths";
import type { Tenant, WorkflowRunRecord, WorkflowSummary } from "../types";
import { outcomeMeta } from "./operationUi";

export interface Command {
  id: string;
  group: string;
  label: string;
  to: string;
  /** Extra words that should match (not shown). */
  keywords?: string;
  hint?: React.ReactNode;
}

const NAVIGATION: Command[] = [
  { id: "nav-home", group: "Go to", label: "Home", to: "/", keywords: "dashboard start" },
  { id: "nav-people", group: "Go to", label: "People", to: "/people", keywords: "users search person" },
  { id: "nav-tenants", group: "Go to", label: "Tenants", to: "/tenants", keywords: "customers" },
  { id: "nav-operations", group: "Go to", label: "Operations", to: "/operations", keywords: "catalog" },
  { id: "nav-activity", group: "Go to", label: "Activity", to: "/activity", keywords: "history runs audit" },
  { id: "nav-approvals", group: "Go to", label: "Approvals", to: "/activity/approvals", keywords: "pending mcp" },
  { id: "nav-fixes", group: "Go to", label: "Known fixes", to: "/operations/workflows", keywords: "workflows" },
  { id: "nav-contracts", group: "Go to", label: "Contracts", to: "/operations/contracts", keywords: "standards offboarding policy" },
  { id: "nav-templates", group: "Go to", label: "App templates", to: "/operations/templates", keywords: "packages win32" },
  { id: "nav-settings", group: "Go to", label: "Settings", to: "/settings" },
  { id: "nav-security", group: "Go to", label: "Security settings", to: "/settings/security", keywords: "passkey totp mfa account" },
  { id: "nav-microsoft", group: "Go to", label: "Microsoft connection", to: "/settings/microsoft", keywords: "sam partner center" },
  { id: "nav-workbench", group: "Go to", label: "Workbench health", to: "/settings/workbench", keywords: "diagnostics exchange doctor" }
];

const OPERATIONS: Command[] = [
  { id: "op-onboard", group: "Operations", label: "New hire", to: onboardPath(), keywords: "onboard create user" },
  { id: "op-offboard", group: "Operations", label: "Offboard", to: offboardPath(), keywords: "leaver terminate" },
  { id: "op-parity", group: "Operations", label: "Mirror access", to: accessParityPath(), keywords: "access parity copy groups" },
  { id: "op-deploy", group: "Operations", label: "Deploy app", to: "/operations/deploy", keywords: "intune" }
];

const MAX_RESULTS = 40;

function matches(c: Command, words: string[]): boolean {
  const hay = `${c.label} ${c.group} ${c.keywords ?? ""}`.toLowerCase();
  return words.every((w) => hay.includes(w));
}

/** The commands for a query: "Find person" first, then everything that matches every typed word. */
export function buildCommands(
  query: string,
  data: { tenants: Tenant[]; workflows: WorkflowSummary[]; runs: WorkflowRunRecord[] }
): Command[] {
  const q = query.trim();
  const words = q.toLowerCase().split(/\s+/).filter(Boolean);
  const dynamic: Command[] = [
    ...data.tenants.map((t) => ({ id: `tenant-${t.id}`, group: "Tenants", label: `Open tenant: ${t.displayName}`, to: `/tenants/${t.id}`, keywords: t.defaultDomain })),
    ...data.workflows.map((w) => ({ id: `fix-${w.id}`, group: "Known fixes", label: `Run known fix: ${w.name}`, to: `/operations/workflows/${w.id}`, keywords: `${w.category} ${w.id}` })),
    ...data.runs.map((r) => ({
      id: `run-${r.id}`,
      group: "Recent runs",
      label: `${r.workflowName} - ${r.targetDisplayName || r.targetId || r.tenantName}`,
      to: runPath(r.id),
      keywords: `${r.tenantName} ${r.outcome ?? ""} evidence`,
      hint: <>{r.outcome ? outcomeMeta(r.outcome).label : r.succeeded ? "ok" : "failed"} · <Timestamp value={r.startedAt} /></>
    }))
  ];
  const all = [...NAVIGATION, ...OPERATIONS, ...dynamic];
  const found = words.length === 0
    ? [...NAVIGATION.slice(0, 6), ...OPERATIONS, ...dynamic.filter((c) => c.group === "Recent runs")]
    : all.filter((c) => matches(c, words));
  const person: Command[] = q
    ? [{ id: "find-person", group: "People", label: `Find person: ${q}`, to: peopleSearchPath(q) }]
    : [];
  return [...person, ...found].slice(0, MAX_RESULTS);
}

/**
 * Keyboard-first jump list (Ctrl+K / Cmd+K): areas and pages, "find person", tenants, operations,
 * known fixes and recent runs. Arrow keys move, Enter opens, Esc closes.
 */
export function CommandPalette({ open, onClose }: { open: boolean; onClose: () => void }) {
  const navigate = useNavigate();
  const isPhone = useIsPhone();
  const titleId = useId();
  const listId = useId();
  const [query, setQuery] = useState("");
  const [active, setActive] = useState(0);
  const [tenants, setTenants] = useState<Tenant[]>([]);
  const [workflows, setWorkflows] = useState<WorkflowSummary[]>([]);
  const [runs, setRuns] = useState<WorkflowRunRecord[]>([]);
  const loadedStatic = useRef(false);
  const listRef = useRef<HTMLUListElement>(null);

  useEffect(() => {
    if (!open) return;
    setQuery("");
    setActive(0);
    let alive = true;
    // Tenants and the fix catalog change rarely: once per session. Recent runs: every open.
    if (!loadedStatic.current) {
      loadedStatic.current = true;
      api.tenants.list().then((t) => { if (alive) setTenants(t); }).catch(() => { loadedStatic.current = false; });
      api.workflows.list().then((w) => { if (alive) setWorkflows(w); }).catch(() => { loadedStatic.current = false; });
    }
    api.workflows.runs({ take: 5 }).then((r) => { if (alive) setRuns(r.slice(0, 5)); }).catch(() => {});
    return () => { alive = false; };
  }, [open]);

  const commands = useMemo(() => buildCommands(query, { tenants, workflows, runs }), [query, tenants, workflows, runs]);
  const activeIndex = Math.min(active, Math.max(commands.length - 1, 0));

  useEffect(() => {
    listRef.current?.querySelector<HTMLElement>(`[data-index="${activeIndex}"]`)?.scrollIntoView?.({ block: "nearest" });
  }, [activeIndex]);

  const choose = (c: Command | undefined) => {
    if (!c) return;
    onClose();
    navigate(c.to);
  };

  const onKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === "ArrowDown") {
      e.preventDefault();
      setActive((i) => (commands.length === 0 ? 0 : (Math.min(i, commands.length - 1) + 1) % commands.length));
    } else if (e.key === "ArrowUp") {
      e.preventDefault();
      setActive((i) => (commands.length === 0 ? 0 : (Math.min(i, commands.length - 1) - 1 + commands.length) % commands.length));
    } else if (e.key === "Home" && e.ctrlKey) {
      e.preventDefault();
      setActive(0);
    } else if (e.key === "End" && e.ctrlKey) {
      e.preventDefault();
      setActive(Math.max(commands.length - 1, 0));
    } else if (e.key === "Enter") {
      e.preventDefault();
      choose(commands[activeIndex]);
    }
  };

  const optionId = (i: number) => `${listId}-opt-${i}`;
  let lastGroup = "";

  return (
    <Dialog
      open={open}
      onClose={onClose}
      fullScreen={isPhone}
      fullWidth
      maxWidth="sm"
      aria-labelledby={titleId}
      slotProps={{ paper: { sx: { alignSelf: { sm: "flex-start" }, mt: { sm: 10 } } } }}
    >
      <Typography id={titleId} component="h2" sx={visuallyHidden}>Command palette</Typography>
      <Box sx={{ display: "flex", alignItems: "center", gap: 1, px: 2, py: 1.25, borderBottom: 1, borderColor: "divider" }}>
        <SearchIcon color="action" />
        <InputBase
          autoFocus
          fullWidth
          placeholder="Jump to a page, person, tenant or fix..."
          value={query}
          onChange={(e) => { setQuery(e.target.value); setActive(0); }}
          onKeyDown={onKeyDown}
          inputProps={{
            role: "combobox",
            "aria-label": "Search commands",
            "aria-expanded": true,
            "aria-controls": listId,
            "aria-autocomplete": "list",
            "aria-activedescendant": commands.length > 0 ? optionId(activeIndex) : undefined
          }}
        />
        {isPhone && (
          <IconButton aria-label="Close" onClick={onClose} edge="end" size="small">
            <Typography component="span" variant="caption">Esc</Typography>
          </IconButton>
        )}
      </Box>
      <Box
        component="ul"
        id={listId}
        ref={listRef}
        role="listbox"
        aria-label="Commands"
        sx={{ listStyle: "none", m: 0, p: 1, maxHeight: { xs: "none", sm: 420 }, overflowY: "auto" }}
      >
        {commands.length === 0 && (
          <Box component="li" role="presentation" sx={{ px: 1.5, py: 2 }}>
            <Typography variant="body2" color="text.secondary">No matches. Try a person's name, a tenant, or a page.</Typography>
          </Box>
        )}
        {commands.map((c, i) => {
          const header = c.group !== lastGroup ? c.group : null;
          lastGroup = c.group;
          const selected = i === activeIndex;
          return (
            <Box component="li" role="presentation" key={c.id}>
              {header && (
                <Typography variant="overline" color="text.secondary" component="div" sx={{ px: 1.5, pt: i === 0 ? 0 : 1, lineHeight: 2 }} aria-hidden>
                  {header}
                </Typography>
              )}
              <Box
                id={optionId(i)}
                role="option"
                aria-selected={selected}
                data-index={i}
                onMouseMove={() => { if (!selected) setActive(i); }}
                onClick={() => choose(c)}
                sx={{
                  px: 1.5, py: 1, borderRadius: 1, cursor: "pointer", display: "flex", gap: 1, alignItems: "baseline",
                  justifyContent: "space-between", flexWrap: "wrap",
                  bgcolor: selected ? "action.selected" : "transparent"
                }}
              >
                <Typography variant="body2" sx={{ overflowWrap: "anywhere", minWidth: 0 }}>{c.label}</Typography>
                {c.hint && <Typography variant="caption" color="text.secondary">{c.hint}</Typography>}
              </Box>
            </Box>
          );
        })}
      </Box>
      <Box sx={{ display: { xs: "none", sm: "flex" }, gap: 2, px: 2, py: 1, borderTop: 1, borderColor: "divider", color: "text.secondary", typography: "caption" }}>
        <span>Up/Down to move</span><span>Enter to open</span><span>Esc to close</span>
      </Box>
    </Dialog>
  );
}

/** Opens the palette on Ctrl+K / Cmd+K anywhere in the app (the shortcut never types a character). */
export function useCommandPaletteShortcut(setOpen: (update: (open: boolean) => boolean) => void) {
  useEffect(() => {
    const onKey = (e: globalThis.KeyboardEvent) => {
      if (e.defaultPrevented || e.altKey || e.shiftKey) return;
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") {
        e.preventDefault();
        setOpen((o) => !o);
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [setOpen]);
}

/** The shell's search affordance: a wide "search" field on larger screens, an icon on phones. */
export function CommandPaletteTrigger({ onOpen }: { onOpen: () => void }) {
  const isPhone = useIsPhone();
  const isMac = typeof navigator !== "undefined" && /Mac|iPhone|iPad/.test(navigator.platform ?? "");
  if (isPhone) {
    return (
      <IconButton aria-label="Search and jump (Ctrl+K)" onClick={onOpen}>
        <SearchIcon />
      </IconButton>
    );
  }
  return (
    <ButtonBase
      onClick={onOpen}
      aria-label="Search and jump (Ctrl+K)"
      aria-keyshortcuts="Control+K Meta+K"
      sx={{
        display: "flex", alignItems: "center", gap: 1, px: 1.5, py: 0.75, mr: 1, minWidth: { sm: 200, md: 260 },
        border: 1, borderColor: "divider", borderRadius: 2, color: "text.secondary", justifyContent: "flex-start",
        "&:hover": { borderColor: "text.secondary" }
      }}
    >
      <SearchIcon fontSize="small" />
      <Typography variant="body2" sx={{ flex: 1, textAlign: "left" }}>Search or jump to...</Typography>
      <Box component="kbd" sx={{ fontFamily: "inherit", fontSize: "0.75rem", border: 1, borderColor: "divider", borderRadius: 1, px: 0.5 }}>
        {isMac ? "Cmd K" : "Ctrl K"}
      </Box>
    </ButtonBase>
  );
}
