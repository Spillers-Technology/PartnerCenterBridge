import { useState } from "react";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Stack from "@mui/material/Stack";
import TextField from "@mui/material/TextField";
import Typography from "@mui/material/Typography";
import { api } from "../api";
import { setLocalToken } from "../session";
import type { AuthResponse } from "../types";
import { useAsyncAction } from "../hooks/useAsyncAction";
import { useConfirm } from "../hooks/useConfirm";
import Divider from "@mui/material/Divider";

export function Register({
  onAuthenticated,
  onGoLogin,
  setup = false,
  skipAccount = null
}: {
  onAuthenticated: (r: AuthResponse) => void;
  onGoLogin: () => void;
  /** First run: nobody has registered yet, so this account becomes the instance Administrator. */
  setup?: boolean;
  /**
   * First run of a Local Workbench that may be used without an account: offers "Skip -- use without
   * an account on this computer" as a clearly secondary, confirmed choice. Null hides it.
   */
  skipAccount?: { windowsUser: string } | null;
}) {
  const [email, setEmail] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [password, setPassword] = useState("");

  const registerAction = useAsyncAction(async () => {
    const r = await api.auth.register(email, password, displayName);
    setLocalToken(r.accessToken);
    onAuthenticated(r);
  });

  return (
    <Box sx={{ display: "grid", placeItems: "center", minHeight: "100vh", p: 2 }}>
      <Stack spacing={2} sx={{ width: "100%", maxWidth: 400 }}>
        {setup ? (
          <>
            <Typography variant="h5" component="h1">
              Set up this workbench
            </Typography>
            <Typography variant="body2" color="text.secondary">
              Nobody has signed in here yet. Create the first account: it becomes this
              workbench's Administrator, and Home will then walk you through connecting
              Microsoft and adding tenants.
            </Typography>
          </>
        ) : (
          <>
            <Typography variant="h5" component="h1">
              Create an account
            </Typography>
            <Typography variant="body2" color="text.secondary">
              Registration is open; your new account starts with no tenant access. Someone who
              already has access to a customer tenant can share it with you afterward, from Tenants.
            </Typography>
          </>
        )}

        <Stack
          component="form"
          spacing={2}
          onSubmit={(ev) => {
            ev.preventDefault();
            void registerAction.run();
          }}
        >
          <TextField autoFocus label="Display name" value={displayName} onChange={(e) => setDisplayName(e.target.value)} />
          <TextField label="Email" type="email" autoComplete="username" value={email} onChange={(e) => setEmail(e.target.value)} />
          <TextField
            label="Password (12+ characters)"
            type="password"
            autoComplete="new-password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
          />
          <Button type="submit" variant="contained" disabled={registerAction.busy}>
            {registerAction.busy ? "Creating account..." : setup ? "Create administrator account" : "Create account"}
          </Button>
        </Stack>

        {registerAction.error && <Alert severity="error">{registerAction.error}</Alert>}

        {setup && skipAccount && <SkipAccountOption windowsUser={skipAccount.windowsUser} onAuthenticated={onAuthenticated} />}

        {!setup && (
          <Typography variant="body2" color="text.secondary">
            Already registered? <Button size="small" onClick={onGoLogin}>Sign in</Button>
          </Typography>
        )}
        <Typography variant="body2" color="text.secondary">
          You can add a passkey and enable two-factor authentication afterward, from Settings.
        </Typography>
      </Stack>
    </Box>
  );
}

/** The confirmation text for using the workbench without an account. */
export function skipAccountWarning(windowsUser: string): string {
  return (
    `Anyone who can run programs as ${windowsUser} on this computer, or open the launch link, can use ` +
    "Partner Center Bridge with full administrator rights. Choose this only if this computer is already " +
    "secured (screen lock, disk encryption, no shared Windows account). You can add a password later in Settings."
  );
}

function SkipAccountOption({ windowsUser, onAuthenticated }: { windowsUser: string; onAuthenticated: (r: AuthResponse) => void }) {
  const confirm = useConfirm();
  const skipAction = useAsyncAction(async () => {
    const r = await api.auth.setupNoAccount();
    setLocalToken(r.accessToken);
    onAuthenticated(r);
  });

  return (
    <Stack spacing={1}>
      <Divider>or</Divider>
      <Box>
        <Button
          variant="text"
          color="inherit"
          size="small"
          disabled={skipAction.busy}
          onClick={async () => {
            const ok = await confirm({
              title: "Use without an account?",
              message: skipAccountWarning(windowsUser),
              confirmLabel: "Use without an account",
              destructive: true
            });
            if (ok) void skipAction.run();
          }}
        >
          {skipAction.busy ? "Setting up..." : "Skip -- use without an account on this computer"}
        </Button>
      </Box>
      <Typography variant="caption" color="text.secondary">
        For a computer only you use: PartnerCenterBridge.exe then opens the workbench already signed in as {windowsUser}.
      </Typography>
      {skipAction.error && <Alert severity="error">{skipAction.error}</Alert>}
    </Stack>
  );
}
