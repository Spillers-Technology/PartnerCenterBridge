import { useEffect, useState } from "react";
import { authEnabled, initAuth, login, logout } from "./auth";
import { api } from "./api";
import { clearLocalToken, getLocalToken } from "./session";
import type { AuthMode, AuthResponse, MeProfile } from "./types";
import { Tenants } from "./components/Tenants";
import { Contracts } from "./components/Contracts";
import { AppTemplates } from "./components/AppTemplates";
import { DeployWizard } from "./components/DeployWizard";
import { Deployments } from "./components/Deployments";
import { NewHire } from "./components/NewHire";
import { Offboard } from "./components/Offboard";
import { Workflows } from "./components/Workflows";
import { Approvals } from "./components/Approvals";
import { Dashboard } from "./components/Dashboard";
import { UserSearch, type WorkflowLaunch } from "./components/UserSearch";
import { Login } from "./components/Login";
import { Register } from "./components/Register";
import { Security } from "./components/Security";
import { AppShell } from "./components/AppShell";
import { ConfigSnapshots } from "./components/ConfigSnapshots";

type Tab = "dashboard" | "finduser" | "tenants" | "contracts" | "templates" | "deploy" | "history" | "newhire" | "offboard" | "workflows" | "approvals" | "snapshots" | "security";

const TABS: { key: Tab; label: string }[] = [
  { key: "dashboard", label: "Dashboard" },
  { key: "finduser", label: "Find User" },
  { key: "tenants", label: "Tenants" },
  { key: "contracts", label: "Contracts" },
  { key: "templates", label: "App Templates" },
  { key: "deploy", label: "Deploy" },
  { key: "history", label: "History" },
  { key: "newhire", label: "New Hire" },
  { key: "offboard", label: "Offboard" },
  { key: "workflows", label: "Workflows" },
  { key: "approvals", label: "Approvals" },
  { key: "snapshots", label: "Config Snapshots" }
];

export function App() {
  const [tab, setTab] = useState<Tab>("dashboard");
  const [ready, setReady] = useState(false);
  const [user, setUser] = useState<string | null>(null);
  const [wfLaunch, setWfLaunch] = useState<WorkflowLaunch | null>(null);

  // Auth:Mode=Local state. authMode is fetched once from the API so the same published web
  // image works regardless of how a given deployment is configured -- no separate build per mode.
  const [authMode, setAuthMode] = useState<AuthMode | null>(null);
  const [me, setMe] = useState<MeProfile | null>(null);
  const [localScreen, setLocalScreen] = useState<"login" | "register">("login");

  const launchWorkflow = (l: WorkflowLaunch) => { setWfLaunch(l); setTab("workflows"); };

  useEffect(() => {
    api.auth.mode()
      .then(async (m) => {
        setAuthMode(m.mode);
        if (m.mode === "Local") {
          if (getLocalToken()) {
            try { setMe(await api.auth.me()); }
            catch { clearLocalToken(); }
          }
        } else {
          const u = await initAuth();
          setUser(u?.profile?.preferred_username ?? (authEnabled ? null : "local"));
        }
      })
      .finally(() => setReady(true));
  }, []);

  const onAuthenticated = (r: AuthResponse) => setMe(r.user);
  const refreshMe = () => api.auth.me().then(setMe).catch(() => {});

  useEffect(() => {
    if (authMode !== "Local" || !me) return;
    const refreshOnFocus = () => { void refreshMe(); };
    window.addEventListener("focus", refreshOnFocus);
    return () => window.removeEventListener("focus", refreshOnFocus);
    // Refresh against current database roles/grants whenever a Local operator returns to the app.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [authMode, me?.id]);
  const signOutLocal = async () => {
    try { await api.auth.logout(); } catch { /* best-effort */ }
    clearLocalToken();
    setMe(null);
    setLocalScreen("login");
  };

  if (!ready || authMode === null) return <div className="center">Loading…</div>;

  if (authMode === "Local" && !me) {
    return localScreen === "login"
      ? <Login onAuthenticated={onAuthenticated} onGoRegister={() => setLocalScreen("register")} />
      : <Register onAuthenticated={onAuthenticated} onGoLogin={() => setLocalScreen("login")} />;
  }

  if (authMode === "Oidc" && authEnabled && !user) {
    return (
      <div className="center">
        <h1>Partner Center Bridge</h1>
        <button onClick={login}>Sign in</button>
      </div>
    );
  }

  const displayName = me?.displayName ?? user;
  const allTabs = authMode === "Local" ? [...TABS, { key: "security" as Tab, label: "Security" }] : TABS;

  const showSignOut = authMode === "Local" || (authMode === "Oidc" && authEnabled);
  const handleSignOut = authMode === "Local" ? signOutLocal : logout;

  return (
    <AppShell
      tabs={allTabs}
      activeTab={tab}
      onSelectTab={(key) => setTab(key as Tab)}
      displayName={displayName ?? null}
      onSignOut={showSignOut ? handleSignOut : undefined}
    >
      {tab === "dashboard" && <Dashboard />}
      {tab === "finduser" && <UserSearch onLaunch={launchWorkflow} />}
      {tab === "tenants" && <Tenants me={me} onProfileChanged={refreshMe} />}
      {tab === "contracts" && <Contracts me={me} />}
      {tab === "templates" && <AppTemplates me={me} />}
      {tab === "deploy" && <DeployWizard />}
      {tab === "history" && <Deployments />}
      {tab === "newhire" && <NewHire />}
      {tab === "offboard" && <Offboard />}
      {tab === "workflows" && <Workflows prefill={wfLaunch} />}
      {tab === "approvals" && <Approvals />}
      {tab === "snapshots" && <ConfigSnapshots me={me} />}
      {tab === "security" && me && <Security me={me} onProfileChanged={refreshMe} />}
    </AppShell>
  );
}
