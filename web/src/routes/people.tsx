import { useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { Link as RouterLink, useLocation, useNavigate, useParams, useSearchParams } from "react-router";
import Alert from "@mui/material/Alert";
import AlertTitle from "@mui/material/AlertTitle";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Card from "@mui/material/Card";
import CardActionArea from "@mui/material/CardActionArea";
import CardContent from "@mui/material/CardContent";
import Chip from "@mui/material/Chip";
import Link from "@mui/material/Link";
import List from "@mui/material/List";
import ListItemButton from "@mui/material/ListItemButton";
import ListItemText from "@mui/material/ListItemText";
import Skeleton from "@mui/material/Skeleton";
import Stack from "@mui/material/Stack";
import Tab from "@mui/material/Tab";
import Tabs from "@mui/material/Tabs";
import Typography from "@mui/material/Typography";
import { api, isApiStatus } from "../api";
import { humanizeEnum, pluralize, Timestamp } from "../format";
import { AccessNotice } from "../components/AccessNotice";
import { CategoryChip, categoryLabel, Fact, OutcomeChip } from "../components/operationUi";
import { PageHeader } from "../components/PageHeader";
import { QuestChip } from "../components/QuestChip";
import { RunList } from "../components/RunList";
import { UserSearch, type WorkflowLaunch } from "../components/UserSearch";
import { accessParityPath, offboardPath, personPath, runPath, workflowLink } from "../paths";
import { tenantRole } from "../permissions";
import type {
  DiagnosticFix, GlobalUserHit, PersonWorkspace, Tenant, TenantRole, WorkflowRunRecord, WorkflowSummary, WorkspaceSection
} from "../types";
import { useWorkbench } from "../workbench";

export { personPath, workflowLink };

/** /people?q= -- cross-tenant person search; the query lives in the URL so it survives refresh/back. */
export function PeopleSearchPage() {
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  const q = params.get("q") ?? "";

  // UserSearch owns its input and runs a ?q= search once on mount. A search submitted here pushes
  // its query into the URL (lastPushed); any *other* change of ?q= (back/forward, a link from
  // Home) remounts the search with the new query instead.
  const lastPushed = useRef(q);
  const [mountKey, setMountKey] = useState(0);
  useEffect(() => {
    if (q !== lastPushed.current) {
      lastPushed.current = q;
      setMountKey((k) => k + 1);
    }
  }, [q]);

  return (
    <UserSearch
      key={mountKey}
      title="People"
      initialQuery={q}
      onSearched={(query) => {
        if (query === lastPushed.current) return;
        lastPushed.current = query;
        setParams({ q: query });
      }}
      personPath={(h) => personPath(h.tenantId, h.id)}
      onLaunch={(l: WorkflowLaunch) => navigate(workflowLink(l.workflowId, l.tenantId, l.inputs.userUpn))}
    />
  );
}

/** Workspace tabs, in the order a technician works through a ticket. Keys are the ?tab= values. */
export const PERSON_TABS = [
  { key: "summary", label: "Summary" },
  { key: "access", label: "Access" },
  { key: "licensing", label: "Licensing" },
  { key: "auth", label: "Authentication" },
  { key: "mailbox", label: "Mailbox" },
  { key: "devices", label: "Devices" },
  { key: "history", label: "History" }
] as const;
type PersonTab = (typeof PERSON_TABS)[number]["key"];

// --- Section states ------------------------------------------------------------------------------

/** A fix to offer next to an Unavailable reason, when the reason names something PCB can set up. */
function fixFor(reason: string | null | undefined): DiagnosticFix | null {
  if (!reason) return null;
  if (/exchange|powershell|pwsh|module/i.test(reason)) return { label: "Check workbench setup", route: "/settings/workbench" };
  if (/permission|consent|lacks|403|scope/i.test(reason)) return { label: "Review the Microsoft connection", route: "/settings/microsoft" };
  return null;
}

function Unavailable({ what, reason }: { what: string; reason?: string | null }) {
  const fix = fixFor(reason);
  return (
    <Alert severity="info" variant="outlined">
      <AlertTitle>{what} unavailable</AlertTitle>
      <Typography variant="body2" sx={{ overflowWrap: "anywhere" }}>
        {reason || "PCB can't read this here."}
      </Typography>
      {fix && <Box sx={{ mt: 1 }}><QuestChip fix={fix} /></Box>}
    </Alert>
  );
}

interface WorkspaceLoad {
  workspace: PersonWorkspace | null;
  /** The whole workspace request failed (not one section). */
  error: string | null;
  /** The server predates the person workspace API. */
  unsupported: boolean;
  retry: () => void;
}

/**
 * One workspace section: Ok data, an Unavailable reason (with a fix when there is one), or an Error
 * with retry. Never renders placeholder data in place of something PCB could not read.
 */
function SectionView<T>({
  load,
  section,
  what,
  children
}: {
  load: WorkspaceLoad;
  section: (w: PersonWorkspace) => WorkspaceSection<T>;
  what: string;
  children: (data: T | null, note: string | null) => ReactNode;
}) {
  if (load.unsupported) {
    return (
      <AccessNotice title={`${what} needs a newer server`}>
        This server predates the person workspace (Partner Center Bridge 0.9.0), so PCB can't show {what.toLowerCase()} here.
      </AccessNotice>
    );
  }
  if (load.error) {
    return (
      <Alert severity="error" action={<Button color="inherit" size="small" onClick={load.retry}>Retry</Button>}>
        Couldn't load this person: {load.error}
      </Alert>
    );
  }
  if (!load.workspace) return <Skeleton variant="rounded" height={96} aria-label={`Loading ${what.toLowerCase()}`} />;
  const s = section(load.workspace);
  if (s.status === "Unavailable") return <Unavailable what={what} reason={s.reason} />;
  if (s.status === "Error") {
    return (
      <Alert severity="error" action={<Button color="inherit" size="small" onClick={load.retry}>Retry</Button>}>
        <AlertTitle>Couldn't read {what.toLowerCase()}</AlertTitle>
        <Typography variant="body2" sx={{ overflowWrap: "anywhere" }}>{s.reason || "The read failed."}</Typography>
      </Alert>
    );
  }
  return <>{children(s.data ?? null, s.reason ?? null)}</>;
}

// --- Formatting ----------------------------------------------------------------------------------

const AUTH_METHOD_LABEL: Record<string, string> = {
  password: "Password",
  microsoftAuthenticator: "Microsoft Authenticator",
  phone: "Phone (SMS/voice)",
  fido2: "FIDO2 security key",
  windowsHelloForBusiness: "Windows Hello for Business",
  email: "Email (SSPR)",
  softwareOath: "Authenticator app (OATH)",
  hardwareOath: "Hardware token (OATH)",
  temporaryAccessPass: "Temporary Access Pass",
  platformCredential: "Platform credential (macOS)",
  qrCodePin: "QR code PIN",
  passwordless: "Passwordless phone sign-in"
};
const authMethodLabel = (m: string) => AUTH_METHOD_LABEL[m] ?? humanizeEnum(m);
/** Everything that counts as a second factor (password and email-for-SSPR do not). */
const mfaMethods = (methods: string[]) => methods.filter((m) => !["password", "email"].includes(m));

function complianceColor(state?: string | null): "success" | "error" | "warning" | "default" {
  switch ((state ?? "").toLowerCase()) {
    case "compliant": return "success";
    case "noncompliant": return "error";
    case "ingraceperiod": case "conflict": case "error": return "warning";
    default: return "default";
  }
}

// --- Actions -------------------------------------------------------------------------------------

interface PersonAction {
  key: string;
  label: string;
  detail: string;
  to: string;
  /** Disrupts the person's work (sign-in, credentials, access); flagged so it is never a casual click. */
  disruptive: boolean;
  /** Present only when the server has this known fix. */
  workflowId?: string;
}

function personActions(tenantId: string, userRef: string): PersonAction[] {
  if (!userRef) return [];
  return [
    { key: "parity", label: "Mirror access from another user", detail: "Add this person to a colleague's groups. Additive only.", to: accessParityPath({ tenant: tenantId, target: userRef }), disruptive: false },
    { key: "offboard", label: "Offboard", detail: "Block sign-in, revoke sessions, remove licenses and groups per the contract's policy.", to: offboardPath(tenantId, userRef), disruptive: true },
    { key: "mfa-reset", workflowId: "mfa-reset", label: "Reset MFA", detail: "Clear registered methods so they register again.", to: workflowLink("mfa-reset", tenantId, userRef), disruptive: true },
    { key: "password-reset", workflowId: "password-reset", label: "Reset password", detail: "Temporary password and session revoke.", to: workflowLink("password-reset", tenantId, userRef), disruptive: true },
    { key: "compromised-lockdown", workflowId: "compromised-lockdown", label: "Compromised account lockdown", detail: "Block sign-in, revoke sessions, disable risky inbox rules.", to: workflowLink("compromised-lockdown", tenantId, userRef), disruptive: true },
    { key: "license-repair", workflowId: "license-repair", label: "License repair", detail: "Fix a license that won't apply.", to: workflowLink("license-repair", tenantId, userRef), disruptive: false },
    { key: "mailbox-archive", workflowId: "mailbox-archive", label: "Mailbox archive fix", detail: "Enable archiving and retention processing.", to: workflowLink("mailbox-archive", tenantId, userRef), disruptive: false }
  ];
}

function ActionsPanel({
  actions,
  role,
  tenantName
}: {
  actions: PersonAction[];
  role: TenantRole | null;
  tenantName: string;
}) {
  const canOperate = role === "Operator" || role === "Owner";
  return (
    <Card variant="outlined" sx={{ minWidth: 0 }}>
      <CardContent>
        <Typography variant="h6" component="h3" gutterBottom>Actions</Typography>
        {!canOperate && (
          <AccessNotice title={role ? `You have ${role} access to ${tenantName}` : "Read-only"}>
            You can preview and diagnose, but applying any of these needs Operator. Ask one of the
            tenant's Owners to raise your role.
          </AccessNotice>
        )}
        <List dense disablePadding aria-label="Actions for this person">
          {actions.map((a) => (
            <ListItemButton key={a.key} component={RouterLink} to={a.to} sx={{ borderRadius: 1, alignItems: "flex-start", px: 1 }}>
              <ListItemText
                primary={
                  <Stack direction="row" spacing={1} useFlexGap sx={{ alignItems: "center", flexWrap: "wrap" }}>
                    <span>{a.label}</span>
                    {a.disruptive && <Chip size="small" color="warning" variant="outlined" label="Disruptive" />}
                  </Stack>
                }
                secondary={canOperate ? a.detail : `${a.detail} Preview only: applying needs Operator.`}
              />
            </ListItemButton>
          ))}
        </List>
      </CardContent>
    </Card>
  );
}

// --- Summary -------------------------------------------------------------------------------------

type Tone = "default" | "success" | "warning" | "error";

function Glance({ label, value, tone = "default", tab }: { label: string; value: ReactNode; tone?: Tone; tab?: PersonTab }) {
  const body = (
    <CardContent sx={{ py: 1.25, "&:last-child": { pb: 1.25 } }}>
      <Typography variant="caption" color="text.secondary" component="div">{label}</Typography>
      <Typography
        variant="body2"
        component="div"
        sx={{ fontWeight: 600, overflowWrap: "anywhere", color: tone === "default" ? "text.primary" : `${tone}.main` }}
      >
        {value}
      </Typography>
    </CardContent>
  );
  return (
    <Card variant="outlined" sx={{ minWidth: 0 }}>
      {tab ? <CardActionArea component={RouterLink} to={`?tab=${tab}`} sx={{ height: "100%" }}>{body}</CardActionArea> : body}
    </Card>
  );
}

/** "Unavailable"/"Couldn't read" in a glance tile, so a missing count never reads as zero. */
function glanceOf<T>(w: PersonWorkspace | null, pick: (w: PersonWorkspace) => WorkspaceSection<T>, ok: (d: T | null) => { value: ReactNode; tone?: Tone }) {
  if (!w) return { value: <Skeleton width={60} />, tone: "default" as Tone };
  const s = pick(w);
  if (s.status === "Unavailable") return { value: "Unavailable", tone: "default" as Tone };
  if (s.status === "Error") return { value: "Couldn't read", tone: "error" as Tone };
  return ok(s.data ?? null);
}

function SummaryGlance({ workspace }: { workspace: PersonWorkspace | null }) {
  const account = glanceOf(workspace, (w) => w.profile, (p) =>
    p?.accountEnabled === false ? { value: "Sign-in blocked", tone: "warning" }
      : p?.accountEnabled === true ? { value: "Enabled", tone: "success" }
        : { value: "Unknown" });
  const source = glanceOf(workspace, (w) => w.profile, (p) =>
    p?.onPremisesSyncEnabled ? { value: "Synced from on-prem", tone: "warning" } : { value: "Cloud only" });
  const licenses = glanceOf(workspace, (w) => w.licenses, (l) => ({ value: pluralize(l?.length ?? 0, "license") }));
  const groups = glanceOf(workspace, (w) => w.groups, (g) => ({ value: pluralize(g?.length ?? 0, "group") }));
  const mfa = glanceOf(workspace, (w) => w.authMethods, (m) => {
    const second = mfaMethods(m ?? []);
    return second.length === 0
      ? { value: "No MFA method", tone: "warning" }
      : { value: `${pluralize(second.length, "method")}: ${second.map(authMethodLabel).join(", ")}` };
  });
  const mailbox = glanceOf(workspace, (w) => w.mailbox, (m) =>
    m ? { value: humanizeEnum(m.recipientTypeDetails) } : { value: "No mailbox found" });
  const devices = glanceOf(workspace, (w) => w.devices, (d) => ({ value: pluralize(d?.length ?? 0, "managed device") }));
  const last = glanceOf(workspace, (w) => w.recentRuns, (runs) => {
    const r = runs?.[0];
    if (!r) return { value: "None yet" };
    return {
      value: (
        <Stack direction="row" spacing={0.75} useFlexGap sx={{ alignItems: "center", flexWrap: "wrap" }}>
          <span>{r.workflowName}</span>
          {r.outcome ? <OutcomeChip outcome={r.outcome} /> : <Chip size="small" label={r.succeeded ? "ok" : "failed"} color={r.succeeded ? "success" : "error"} />}
          <Typography component="span" variant="caption" color="text.secondary"><Timestamp value={r.startedAt} /></Typography>
        </Stack>
      )
    };
  });

  return (
    <Box
      aria-label="At a glance"
      role="group"
      sx={{ display: "grid", gridTemplateColumns: { xs: "repeat(2, minmax(0, 1fr))", sm: "repeat(4, minmax(0, 1fr))" }, gap: 1, mb: 2 }}
    >
      <Glance label="Account" {...account} />
      <Glance label="Directory" {...source} />
      <Glance label="Licenses" {...licenses} tab="licensing" />
      <Glance label="Groups" {...groups} tab="access" />
      <Glance label="MFA" {...mfa} tab="auth" />
      <Glance label="Mailbox" {...mailbox} tab="mailbox" />
      <Glance label="Devices" {...devices} tab="devices" />
      <Glance label="Last PCB operation" {...last} tab="history" />
    </Box>
  );
}

// --- Page ----------------------------------------------------------------------------------------

/** /people/:tenantId/:userId?tab= -- one person in one tenant: what they have, and what to do about it. */
export function PersonWorkspacePage() {
  const { tenantId = "", userId = "" } = useParams();
  const [params, setParams] = useSearchParams();
  const location = useLocation();
  const { me } = useWorkbench();
  const requestedTab = params.get("tab");
  const tab: PersonTab = PERSON_TABS.some((t) => t.key === requestedTab) ? (requestedTab as PersonTab) : "summary";

  // The search hit that led here, if any (lost on refresh -- the workspace API then fills in).
  const hit = (location.state as { hit?: GlobalUserHit } | null)?.hit;
  const knownHit = hit && hit.tenantId === tenantId && hit.id === userId ? hit : undefined;

  const [tenant, setTenant] = useState<Tenant | null | undefined>(undefined);
  const [workspace, setWorkspace] = useState<PersonWorkspace | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [unsupported, setUnsupported] = useState(false);
  const [attempt, setAttempt] = useState(0);
  const [catalog, setCatalog] = useState<WorkflowSummary[] | null>(null);

  useEffect(() => {
    let alive = true;
    setTenant(undefined);
    api.tenants.list()
      .then((ts) => { if (alive) setTenant(ts.find((t) => t.id === tenantId) ?? null); })
      .catch(() => { if (alive) setTenant(null); });
    api.workflows.list()
      .then((w) => { if (alive) setCatalog(w); })
      .catch(() => { /* offer every action; the workflow page explains a missing one */ });
    return () => { alive = false; };
  }, [tenantId]);

  useEffect(() => {
    let alive = true;
    setWorkspace(null);
    setLoadError(null);
    setUnsupported(false);
    api.people.get(tenantId, userId)
      .then((w) => { if (alive) setWorkspace(w); })
      .catch((e) => {
        if (!alive) return;
        // 404 from an older server without the workspace API (a real "no such user" comes back
        // as a 200 whose profile section is an Error).
        if (isApiStatus(e, 404)) setUnsupported(true);
        else setLoadError(e instanceof Error ? e.message : String(e));
      });
    return () => { alive = false; };
  }, [tenantId, userId, attempt]);

  const load: WorkspaceLoad = { workspace, error: loadError, unsupported, retry: () => setAttempt((n) => n + 1) };
  const profile = workspace?.profile.status === "Ok" ? workspace.profile.data ?? null : null;
  const displayName = profile?.displayName ?? knownHit?.displayName ?? null;
  const upn = profile?.upn ?? knownHit?.userPrincipalName ?? null;
  const tenantName = tenant?.displayName ?? knownHit?.tenantName ?? null;
  const role = tenantRole(me, tenantId);
  const objectId = profile?.id ?? workspace?.userId ?? userId;
  const userRef = upn ?? objectId;

  const actions = useMemo(() => {
    const all = personActions(tenantId, userRef);
    if (!catalog) return all;
    return all.filter((a) => !a.workflowId || catalog.some((w) => w.id === a.workflowId));
  }, [tenantId, userRef, catalog]);

  if (me !== null && role === null && tenant === null) {
    return (
      <Box>
        <PageHeader title="Person" parent={{ label: "People", to: "/people" }} />
        <AccessNotice title="This tenant isn't shared with you">
          You don't have access to the tenant this person belongs to, so PCB can't show or act on
          them. Ask one of the tenant's Owners to share it with you.
        </AccessNotice>
      </Box>
    );
  }

  const actionLink = (key: string) => actions.find((a) => a.key === key);

  return (
    <Box>
      <PageHeader
        parent={{ label: "People", to: "/people" }}
        title={displayName ?? upn ?? "Person"}
        meta={profile && (
          <>
            {profile.accountEnabled === false && <Chip size="small" color="warning" label="Sign-in blocked" />}
            {profile.accountEnabled === true && <Chip size="small" color="success" variant="outlined" label="Enabled" />}
            {profile.onPremisesSyncEnabled && <Chip size="small" color="warning" variant="outlined" label="Synced from on-prem" />}
          </>
        )}
        subtitle={
          <>
            {upn && <Box component="span" sx={{ fontFamily: "monospace", overflowWrap: "anywhere" }}>{upn}</Box>}
            {upn && tenantName && " in "}
            {tenantName ? (
              <Link component={RouterLink} to={`/tenants/${tenantId}`} underline="hover">{tenantName}</Link>
            ) : tenant === undefined ? "" : !upn ? `Tenant ${tenantId}` : ""}
          </>
        }
      />

      <Tabs
        value={tab}
        onChange={(_, v: PersonTab) => setParams(v === "summary" ? {} : { tab: v })}
        aria-label="Person sections"
        variant="scrollable"
        scrollButtons="auto"
        allowScrollButtonsMobile
        sx={{ mb: 2, borderBottom: 1, borderColor: "divider" }}
      >
        {PERSON_TABS.map((t) => <Tab key={t.key} value={t.key} label={t.label} />)}
      </Tabs>

      {profile?.onPremisesSyncEnabled && tab === "summary" && (
        <Alert severity="warning" variant="outlined" sx={{ mb: 2 }}>
          <AlertTitle>Hybrid account</AlertTitle>
          This person is synced from on-premises Active Directory. Blocking sign-in, changing their name or
          removing them from synced groups has to be done on-premises as well, or the next sync undoes it.
        </Alert>
      )}

      {tab === "summary" && (
        <>
          {!unsupported && !loadError && <SummaryGlance workspace={workspace} />}
          <Box sx={{ display: "grid", gridTemplateColumns: { xs: "1fr", md: "minmax(0, 3fr) minmax(0, 2fr)" }, gap: 2 }}>
            <Card variant="outlined" sx={{ minWidth: 0 }}>
              <CardContent>
                <Typography variant="h6" component="h3" gutterBottom>Profile</Typography>
                {unsupported || loadError ? (
                  <>
                    <Box sx={{ display: "grid", gridTemplateColumns: { xs: "1fr", sm: "repeat(2, minmax(0, 1fr))" }, gap: 1.5, mb: 2 }}>
                      <Fact label="Display name">{displayName ?? "--"}</Fact>
                      <Fact label="User principal name">{upn ?? "--"}</Fact>
                      <Fact label="Tenant">{tenantName ?? tenantId}</Fact>
                      <Fact label="Object id"><Box component="span" sx={{ fontFamily: "monospace" }}>{userId}</Box></Fact>
                    </Box>
                    <SectionView load={load} section={(w) => w.profile} what="Profile">{() => null}</SectionView>
                  </>
                ) : (
                  <SectionView load={load} section={(w) => w.profile} what="Profile">
                    {(p) => p && (
                      <Box sx={{ display: "grid", gridTemplateColumns: { xs: "1fr", sm: "repeat(2, minmax(0, 1fr))" }, gap: 1.5 }}>
                        <Fact label="Display name">{p.displayName}</Fact>
                        <Fact label="User principal name">{p.upn ?? "--"}</Fact>
                        <Fact label="Mail">{p.mail || "--"}</Fact>
                        <Fact label="Tenant">{tenantName ?? tenantId}</Fact>
                        <Fact label="Job title">{p.jobTitle || "--"}</Fact>
                        <Fact label="Department">{p.department || "--"}</Fact>
                        <Fact label="Sign-in">{p.accountEnabled === false ? "Blocked" : p.accountEnabled ? "Allowed" : "Unknown"}</Fact>
                        <Fact label="Source">{p.onPremisesSyncEnabled ? "Synced from on-premises AD" : "Cloud only"}</Fact>
                        <Fact label="Created"><Timestamp value={p.createdDateTime} /></Fact>
                        <Fact label="Last sign-in"><Timestamp value={p.lastSignIn} fallback="Not readable" /></Fact>
                        <Fact label="Object id"><Box component="span" sx={{ fontFamily: "monospace" }}>{p.id}</Box></Fact>
                      </Box>
                    )}
                  </SectionView>
                )}
              </CardContent>
            </Card>
            <ActionsPanel actions={actions} role={role} tenantName={tenantName ?? "this tenant"} />
          </Box>
        </>
      )}

      {tab === "access" && (
        <SectionView load={load} section={(w) => w.groups} what="Group memberships">
          {(groups) => (
            <Card variant="outlined">
              <CardContent>
                <Stack direction={{ xs: "column", sm: "row" }} spacing={1} sx={{ justifyContent: "space-between", alignItems: { sm: "center" }, mb: 1 }}>
                  <Typography variant="h6" component="h3">
                    Direct memberships
                    <Typography component="span" variant="body2" color="text.secondary" sx={{ ml: 1 }}>{pluralize(groups?.length ?? 0, "group")}</Typography>
                  </Typography>
                  {actionLink("parity") && (
                    <Button component={RouterLink} to={actionLink("parity")!.to} size="small" variant="outlined">Mirror access from another user</Button>
                  )}
                </Stack>
                {!groups || groups.length === 0 ? (
                  <Typography variant="body2" color="text.secondary">Not a direct member of any group or role.</Typography>
                ) : (
                  <Box component="ul" aria-label="Group memberships" sx={{ listStyle: "none", m: 0, p: 0 }}>
                    {[...groups].sort((a, b) => categoryLabel(a.category).localeCompare(categoryLabel(b.category)) || a.displayName.localeCompare(b.displayName)).map((g) => (
                      <Box component="li" key={g.id} sx={{ display: "flex", gap: 1, alignItems: "center", py: 0.75, borderBottom: 1, borderColor: "divider", "&:last-child": { borderBottom: 0 } }}>
                        <Typography variant="body2" sx={{ flex: 1, minWidth: 0, overflowWrap: "anywhere" }}>{g.displayName}</Typography>
                        <CategoryChip category={g.category} />
                      </Box>
                    ))}
                  </Box>
                )}
              </CardContent>
            </Card>
          )}
        </SectionView>
      )}

      {tab === "licensing" && (
        <SectionView load={load} section={(w) => w.licenses} what="Licenses">
          {(licenses) => (
            <Card variant="outlined">
              <CardContent>
                <Stack direction={{ xs: "column", sm: "row" }} spacing={1} sx={{ justifyContent: "space-between", alignItems: { sm: "center" }, mb: 1 }}>
                  <Typography variant="h6" component="h3">Assigned licenses</Typography>
                  {actionLink("license-repair") && (
                    <Button component={RouterLink} to={actionLink("license-repair")!.to} size="small" variant="outlined">License repair</Button>
                  )}
                </Stack>
                {!licenses || licenses.length === 0 ? (
                  <Typography variant="body2" color="text.secondary">No licenses assigned.</Typography>
                ) : (
                  <Box component="ul" aria-label="Licenses" sx={{ listStyle: "none", m: 0, p: 0 }}>
                    {licenses.map((l) => (
                      <Box component="li" key={l.skuId} sx={{ py: 0.75, borderBottom: 1, borderColor: "divider", "&:last-child": { borderBottom: 0 } }}>
                        <Typography variant="body2" sx={{ fontWeight: 500 }}>{l.skuPartNumber}</Typography>
                        <Typography variant="caption" color="text.secondary" sx={{ fontFamily: "monospace", overflowWrap: "anywhere" }}>{l.skuId}</Typography>
                      </Box>
                    ))}
                  </Box>
                )}
              </CardContent>
            </Card>
          )}
        </SectionView>
      )}

      {tab === "auth" && (
        <SectionView load={load} section={(w) => w.authMethods} what="Authentication methods">
          {(methods) => {
            const second = mfaMethods(methods ?? []);
            return (
              <Card variant="outlined">
                <CardContent>
                  <Stack direction={{ xs: "column", sm: "row" }} spacing={1} sx={{ justifyContent: "space-between", alignItems: { sm: "center" }, mb: 1 }}>
                    <Typography variant="h6" component="h3">Registered methods</Typography>
                    {actionLink("mfa-reset") && (
                      <Button component={RouterLink} to={actionLink("mfa-reset")!.to} size="small" variant="outlined" color="warning">Reset MFA</Button>
                    )}
                  </Stack>
                  {second.length === 0 && (
                    <Alert severity="warning" sx={{ mb: 1 }}>No second factor is registered. This person signs in with a password alone.</Alert>
                  )}
                  {methods && methods.length > 0 ? (
                    <Stack direction="row" spacing={1} useFlexGap sx={{ flexWrap: "wrap" }} aria-label="Authentication methods" role="list">
                      {methods.map((m) => <Chip key={m} role="listitem" label={authMethodLabel(m)} variant="outlined" />)}
                    </Stack>
                  ) : (
                    <Typography variant="body2" color="text.secondary">No methods registered.</Typography>
                  )}
                  <Typography variant="caption" color="text.secondary" component="p" sx={{ mt: 1.5 }}>
                    Method types only; PCB never reads phone numbers or keys.
                  </Typography>
                </CardContent>
              </Card>
            );
          }}
        </SectionView>
      )}

      {tab === "mailbox" && (
        <SectionView load={load} section={(w) => w.mailbox} what="Mailbox">
          {(mailbox, note) => (
            <Card variant="outlined">
              <CardContent>
                <Stack direction={{ xs: "column", sm: "row" }} spacing={1} sx={{ justifyContent: "space-between", alignItems: { sm: "center" }, mb: 1 }}>
                  <Typography variant="h6" component="h3">Mailbox</Typography>
                  {actionLink("mailbox-archive") && (
                    <Button component={RouterLink} to={actionLink("mailbox-archive")!.to} size="small" variant="outlined">Mailbox archive fix</Button>
                  )}
                </Stack>
                {mailbox ? (
                  <Box sx={{ display: "grid", gridTemplateColumns: { xs: "1fr", sm: "repeat(2, minmax(0, 1fr))" }, gap: 1.5 }}>
                    <Fact label="Type">{humanizeEnum(mailbox.recipientTypeDetails)}</Fact>
                    <Fact label="Address">{mailbox.userPrincipalName}</Fact>
                    <Fact label="Forwarding">{mailbox.forwardingSmtpAddress || "None"}</Fact>
                    <Fact label="Keep a copy when forwarding">{mailbox.forwardingSmtpAddress ? (mailbox.deliverToMailboxAndForward ? "Yes" : "No") : "--"}</Fact>
                  </Box>
                ) : (
                  <Typography variant="body2" color="text.secondary">{note ?? "Exchange Online returned no mailbox for this person."}</Typography>
                )}
              </CardContent>
            </Card>
          )}
        </SectionView>
      )}

      {tab === "devices" && (
        <SectionView load={load} section={(w) => w.devices} what="Managed devices">
          {(devices) => (
            !devices || devices.length === 0 ? (
              <Typography variant="body2" color="text.secondary">No Intune-managed devices for this person.</Typography>
            ) : (
              <Box component="ul" aria-label="Managed devices" sx={{ listStyle: "none", m: 0, p: 0, display: "grid", gridTemplateColumns: { xs: "1fr", sm: "repeat(2, minmax(0, 1fr))" }, gap: 1.5 }}>
                {devices.map((d) => (
                  <Card component="li" variant="outlined" key={d.id} sx={{ minWidth: 0 }}>
                    <CardContent>
                      <Stack direction="row" spacing={1} useFlexGap sx={{ alignItems: "center", flexWrap: "wrap", mb: 1 }}>
                        <Typography variant="subtitle1" component="h3" sx={{ fontWeight: 600, overflowWrap: "anywhere" }}>{d.deviceName || "Unnamed device"}</Typography>
                        {d.complianceState && <Chip size="small" label={humanizeEnum(d.complianceState)} color={complianceColor(d.complianceState)} />}
                      </Stack>
                      <Box sx={{ display: "grid", gridTemplateColumns: "repeat(2, minmax(0, 1fr))", gap: 1 }}>
                        <Fact label="OS">{[d.operatingSystem, d.osVersion].filter(Boolean).join(" ") || "--"}</Fact>
                        <Fact label="Last sync"><Timestamp value={d.lastSyncDateTime} /></Fact>
                        <Fact label="Managed by">{d.managementAgent ? humanizeEnum(d.managementAgent) : "--"}</Fact>
                      </Box>
                    </CardContent>
                  </Card>
                ))}
              </Box>
            )
          )}
        </SectionView>
      )}

      {tab === "history" && <PersonHistory tenantId={tenantId} userId={objectId} upn={upn} />}
    </Box>
  );
}

function PersonHistory({ tenantId, userId, upn }: { tenantId: string; userId: string; upn: string | null }) {
  const [runs, setRuns] = useState<WorkflowRunRecord[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let alive = true;
    api.workflows.runs({ tenantId, targetId: userId, take: 50 })
      .then((r) => { if (alive) setRuns(r); })
      .catch((e) => { if (alive) setError(e instanceof Error ? e.message : String(e)); });
    return () => { alive = false; };
  }, [tenantId, userId]);

  // An older server ignores targetId and returns every run in the tenant; keep only the runs whose
  // recorded target or inputs name this person.
  const mine = useMemo(() => {
    const ids = [userId, upn].filter(Boolean).map((v) => v!.toLowerCase());
    return (runs ?? []).filter((r) =>
      (r.targetId && ids.includes(r.targetId.toLowerCase())) ||
      Object.values(r.inputs ?? {}).some((v) => ids.includes(String(v).toLowerCase())));
  }, [runs, userId, upn]);

  if (error) return <Alert severity="error">{error}</Alert>;
  if (runs === null) return <Skeleton variant="rounded" height={120} />;
  if (mine.length === 0) {
    return (
      <Typography variant="body2" color="text.secondary">
        No PCB runs have targeted this person yet.
      </Typography>
    );
  }
  return (
    <>
      <RunList runs={mine} label="Runs for this person" showTarget={false} showTenant={false} />
      {mine[0] && (
        <Typography variant="body2" sx={{ mt: 1 }}>
          <Link component={RouterLink} to={runPath(mine[0].id)} underline="hover">Open the latest run's evidence</Link>
        </Typography>
      )}
    </>
  );
}

