import { useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { Link as RouterLink, useLocation, useNavigate, useParams, useSearchParams } from "react-router";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Card from "@mui/material/Card";
import CardContent from "@mui/material/CardContent";
import Chip from "@mui/material/Chip";
import Link from "@mui/material/Link";
import List from "@mui/material/List";
import ListItemButton from "@mui/material/ListItemButton";
import ListItemText from "@mui/material/ListItemText";
import Skeleton from "@mui/material/Skeleton";
import Stack from "@mui/material/Stack";
import Tab from "@mui/material/Tab";
import Table from "@mui/material/Table";
import TableBody from "@mui/material/TableBody";
import TableCell from "@mui/material/TableCell";
import TableContainer from "@mui/material/TableContainer";
import TableHead from "@mui/material/TableHead";
import TableRow from "@mui/material/TableRow";
import Tabs from "@mui/material/Tabs";
import Typography from "@mui/material/Typography";
import { api, isApiStatus } from "../api";
import { Timestamp } from "../format";
import { AccessNotice } from "../components/AccessNotice";
import { PageHeader } from "../components/PageHeader";
import { PERSON_ACTIONS, UserSearch, type WorkflowLaunch } from "../components/UserSearch";
import { hasTenantRole, tenantRole } from "../permissions";
import type { GlobalUserHit, PersonWorkspace, Tenant, WorkflowRunRecord } from "../types";
import { useWorkbench } from "../workbench";

/** Deep link into a known fix with the tenant and target user already filled in. */
export function workflowLink(workflowId: string, tenantId?: string, user?: string) {
  const q = new URLSearchParams();
  if (tenantId) q.set("tenant", tenantId);
  if (user) q.set("user", user);
  const qs = q.toString();
  return `/operations/workflows/${encodeURIComponent(workflowId)}${qs ? `?${qs}` : ""}`;
}

export function personPath(tenantId: string, userId: string) {
  return `/people/${encodeURIComponent(tenantId)}/${encodeURIComponent(userId)}`;
}

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

/**
 * Workspace tabs. C2 adds licensing/auth/mailbox/devices once GET .../people/{userId} is live;
 * a tab only appears here when it has real content to show.
 */
const PERSON_TABS = [
  { key: "summary", label: "Summary" },
  { key: "history", label: "History" }
] as const;
type PersonTab = (typeof PERSON_TABS)[number]["key"];

function Fact({ label, children }: { label: string; children: ReactNode }) {
  return (
    <Box sx={{ minWidth: 0 }}>
      <Typography variant="caption" color="text.secondary" component="div">
        {label}
      </Typography>
      <Typography variant="body2" component="div" sx={{ overflowWrap: "anywhere" }}>
        {children}
      </Typography>
    </Box>
  );
}

/** /people/:tenantId/:userId -- one person in one tenant, with the actions that apply to them. */
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
  const [workspaceNote, setWorkspaceNote] = useState<string | null>(null);

  useEffect(() => {
    let alive = true;
    setTenant(undefined);
    api.tenants.list()
      .then((ts) => { if (alive) setTenant(ts.find((t) => t.id === tenantId) ?? null); })
      .catch(() => { if (alive) setTenant(null); });
    setWorkspace(null);
    setWorkspaceNote(null);
    api.people.get(tenantId, userId)
      .then((w) => { if (alive) setWorkspace(w); })
      .catch((e) => {
        if (!alive) return;
        setWorkspaceNote(isApiStatus(e, 404)
          ? null // an older server without the person workspace API -- the search hit is all we have
          : e instanceof Error ? e.message : String(e));
      });
    return () => { alive = false; };
  }, [tenantId, userId]);

  const profile = workspace?.profile.status === "Ok" ? workspace.profile.data ?? null : null;
  const displayName = profile?.displayName ?? knownHit?.displayName ?? null;
  const upn = profile?.upn ?? knownHit?.userPrincipalName ?? null;
  const tenantName = tenant?.displayName ?? knownHit?.tenantName ?? null;
  const role = tenantRole(me, tenantId);
  const canOperate = hasTenantRole(me, tenantId, "Operator");

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

  const userRef = upn ?? userId;

  return (
    <Box>
      <PageHeader
        parent={{ label: "People", to: "/people" }}
        title={displayName ?? upn ?? "Person"}
        meta={profile && !profile.accountEnabled ? <Chip size="small" color="warning" label="Sign-in blocked" /> : undefined}
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
        sx={{ mb: 2, borderBottom: 1, borderColor: "divider" }}
      >
        {PERSON_TABS.map((t) => <Tab key={t.key} value={t.key} label={t.label} />)}
      </Tabs>

      {workspaceNote && (
        <Alert severity="warning" sx={{ mb: 2 }}>
          Some details couldn't be loaded: {workspaceNote}
        </Alert>
      )}

      {tab === "summary" && (
        <Box sx={{ display: "grid", gridTemplateColumns: { xs: "1fr", md: "minmax(0, 3fr) minmax(0, 2fr)" }, gap: 2 }}>
          <Card variant="outlined">
            <CardContent>
              <Typography variant="h6" component="h3" gutterBottom>
                Summary
              </Typography>
              {!knownHit && !workspace && !workspaceNote && tenant === undefined ? (
                <Skeleton variant="rounded" height={80} />
              ) : (
                <Box sx={{ display: "grid", gridTemplateColumns: { xs: "1fr", sm: "repeat(2, minmax(0, 1fr))" }, gap: 1.5 }}>
                  <Fact label="Display name">{displayName ?? "--"}</Fact>
                  <Fact label="User principal name">{upn ?? "--"}</Fact>
                  <Fact label="Tenant">{tenantName ?? tenantId}</Fact>
                  <Fact label="Object id"><Box component="span" sx={{ fontFamily: "monospace" }}>{userId}</Box></Fact>
                  {profile && (
                    <>
                      <Fact label="Job title">{profile.jobTitle || "--"}</Fact>
                      <Fact label="Department">{profile.department || "--"}</Fact>
                      <Fact label="Sign-in">{profile.accountEnabled ? "Allowed" : "Blocked"}</Fact>
                      <Fact label="Source">{profile.onPremisesSyncEnabled ? "Synced from on-premises AD" : "Cloud only"}</Fact>
                      <Fact label="Created"><Timestamp value={profile.createdDateTime} /></Fact>
                      <Fact label="Last sign-in"><Timestamp value={profile.lastSignIn} fallback="Not readable" /></Fact>
                    </>
                  )}
                </Box>
              )}
              {!profile && !knownHit && (
                <Typography variant="body2" color="text.secondary" sx={{ mt: 2 }}>
                  Open this person from a <Link component={RouterLink} to="/people">People search</Link> to see their name and UPN here.
                </Typography>
              )}
            </CardContent>
          </Card>

          <Card variant="outlined">
            <CardContent>
              <Typography variant="h6" component="h3" gutterBottom>
                Actions
              </Typography>
              {!canOperate && (
                <AccessNotice title={role ? `You have ${role} access to this tenant` : "Read-only"}>
                  You can diagnose, but applying fixes or offboarding needs Operator. Ask a tenant Owner
                  to raise your role.
                </AccessNotice>
              )}
              <List dense disablePadding aria-label="Actions for this person">
                {PERSON_ACTIONS.map((a) => (
                  <ListItemButton key={a.workflowId} component={RouterLink} to={workflowLink(a.workflowId, tenantId, userRef)}>
                    <ListItemText primary={a.label} secondary="Diagnose first, then apply" />
                  </ListItemButton>
                ))}
                <ListItemButton
                  component={RouterLink}
                  to={`/operations/access-parity?tenant=${encodeURIComponent(tenantId)}&target=${encodeURIComponent(userId)}`}
                  disabled={!canOperate}
                >
                  <ListItemText primary="Mirror a colleague's access" secondary="Copy group memberships onto this person" />
                </ListItemButton>
                <ListItemButton
                  component={RouterLink}
                  to={`/operations/offboard?tenant=${encodeURIComponent(tenantId)}&user=${encodeURIComponent(userRef)}`}
                  disabled={!canOperate}
                >
                  <ListItemText primary="Offboard" secondary="Block sign-in, revoke sessions, remove licenses" />
                </ListItemButton>
              </List>
            </CardContent>
          </Card>
        </Box>
      )}

      {tab === "history" && <PersonHistory tenantId={tenantId} userId={userId} upn={upn} />}
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
  // inputs name this person.
  const mine = useMemo(() => {
    const ids = [userId, upn].filter(Boolean).map((v) => v!.toLowerCase());
    return (runs ?? []).filter((r) => Object.values(r.inputs ?? {}).some((v) => ids.includes(String(v).toLowerCase())));
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
    <TableContainer sx={{ overflowX: "auto" }}>
      <Table size="small" aria-label="Runs for this person">
        <TableHead>
          <TableRow>
            <TableCell>When</TableCell>
            <TableCell>Operation</TableCell>
            <TableCell>Kind</TableCell>
            <TableCell>Operator</TableCell>
            <TableCell>Result</TableCell>
          </TableRow>
        </TableHead>
        <TableBody>
          {mine.map((r) => (
            <TableRow key={r.id}>
              <TableCell><Timestamp value={r.startedAt} /></TableCell>
              <TableCell>
                <Link component={RouterLink} to={`/activity/runs/${r.id}`} underline="hover">{r.workflowName}</Link>
              </TableCell>
              <TableCell>{r.kind}</TableCell>
              <TableCell>{r.operator}</TableCell>
              <TableCell>
                <Stack direction="row" spacing={0.5}>
                  <Chip size="small" label={r.succeeded ? "ok" : "failed"} color={r.succeeded ? "success" : "error"} />
                </Stack>
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </TableContainer>
  );
}
