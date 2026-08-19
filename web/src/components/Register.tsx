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

export function Register({ onAuthenticated, onGoLogin }: { onAuthenticated: (r: AuthResponse) => void; onGoLogin: () => void }) {
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
        <Typography variant="h5" component="h1">
          Create an account
        </Typography>
        <Typography variant="body2" color="text.secondary">
          Registration is open -- your new account starts with no tenant access. Someone who
          already has access to a customer tenant can share it with you afterward, from Tenants.
        </Typography>

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
            {registerAction.busy ? "Creating account..." : "Create account"}
          </Button>
        </Stack>

        {registerAction.error && <Alert severity="error">{registerAction.error}</Alert>}

        <Typography variant="body2" color="text.secondary">
          Already registered? <Button size="small" onClick={onGoLogin}>Sign in</Button>
        </Typography>
        <Typography variant="body2" color="text.secondary">
          You can add a passkey and enable two-factor authentication afterward, from Security.
        </Typography>
      </Stack>
    </Box>
  );
}
