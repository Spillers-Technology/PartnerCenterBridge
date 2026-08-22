import { useEffect, useRef, useState } from "react";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Stack from "@mui/material/Stack";
import TextField from "@mui/material/TextField";
import Typography from "@mui/material/Typography";
import { api } from "../api";
import { setLocalToken } from "../session";
import { conditionalMediationSupported, getPasskey, passkeysSupported, type LoginOptionsWire } from "../webauthn";
import { isMfaChallenge } from "../types";
import type { AuthResponse } from "../types";
import { useAsyncAction } from "../hooks/useAsyncAction";

type Step = "start" | "password" | "mfa";

export function Login({ onAuthenticated, onGoRegister }: { onAuthenticated: (r: AuthResponse) => void; onGoRegister: () => void }) {
  const [step, setStep] = useState<Step>("start");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [code, setCode] = useState("");
  const [mfaTicket, setMfaTicket] = useState("");
  const [lastAttempt, setLastAttempt] = useState<"passkey" | "password" | "mfa" | null>(null);

  // Guards against the conditional-autofill sign-in below and an explicit sign-in (button or
  // password form) both completing. Setting the flag before the side effects run keeps two
  // near-simultaneous completions from both landing; if a side effect itself throws (e.g. a
  // storage write failure), the flag is rolled back so a genuine later success isn't dropped.
  const finishedRef = useRef(false);
  // The conditional flow's own AbortController, reachable from the explicit passkey/password/MFA
  // actions below so any of them can cancel a still-pending conditional request before starting
  // their own -- otherwise a pending conditional get() can (a) race an explicit attempt for a
  // different outcome (e.g. conditional resolves for one credential while a password submit for a
  // different account is also in flight) and (b) on some browsers, block a subsequent explicit
  // navigator.credentials.get() call outright, since only one such call may be pending at a time.
  const conditionalAbortRef = useRef<AbortController | null>(null);

  const finish = (r: AuthResponse) => {
    if (finishedRef.current) return;
    finishedRef.current = true;
    try {
      setLocalToken(r.accessToken);
      onAuthenticated(r);
    } catch (e) {
      finishedRef.current = false;
      throw e;
    }
  };

  const passkeyAction = useAsyncAction(async () => {
    conditionalAbortRef.current?.abort();
    const { challengeKey, options } = await api.passkey.loginOptions();
    const assertionResponse = await getPasskey(options as LoginOptionsWire);
    finish(await api.passkey.loginVerify(challengeKey, assertionResponse));
  });

  // WebAuthn conditional UI: as soon as the form mounts, silently offer this browser's saved
  // passkeys through the email field's native autofill dropdown (autoComplete="webauthn" below),
  // rather than requiring the explicit "Sign in with a passkey" button first. That button (and
  // mediation: "optional" above) stays as the always-available fallback -- browsers/autofill
  // configurations that don't surface the dropdown, or a user who prefers pressing a button, both
  // still work exactly as before. Aborted on unmount, or when an explicit sign-in attempt starts
  // (see conditionalAbortRef above), so a still-pending request doesn't linger or interfere. The
  // abort signal only stops navigator.credentials.get() itself -- it can't cancel the loginVerify
  // call after a credential is already chosen -- so the signal is re-checked after every await
  // instead of trusting the AbortController alone to stop a stale completion.
  useEffect(() => {
    if (!passkeysSupported) return;
    const controller = new AbortController();
    conditionalAbortRef.current = controller;

    (async () => {
      try {
        if (!(await conditionalMediationSupported()) || controller.signal.aborted) return;
        const { challengeKey, options } = await api.passkey.loginOptions();
        if (controller.signal.aborted) return;
        const assertionResponse = await getPasskey(options as LoginOptionsWire, {
          mediation: "conditional",
          signal: controller.signal
        });
        if (controller.signal.aborted) return;
        const verified = await api.passkey.loginVerify(challengeKey, assertionResponse);
        if (controller.signal.aborted) return;
        finish(verified);
      } catch {
        // Aborted (component unmounted, or an explicit sign-in took over), no credential was
        // picked from the dropdown, or the request itself failed -- not an error to surface, the
        // explicit button and password form remain available either way.
      }
    })();

    return () => controller.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const passwordAction = useAsyncAction(async () => {
    conditionalAbortRef.current?.abort();
    const r = await api.auth.login(email, password);
    if (isMfaChallenge(r)) {
      setMfaTicket(r.mfaTicket);
      setStep("mfa");
    } else {
      finish(r);
    }
  });

  const mfaAction = useAsyncAction(async () => {
    conditionalAbortRef.current?.abort();
    finish(await api.totp.challenge(mfaTicket, code));
  });

  const busy = passkeyAction.busy || passwordAction.busy || mfaAction.busy;
  const error =
    lastAttempt === "passkey" ? passkeyAction.error :
    lastAttempt === "password" ? passwordAction.error :
    lastAttempt === "mfa" ? mfaAction.error :
    null;

  if (step === "mfa") {
    return (
      <Box sx={{ display: "grid", placeItems: "center", minHeight: "100vh", p: 2 }}>
        <Stack
          component="form"
          spacing={2}
          sx={{ width: "100%", maxWidth: 360 }}
          onSubmit={(ev) => {
            ev.preventDefault();
            setLastAttempt("mfa");
            void mfaAction.run();
          }}
        >
          <Typography variant="h5" component="h1">
            Partner Center Bridge
          </Typography>
          <TextField
            autoFocus
            label="6-digit code (or a recovery code)"
            placeholder="123456"
            value={code}
            onChange={(e) => setCode(e.target.value)}
          />
          <Button type="submit" variant="contained" disabled={busy}>
            {mfaAction.busy ? "Verifying..." : "Verify"}
          </Button>
          {error && <Alert severity="error">{error}</Alert>}
        </Stack>
      </Box>
    );
  }

  return (
    <Box sx={{ display: "grid", placeItems: "center", minHeight: "100vh", p: 2 }}>
      <Stack spacing={2} sx={{ width: "100%", maxWidth: 360 }}>
        <Typography variant="h5" component="h1">
          Partner Center Bridge
        </Typography>

        {passkeysSupported && (
          <>
            <Button variant="contained" onClick={() => { setLastAttempt("passkey"); void passkeyAction.run(); }} disabled={busy}>
              {passkeyAction.busy ? "Waiting for passkey..." : "Sign in with a passkey"}
            </Button>
            <Typography variant="body2" color="text.secondary">
              or use your password
            </Typography>
          </>
        )}

        <Stack
          component="form"
          spacing={2}
          onSubmit={(ev) => {
            ev.preventDefault();
            setLastAttempt("password");
            void passwordAction.run();
          }}
        >
          <TextField label="Email" type="email" autoComplete="username webauthn" value={email} onChange={(e) => setEmail(e.target.value)} />
          <TextField
            label="Password"
            type="password"
            autoComplete="current-password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
          />
          <Button type="submit" variant="contained" disabled={busy}>
            {passwordAction.busy ? "Signing in..." : "Sign in"}
          </Button>
        </Stack>

        <Typography variant="body2" color="text.secondary">
          No account yet? <Button size="small" onClick={onGoRegister}>Register</Button>
        </Typography>

        {error && <Alert severity="error">{error}</Alert>}
      </Stack>
    </Box>
  );
}
