import { lazy, Suspense, useCallback, useEffect, useMemo, useState, type ReactNode } from "react";
import { Navigate, Outlet, Route, Routes, useLocation, useNavigate, type Location } from "react-router";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Stack from "@mui/material/Stack";
import Typography from "@mui/material/Typography";
import { authEnabled, initAuth, login, logout } from "./auth";
import { api } from "./api";
import { clearLocalToken, getLocalToken } from "./session";
import type { AuthMode, AuthResponse, MeProfile, SystemStatus } from "./types";
import { AppShell } from "./components/AppShell";
import { Login } from "./components/Login";
import { Register } from "./components/Register";
import { CommandPalette, CommandPaletteTrigger, useCommandPaletteShortcut } from "./components/CommandPalette";
import { PageLoading, RouteErrorBoundary } from "./components/RouteFallback";
import { WorkbenchProvider, useWorkbench, type WorkbenchSession } from "./workbench";

// Each top-level area is its own chunk: the shell and sign-in screens load first, and an area's
// screens (and the MUI components only they use) download the first time it is opened.
const Home = lazy(() => import("./routes/home"));
const PeopleSearchPage = lazy(() => import("./routes/people").then((m) => ({ default: m.PeopleSearchPage })));
const PersonWorkspacePage = lazy(() => import("./routes/people").then((m) => ({ default: m.PersonWorkspacePage })));
const TenantsPage = lazy(() => import("./routes/tenants").then((m) => ({ default: m.TenantsPage })));
const TenantWorkspacePage = lazy(() => import("./routes/tenants").then((m) => ({ default: m.TenantWorkspacePage })));
const OperationsCatalog = lazy(() => import("./routes/operations").then((m) => ({ default: m.OperationsCatalog })));
const OnboardPage = lazy(() => import("./routes/operations").then((m) => ({ default: m.OnboardPage })));
const OffboardPage = lazy(() => import("./routes/operations").then((m) => ({ default: m.OffboardPage })));
const DeployPage = lazy(() => import("./routes/operations").then((m) => ({ default: m.DeployPage })));
const WorkflowsPage = lazy(() => import("./routes/operations").then((m) => ({ default: m.WorkflowsPage })));
const AccessParityPage = lazy(() => import("./routes/operations").then((m) => ({ default: m.AccessParityPage })));
const ContractsPage = lazy(() => import("./routes/operations").then((m) => ({ default: m.ContractsPage })));
const TemplatesPage = lazy(() => import("./routes/operations").then((m) => ({ default: m.TemplatesPage })));
const ActivityLayout = lazy(() => import("./routes/activity").then((m) => ({ default: m.ActivityLayout })));
const ActivityHistory = lazy(() => import("./routes/activity").then((m) => ({ default: m.ActivityHistory })));
const ApprovalsPage = lazy(() => import("./routes/activity").then((m) => ({ default: m.ApprovalsPage })));
const RunDetailPage = lazy(() => import("./routes/activity").then((m) => ({ default: m.RunDetailPage })));
const SettingsOverview = lazy(() => import("./routes/settings").then((m) => ({ default: m.SettingsOverview })));
const SecuritySettingsPage = lazy(() => import("./routes/settings").then((m) => ({ default: m.SecuritySettingsPage })));
const MicrosoftSettingsPage = lazy(() => import("./routes/settings").then((m) => ({ default: m.MicrosoftSettingsPage })));
const WorkbenchSettingsPage = lazy(() => import("./routes/settings").then((m) => ({ default: m.WorkbenchSettingsPage })));
const NotFound = lazy(() => import("./routes/not-found"));

/** Where an unauthenticated visitor was headed, carried through /login and /register. */
interface ReturnState { from?: string }

const OIDC_RETURN_KEY = "pcb.oidc.returnTo";

function returnTarget(location: Location): string {
  const from = (location.state as ReturnState | null)?.from;
  // Only same-app paths: never bounce to /login or /register again, never an absolute URL.
  if (from && from.startsWith("/") && !from.startsWith("//") && !/^\/(login|register)(\/|\?|$)/.test(from)) return from;
  return "/";
}

export function App() {
  const [ready, setReady] = useState(false);
  const [bootError, setBootError] = useState<string | null>(null);
  const [bootAttempt, setBootAttempt] = useState(0);
  const [user, setUser] = useState<string | null>(null);
  const [status, setStatus] = useState<SystemStatus | null>(null);
  const navigate = useNavigate();

  // Auth:Mode=Local state. authMode is fetched once from the API so the same published web
  // image works regardless of how a given deployment is configured -- no separate build per mode.
  const [authMode, setAuthMode] = useState<AuthMode | null>(null);
  const [me, setMe] = useState<MeProfile | null>(null);

  useEffect(() => {
    let cancelled = false;
    setBootError(null);
    // /api/system/status is optional (0.9.0+); an older server answers 404 and first-run
    // detection simply doesn't apply.
    Promise.all([api.auth.mode(), api.system.status().catch(() => null)])
      .then(async ([m, s]) => {
        if (cancelled) return;
        setStatus(s);
        setAuthMode(m.mode);
        if (m.mode === "Local") {
          if (getLocalToken()) {
            try { setMe(await api.auth.me()); }
            catch { clearLocalToken(); }
          }
        } else {
          const returning = window.location.search.includes("code=");
          const u = await initAuth();
          setUser(u?.profile?.preferred_username ?? (authEnabled ? null : "local"));
          if (returning) {
            // Back from the identity provider: go where the user was originally headed.
            const back = sessionStorage.getItem(OIDC_RETURN_KEY);
            sessionStorage.removeItem(OIDC_RETURN_KEY);
            navigate(back && back.startsWith("/") && !back.startsWith("//") ? back : "/", { replace: true });
          }
        }
      })
      .catch((e) => { if (!cancelled) setBootError(e instanceof Error ? e.message : String(e)); })
      .finally(() => { if (!cancelled) setReady(true); });
    return () => { cancelled = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [bootAttempt]);

  const refreshMe = useCallback(() => api.auth.me().then(setMe).catch(() => {}), []);

  useEffect(() => {
    if (authMode !== "Local" || !me) return;
    const refreshOnFocus = () => { void refreshMe(); };
    window.addEventListener("focus", refreshOnFocus);
    return () => window.removeEventListener("focus", refreshOnFocus);
    // Refresh against current database roles/grants whenever a Local operator returns to the app.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [authMode, me?.id]);

  const signOutLocal = useCallback(async () => {
    try { await api.auth.logout(); } catch { /* best-effort */ }
    clearLocalToken();
    setMe(null);
    navigate("/login", { replace: true });
  }, [navigate]);

  const onAuthenticated = useCallback((r: AuthResponse) => {
    setMe(r.user);
    // Whoever just registered is no longer waiting on a first user.
    setStatus((s) => (s && s.needsFirstUser ? { ...s, needsFirstUser: false } : s));
  }, []);

  const session = useMemo<WorkbenchSession | null>(() => {
    if (authMode === null) return null;
    const showSignOut = authMode === "Local" || (authMode === "Oidc" && authEnabled);
    return {
      authMode,
      me,
      displayName: me?.displayName ?? user,
      status,
      refreshMe,
      signOut: showSignOut ? (authMode === "Local" ? () => void signOutLocal() : () => void logout()) : undefined
    };
  }, [authMode, me, user, status, refreshMe, signOutLocal]);

  if (bootError) {
    return (
      <Box sx={{ display: "grid", placeItems: "center", minHeight: "100vh", p: 2 }}>
        <Stack spacing={2} sx={{ maxWidth: 440 }}>
          <Typography variant="h5" component="h1">Partner Center Bridge</Typography>
          <Alert severity="error">
            Couldn't reach the Partner Center Bridge API: {bootError}
          </Alert>
          <Typography variant="body2" color="text.secondary">
            Check that the API is running and reachable from this browser, then try again.
          </Typography>
          <Box>
            <Button variant="contained" onClick={() => { setReady(false); setBootAttempt((n) => n + 1); }}>
              Try again
            </Button>
          </Box>
        </Stack>
      </Box>
    );
  }

  if (!ready || session === null) return <div className="center">Loading…</div>;

  const needsFirstUser = authMode === "Local" && Boolean(status?.needsFirstUser);

  return (
    <WorkbenchProvider value={session}>
      <Routes>
        <Route
          path="/login"
          element={
            <LocalAuthRoute authMode={session.authMode} signedIn={me !== null} redirectTo={needsFirstUser ? "/register" : null}>
              <LoginScreen onAuthenticated={onAuthenticated} />
            </LocalAuthRoute>
          }
        />
        <Route
          path="/register"
          element={
            <LocalAuthRoute authMode={session.authMode} signedIn={me !== null} redirectTo={null}>
              <RegisterScreen onAuthenticated={onAuthenticated} setup={needsFirstUser} />
            </LocalAuthRoute>
          }
        />
        <Route element={<RequireAuth user={user} needsFirstUser={needsFirstUser}><ShellLayout /></RequireAuth>}>
          <Route index element={<Home />} />
          <Route path="people" element={<PeopleSearchPage />} />
          <Route path="people/:tenantId/:userId" element={<PersonWorkspacePage />} />
          <Route path="tenants" element={<TenantsPage />} />
          <Route path="tenants/:tenantId" element={<TenantWorkspacePage />} />
          <Route path="operations" element={<OperationsCatalog />} />
          <Route path="operations/onboard" element={<OnboardPage />} />
          <Route path="operations/offboard" element={<OffboardPage />} />
          <Route path="operations/deploy" element={<DeployPage />} />
          <Route path="operations/access-parity" element={<AccessParityPage />} />
          <Route path="operations/workflows" element={<WorkflowsPage />} />
          <Route path="operations/workflows/:workflowId" element={<WorkflowsPage />} />
          <Route path="operations/contracts" element={<ContractsPage />} />
          <Route path="operations/templates" element={<TemplatesPage />} />
          <Route path="activity" element={<ActivityLayout />}>
            <Route index element={<ActivityHistory />} />
            <Route path="approvals" element={<ApprovalsPage />} />
          </Route>
          <Route path="activity/runs/:runId" element={<RunDetailPage />} />
          <Route path="settings" element={<SettingsOverview />} />
          <Route path="settings/security" element={<SecuritySettingsPage />} />
          <Route path="settings/microsoft" element={<MicrosoftSettingsPage />} />
          <Route path="settings/workbench" element={<WorkbenchSettingsPage />} />
          <Route path="*" element={<NotFound />} />
        </Route>
      </Routes>
    </WorkbenchProvider>
  );
}

/** /login and /register only exist under Auth:Mode=Local, and only while signed out. */
function LocalAuthRoute({
  authMode,
  signedIn,
  redirectTo,
  children
}: {
  authMode: AuthMode;
  signedIn: boolean;
  redirectTo: string | null;
  children: ReactNode;
}) {
  const location = useLocation();
  if (authMode !== "Local") return <Navigate to="/" replace />;
  if (signedIn) return <Navigate to={returnTarget(location)} replace />;
  if (redirectTo) return <Navigate to={redirectTo} replace state={location.state} />;
  return <>{children}</>;
}

function LoginScreen({ onAuthenticated }: { onAuthenticated: (r: AuthResponse) => void }) {
  const location = useLocation();
  const navigate = useNavigate();
  return (
    <Login
      onAuthenticated={(r) => {
        onAuthenticated(r);
        navigate(returnTarget(location), { replace: true });
      }}
      onGoRegister={() => navigate("/register", { state: location.state })}
    />
  );
}

function RegisterScreen({ onAuthenticated, setup }: { onAuthenticated: (r: AuthResponse) => void; setup: boolean }) {
  const location = useLocation();
  const navigate = useNavigate();
  return (
    <Register
      setup={setup}
      onAuthenticated={(r) => {
        onAuthenticated(r);
        navigate(returnTarget(location), { replace: true });
      }}
      onGoLogin={() => navigate("/login", { state: location.state })}
    />
  );
}

/**
 * Gate for every workbench route. A signed-out Local user is sent to /login (or /register on a
 * brand-new workbench) carrying the requested URL, and returns to it after signing in.
 */
function RequireAuth({ user, needsFirstUser, children }: { user: string | null; needsFirstUser: boolean; children: ReactNode }) {
  const { authMode, me } = useWorkbench();
  const location = useLocation();
  const from = `${location.pathname}${location.search}${location.hash}`;

  if (authMode === "Local" && !me) {
    return <Navigate to={needsFirstUser ? "/register" : "/login"} replace state={{ from } satisfies ReturnState} />;
  }

  if (authMode === "Oidc" && authEnabled && !user) {
    return (
      <Box sx={{ display: "grid", placeItems: "center", minHeight: "100vh", p: 2 }}>
        <Stack spacing={2} sx={{ alignItems: "flex-start" }}>
          <Typography variant="h5" component="h1">Partner Center Bridge</Typography>
          <Button
            variant="contained"
            onClick={() => {
              sessionStorage.setItem(OIDC_RETURN_KEY, from);
              void login();
            }}
          >
            Sign in
          </Button>
        </Stack>
      </Box>
    );
  }

  return <>{children}</>;
}

/** Pending approvals drive the Activity badge; refreshed on a timer and when entering/leaving Activity. */
function usePendingApprovals(): number {
  const location = useLocation();
  const inActivity = location.pathname.startsWith("/activity");
  const [count, setCount] = useState(0);

  useEffect(() => {
    let alive = true;
    const load = () =>
      api.pendingActions.list()
        .then((items) => { if (alive) setCount(items.filter((i) => i.status === "Pending").length); })
        .catch(() => { /* the badge is a hint; Activity itself reports load errors */ });
    void load();
    const timer = window.setInterval(() => void load(), 60_000);
    return () => { alive = false; window.clearInterval(timer); };
  }, [inActivity]);

  return count;
}

function ShellLayout() {
  const { displayName, signOut } = useWorkbench();
  const location = useLocation();
  const pending = usePendingApprovals();
  const [paletteOpen, setPaletteOpen] = useState(false);
  useCommandPaletteShortcut(setPaletteOpen);
  return (
    <AppShell
      displayName={displayName}
      onSignOut={signOut}
      badges={{ activity: pending }}
      headerActions={<CommandPaletteTrigger onOpen={() => setPaletteOpen(true)} />}
    >
      <RouteErrorBoundary resetKey={location.pathname}>
        <Suspense fallback={<PageLoading />}>
          <Outlet />
        </Suspense>
      </RouteErrorBoundary>
      <CommandPalette open={paletteOpen} onClose={() => setPaletteOpen(false)} />
    </AppShell>
  );
}
