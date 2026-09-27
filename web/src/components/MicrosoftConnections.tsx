import { useEffect, useState } from "react";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Stack from "@mui/material/Stack";
import Typography from "@mui/material/Typography";
import { api, errorText } from "../api";

export function MicrosoftConnections({ onConnected }: { onConnected: () => void }) {
  const [status, setStatus] = useState<Awaited<ReturnType<typeof api.microsoftConnections.list>> | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  useEffect(() => {
    let active = true;
    const refresh = () => api.microsoftConnections?.list().then(s => { if (active) setStatus(s); }).catch(() => {});
    void refresh();
    const timer = window.setInterval(() => { void refresh(); }, 30000);
    return () => { active = false; window.clearInterval(timer); };
  }, []);
  async function connect(id?: string) {
    setBusy(true);
    setError("");
    try {
      await api.microsoftConnections.connect(id);
      setStatus(await api.microsoftConnections.list());
      onConnected();
    } catch (e) { setError(errorText(e)); }
    finally { setBusy(false); }
  }
  if (!status?.available) return null;
  return <Box sx={{ mb: 3, p: 3, border: "1px solid", borderColor: "divider", borderRadius: 2, bgcolor: "background.paper" }}>
    <Typography variant="h6">Connect Microsoft 365</Typography>
    <Typography color="text.secondary" sx={{ mt: 1, mb: 2 }}>
      Add each tenant with its own Microsoft admin account. Sign in in your browser; the workbench remembers the connection and refreshes tokens automatically.
    </Typography>
    {!status.configured && <Alert severity="info" sx={{ mb: 2 }}>
      Set MicrosoftSignIn:ClientId in your workbench's pcb.local.json to a multitenant public-client app registration with an http://localhost redirect and delegated Graph permissions, including Organization.Read.All. Restart the workbench after changing configuration.
    </Alert>}
    {error && <Alert severity="error" sx={{ mb: 2 }}>{error}</Alert>}
    {busy && <Alert severity="info" sx={{ mb: 2 }}>Complete Microsoft sign-in in your browser. This request expires after five minutes; cancelling sign-in leaves your existing connections intact.</Alert>}
    <Button variant="contained" disabled={busy || !status.configured} onClick={() => void connect()}>
      {busy ? "Waiting for Microsoft..." : "Add tenant with Microsoft"}
    </Button>
    {status.connections.map(connection => <Stack key={connection.id} direction={{ xs: "column", sm: "row" }} spacing={2} sx={{ mt: 2, alignItems: { sm: "center" } }}>
      <Box sx={{ flex: 1, minWidth: 0, overflowWrap: "anywhere" }}>
        <Typography sx={{ fontWeight: 600 }}>{connection.displayName}</Typography>
        <Typography>{connection.username}</Typography>
        <Typography variant="caption" color="text.secondary">{connection.tenantId}</Typography>
        {connection.reconnectRequired && <Typography color="warning.main">Microsoft needs you to sign in again.</Typography>}
      </Box>
      <Button disabled={busy || !status.configured} onClick={() => void connect(connection.id)}>Reconnect</Button>
    </Stack>)}
    <Typography variant="body2" color="text.secondary" sx={{ mt: 2 }}>
      Microsoft permissions and tenant consent determine which actions are available. Exchange Online still uses its certificate connection.
    </Typography>
  </Box>;
}
