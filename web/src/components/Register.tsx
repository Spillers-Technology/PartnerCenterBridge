import { useState } from "react";
import { api } from "../api";
import { setLocalToken } from "../session";
import type { AuthResponse } from "../types";

export function Register({ onAuthenticated, onGoLogin }: { onAuthenticated: (r: AuthResponse) => void; onGoLogin: () => void }) {
  const [email, setEmail] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const submit = async (ev: React.FormEvent) => {
    ev.preventDefault();
    setBusy(true); setError(null);
    try {
      const r = await api.auth.register(email, password, displayName);
      setLocalToken(r.accessToken);
      onAuthenticated(r);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="center">
      <h1>Create an account</h1>
      <p className="muted">
        Registration is open -- your new account starts with no tenant access. Someone who
        already has access to a customer tenant can share it with you afterward, from Tenants.
      </p>
      <form className="field" onSubmit={submit}>
        <label>Display name</label>
        <input autoFocus value={displayName} onChange={(e) => setDisplayName(e.target.value)} />
        <label>Email</label>
        <input type="email" autoComplete="username" value={email} onChange={(e) => setEmail(e.target.value)} />
        <label>Password (12+ characters)</label>
        <input type="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} />
        <button disabled={busy}>{busy ? "Creating account…" : "Create account"}</button>
      </form>
      {error && <p className="error">{error}</p>}
      <p className="muted">
        Already registered? <button onClick={onGoLogin}>Sign in</button>
      </p>
      <p className="muted">You can add a passkey and enable two-factor authentication afterward, from Security.</p>
    </div>
  );
}
