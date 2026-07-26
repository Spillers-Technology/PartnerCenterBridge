import { useState } from "react";
import { api } from "../api";
import { setLocalToken } from "../session";
import { getPasskey, passkeysSupported, type LoginOptionsWire } from "../webauthn";
import { isMfaChallenge } from "../types";
import type { AuthResponse } from "../types";

type Step = "start" | "password" | "mfa";

export function Login({ onAuthenticated, onGoRegister }: { onAuthenticated: (r: AuthResponse) => void; onGoRegister: () => void }) {
  const [step, setStep] = useState<Step>("start");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [code, setCode] = useState("");
  const [mfaTicket, setMfaTicket] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const finish = (r: AuthResponse) => {
    setLocalToken(r.accessToken);
    onAuthenticated(r);
  };

  const withPasskey = async () => {
    setBusy(true); setError(null);
    try {
      const { challengeKey, options } = await api.passkey.loginOptions();
      const assertionResponse = await getPasskey(options as LoginOptionsWire);
      const r = await api.passkey.loginVerify(challengeKey, assertionResponse);
      finish(r);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  const submitPassword = async (ev: React.FormEvent) => {
    ev.preventDefault();
    setBusy(true); setError(null);
    try {
      const r = await api.auth.login(email, password);
      if (isMfaChallenge(r)) {
        setMfaTicket(r.mfaTicket);
        setStep("mfa");
      } else {
        finish(r);
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  const submitMfa = async (ev: React.FormEvent) => {
    ev.preventDefault();
    setBusy(true); setError(null);
    try {
      finish(await api.totp.challenge(mfaTicket, code));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="center">
      <h1>Partner Center Bridge</h1>

      {step === "mfa" ? (
        <form className="field" onSubmit={submitMfa}>
          <label>Enter the 6-digit code from your authenticator app (or a recovery code)</label>
          <input autoFocus value={code} onChange={(e) => setCode(e.target.value)} placeholder="123456" />
          <button disabled={busy}>{busy ? "Verifying…" : "Verify"}</button>
        </form>
      ) : (
        <>
          {passkeysSupported && (
            <>
              <button onClick={withPasskey} disabled={busy}>{busy ? "Waiting for passkey…" : "Sign in with a passkey"}</button>
              <p className="muted">or use your password</p>
            </>
          )}
          <form className="field" onSubmit={submitPassword}>
            <label>Email</label>
            <input type="email" autoComplete="username" value={email} onChange={(e) => setEmail(e.target.value)} />
            <label>Password</label>
            <input type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} />
            <button disabled={busy}>{busy ? "Signing in…" : "Sign in"}</button>
          </form>
          <p className="muted">
            No account yet? <button onClick={onGoRegister}>Register</button>
          </p>
        </>
      )}
      {error && <p className="error">{error}</p>}
    </div>
  );
}
