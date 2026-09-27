import { useState } from "react";
import Alert from "@mui/material/Alert";
import Button from "@mui/material/Button";
import Card from "@mui/material/Card";
import CardContent from "@mui/material/CardContent";
import Stack from "@mui/material/Stack";
import TextField from "@mui/material/TextField";
import Typography from "@mui/material/Typography";
import { api } from "../api";
import { setLocalToken } from "../session";
import { useAsyncAction } from "../hooks/useAsyncAction";
import { useConfirm } from "../hooks/useConfirm";
import type { MeProfile } from "../types";

/**
 * For a Local Workbench used without an account: turns the built-in owner into an ordinary account
 * (email + password; passkeys and two-factor become available). The launch link stops working.
 */
export function ProtectWithAccount({ me, onProtected }: { me: MeProfile; onProtected: () => void }) {
  const confirm = useConfirm();
  const [displayName, setDisplayName] = useState(me.displayName.replace(/ \(this computer\)$/, ""));
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");

  const protect = useAsyncAction(async () => {
    const r = await api.auth.protectOwner(email.trim(), password, displayName.trim());
    setLocalToken(r.accessToken);
    setPassword("");
    onProtected();
    return true;
  });

  return (
    <Card variant="outlined" sx={{ mb: 3 }}>
      <CardContent>
        <Typography variant="h6" gutterBottom>
          Protect with an account
        </Typography>
        <Alert severity="warning" sx={{ mb: 2 }}>
          This workbench has no account: anyone who can run programs as this Windows user on this computer,
          or open its launch link, can use it with full administrator rights.
        </Alert>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
          Add an email and password to require a sign-in from now on. Launch links stop working, and passkeys
          and two-factor authentication become available. Your tenants, settings and history stay as they are.
        </Typography>
        <Stack
          component="form"
          spacing={2}
          sx={{ maxWidth: 400 }}
          onSubmit={async (ev) => {
            ev.preventDefault();
            const ok = await confirm({
              title: "Protect this workbench with an account?",
              message: `From now on, signing in needs ${email.trim() || "this email"} and the password. Launch links, including the one PartnerCenterBridge.exe opens, stop working.`,
              confirmLabel: "Protect with an account",
              mutating: true
            });
            if (ok) void protect.run();
          }}
        >
          <TextField label="Display name" value={displayName} onChange={(e) => setDisplayName(e.target.value)} />
          <TextField label="Email" type="email" autoComplete="username" value={email} onChange={(e) => setEmail(e.target.value)} />
          <TextField
            label="Password (12+ characters)"
            type="password"
            autoComplete="new-password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
          />
          <Button type="submit" variant="contained" disabled={protect.busy || !email.trim() || !password}>
            {protect.busy ? "Protecting..." : "Protect with an account"}
          </Button>
          {protect.error && <Alert severity="error">{protect.error}</Alert>}
        </Stack>
      </CardContent>
    </Card>
  );
}
