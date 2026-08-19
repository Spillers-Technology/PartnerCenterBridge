import { useState } from "react";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Stack from "@mui/material/Stack";
import TextField from "@mui/material/TextField";
import Typography from "@mui/material/Typography";
import { api } from "../api";
import { setLocalToken } from "../session";
import { getPasskey, passkeysSupported, type LoginOptionsWire } from "../webauthn";
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

  const finish = (r: AuthResponse) => {
    setLocalToken(r.accessToken);
    onAuthenticated(r);
  };

  const passkeyAction = useAsyncAction(async () => {
    const { challengeKey, options } = await api.passkey.loginOptions();
    const assertionResponse = await getPasskey(options as LoginOptionsWire);
    finish(await api.passkey.loginVerify(challengeKey, assertionResponse));
  });

  const passwordAction = useAsyncAction(async () => {
    const r = await api.auth.login(email, password);
    if (isMfaChallenge(r)) {
      setMfaTicket(r.mfaTicket);
      setStep("mfa");
    } else {
      finish(r);
    }
  });

  const mfaAction = useAsyncAction(async () => {
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
          <TextField label="Email" type="email" autoComplete="username" value={email} onChange={(e) => setEmail(e.target.value)} />
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
