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
import { Dashboard } from "./components/Dashboard";
import { UserSearch, type WorkflowLaunch } from "./components/UserSearch";
import { Login } from "./components/Login";
import { Register } from "./components/Register";
import { Security } from "./components/Security";

type Tab = "dashboard" | "finduser" | "tenants" | "contracts" | "templates" | "deploy" | "history" | "newhire" | "offboard" | "workflows" | "security";

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
  { key: "workflows", label: "Workflows" }
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
  const signOutLocal = async () => {
    try { await api.auth.logout(); } catch { /* best-effort */ }
    clearLocalToken();
    setMe(null);
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

  return (
    <div className="app">
      <header>
        <h1>Partner Center Bridge</h1>
        <nav>
          {allTabs.map((t) => (
            <button key={t.key} className={tab === t.key ? "active" : ""} onClick={() => setTab(t.key)}>
              {t.label}
            </button>
          ))}
        </nav>
        <div className="user">
          <span>{displayName}</span>
          {authMode === "Local" && <button onClick={signOutLocal}>Sign out</button>}
          {authMode === "Oidc" && authEnabled && <button onClick={logout}>Sign out</button>}
        </div>
      </header>
      <main>
        {tab === "dashboard" && <Dashboard />}
        {tab === "finduser" && <UserSearch onLaunch={launchWorkflow} />}
        {tab === "tenants" && <Tenants me={me} />}
        {tab === "contracts" && <Contracts />}
        {tab === "templates" && <AppTemplates />}
        {tab === "deploy" && <DeployWizard />}
        {tab === "history" && <Deployments />}
        {tab === "newhire" && <NewHire />}
        {tab === "offboard" && <Offboard />}
        {tab === "workflows" && <Workflows prefill={wfLaunch} />}
        {tab === "security" && me && <Security me={me} onProfileChanged={refreshMe} />}
      </main>
    </div>
  );
}
