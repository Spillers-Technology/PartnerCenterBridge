import { useEffect, useState } from "react";
import { authEnabled, initAuth, login, logout } from "./auth";
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

type Tab = "dashboard" | "finduser" | "tenants" | "contracts" | "templates" | "deploy" | "history" | "newhire" | "offboard" | "workflows";

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

  const launchWorkflow = (l: WorkflowLaunch) => { setWfLaunch(l); setTab("workflows"); };

  useEffect(() => {
    initAuth()
      .then((u) => setUser(u?.profile?.preferred_username ?? (authEnabled ? null : "local")))
      .finally(() => setReady(true));
  }, []);

  if (!ready) return <div className="center">Loading…</div>;

  if (authEnabled && !user) {
    return (
      <div className="center">
        <h1>Partner Center Bridge</h1>
        <button onClick={login}>Sign in</button>
      </div>
    );
  }

  return (
    <div className="app">
      <header>
        <h1>Partner Center Bridge</h1>
        <nav>
          {TABS.map((t) => (
            <button key={t.key} className={tab === t.key ? "active" : ""} onClick={() => setTab(t.key)}>
              {t.label}
            </button>
          ))}
        </nav>
        <div className="user">
          <span>{user}</span>
          {authEnabled && <button onClick={logout}>Sign out</button>}
        </div>
      </header>
      <main>
        {tab === "dashboard" && <Dashboard />}
        {tab === "finduser" && <UserSearch onLaunch={launchWorkflow} />}
        {tab === "tenants" && <Tenants />}
        {tab === "contracts" && <Contracts />}
        {tab === "templates" && <AppTemplates />}
        {tab === "deploy" && <DeployWizard />}
        {tab === "history" && <Deployments />}
        {tab === "newhire" && <NewHire />}
        {tab === "offboard" && <Offboard />}
        {tab === "workflows" && <Workflows prefill={wfLaunch} />}
      </main>
    </div>
  );
}
