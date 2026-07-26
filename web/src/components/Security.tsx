import { useEffect, useState } from "react";
import { api } from "../api";
import { createPasskey, type RegisterOptionsWire } from "../webauthn";
import type { MeProfile, PasskeyInfo } from "../types";

export function Security({ me, onProfileChanged }: { me: MeProfile; onProfileChanged: () => void }) {
  const [passkeys, setPasskeys] = useState<PasskeyInfo[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // TOTP enrollment (two-step: pending secret -> confirm code -> recovery codes shown once)
  const [pendingKey, setPendingKey] = useState<string | null>(null);
  const [secret, setSecret] = useState<string | null>(null);
  const [otpAuthUri, setOtpAuthUri] = useState<string | null>(null);
  const [enrollCode, setEnrollCode] = useState("");
  const [recoveryCodes, setRecoveryCodes] = useState<string[] | null>(null);
  const [disablePassword, setDisablePassword] = useState("");

  const load = () => api.passkey.list().then(setPasskeys).catch((e) => setError(String(e)));
  useEffect(() => { load(); }, []);

  const startTotpEnroll = async () => {
    setError(null);
    const r = await api.totp.enroll();
    setPendingKey(r.pendingKey);
    setSecret(r.secret);
    setOtpAuthUri(r.otpAuthUri);
  };

  const confirmTotpEnroll = async (ev: React.FormEvent) => {
    ev.preventDefault();
    if (!pendingKey) return;
    setBusy(true); setError(null);
    try {
      const r = await api.totp.verifyEnroll(pendingKey, enrollCode);
      setRecoveryCodes(r.recoveryCodes);
      setPendingKey(null); setSecret(null); setOtpAuthUri(null); setEnrollCode("");
      onProfileChanged();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  const disableTotp = async (ev: React.FormEvent) => {
    ev.preventDefault();
    setBusy(true); setError(null);
    try {
      await api.totp.disable(disablePassword);
      setDisablePassword("");
      onProfileChanged();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  const addPasskey = async () => {
    setBusy(true); setError(null);
    try {
      const { challengeKey, options } = await api.passkey.registerOptions();
      const attestationResponse = await createPasskey(options as RegisterOptionsWire);
      const nickname = window.prompt("Name this passkey (e.g. \"YubiKey\", \"Laptop Touch ID\")") ?? undefined;
      await api.passkey.registerVerify(challengeKey, attestationResponse, nickname);
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  const removePasskey = async (id: string) => {
    await api.passkey.remove(id);
    await load();
  };

  return (
    <section>
      <h2>Security</h2>
      <p className="muted">Signed in as {me.displayName} ({me.email}){me.isSystemAdmin && " -- system admin"}</p>
      {error && <p className="error">{error}</p>}

      <fieldset>
        <legend>Passkeys</legend>
        <p className="muted">
          Primary sign-in method: a single tap, no password typed. Password stays as your
          permanent fallback -- it's never removable, so you can't lock yourself out.
        </p>
        <table>
          <thead><tr><th>Nickname</th><th>Added</th><th>Last used</th><th></th></tr></thead>
          <tbody>
            {passkeys.map((p) => (
              <tr key={p.id}>
                <td>{p.nickname ?? "(unnamed)"}</td>
                <td>{new Date(p.createdAt).toLocaleDateString()}</td>
                <td>{p.lastUsedAt ? new Date(p.lastUsedAt).toLocaleDateString() : "never"}</td>
                <td><button onClick={() => removePasskey(p.id)}>Remove</button></td>
              </tr>
            ))}
            {passkeys.length === 0 && <tr><td colSpan={4} className="muted">No passkeys registered yet.</td></tr>}
          </tbody>
        </table>
        <button onClick={addPasskey} disabled={busy}>{busy ? "Waiting for device…" : "Add a passkey"}</button>
      </fieldset>

      <fieldset>
        <legend>Two-factor authentication (TOTP)</legend>
        {me.totpEnabled ? (
          <>
            <p>2FA is enabled. Password logins require a code from your authenticator app.</p>
            <form className="field" onSubmit={disableTotp}>
              <label>Confirm your password to disable 2FA</label>
              <input type="password" value={disablePassword} onChange={(e) => setDisablePassword(e.target.value)} />
              <button disabled={busy}>{busy ? "Disabling…" : "Disable 2FA"}</button>
            </form>
          </>
        ) : recoveryCodes ? (
          <div className="password">
            <p><strong>Save these recovery codes now -- they will not be shown again.</strong></p>
            <p className="mono">{recoveryCodes.join("  ")}</p>
            <p className="muted">Each code works once, if you lose access to your authenticator app.</p>
            <button onClick={() => setRecoveryCodes(null)}>I've saved these codes</button>
          </div>
        ) : pendingKey ? (
          <form className="field" onSubmit={confirmTotpEnroll}>
            <p>Scan this into your authenticator app, or enter the key manually (no QR image is
              rendered client-side -- a TOTP secret is not something to hand to a third-party QR
              service):</p>
            <p className="mono">{secret}</p>
            <p className="muted"><a href={otpAuthUri ?? "#"}>{otpAuthUri}</a></p>
            <label>Enter the 6-digit code it shows</label>
            <input autoFocus value={enrollCode} onChange={(e) => setEnrollCode(e.target.value)} placeholder="123456" />
            <button disabled={busy}>{busy ? "Confirming…" : "Confirm and enable"}</button>
          </form>
        ) : (
          <button onClick={startTotpEnroll}>Enable 2FA</button>
        )}
      </fieldset>
    </section>
  );
}
