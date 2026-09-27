import { useEffect, useState } from "react";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Stack from "@mui/material/Stack";
import Typography from "@mui/material/Typography";
import TextField from "@mui/material/TextField";
import Accordion from "@mui/material/Accordion";
import AccordionSummary from "@mui/material/AccordionSummary";
import AccordionDetails from "@mui/material/AccordionDetails";
import ExpandMore from "@mui/icons-material/ExpandMore";
import { api, errorText } from "../api";

export function MicrosoftConnections({ onConnected, canConnect = true }: { onConnected: () => void; canConnect?: boolean }) {
  const [status, setStatus] = useState<Awaited<ReturnType<typeof api.microsoftConnections.list>> | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [clientId, setClientId] = useState("");
  const [saving, setSaving] = useState(false);
  useEffect(() => {
    let active = true;
    const refresh = () => api.microsoftConnections?.list().then(s => { if (active) setStatus(s); }).catch(e => { if (active) setError(errorText(e)); });
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
  if (!status) return error ? <Alert severity="error">Could not load Microsoft connections: {error}</Alert> : null;
  if (!status.available) return null;
  return <Box sx={{ mb: 3, p: 3, border: "1px solid", borderColor: "divider", borderRadius: 2, bgcolor: "background.paper" }}>
    <Typography variant="h6">Microsoft 365 tenants</Typography>
    <Typography color="text.secondary" sx={{ mt: 1, mb: 2 }}>
      Connect any Microsoft 365 organization with its own admin account. No Partner Center or GDAP relationship is required.
      Microsoft opens in your browser to handle your username, password and MFA. Return here after sign-in; the workbench remembers each account and refreshes its tokens automatically.
    </Typography>
    {!status.configured && <Box sx={{ mb: 2 }}>
      <Alert severity="info" sx={{ mb: 2 }}>Microsoft sign-in is not configured in this preview build.
        Release builds should include PartnerCenterBridge's Microsoft application registration, so technicians can sign in without creating their own app.</Alert>
      {status.canConfigure !== false && <Accordion>
      <AccordionSummary expandIcon={<ExpandMore />}><Typography>Advanced: use your own Microsoft application</Typography></AccordionSummary>
      <AccordionDetails><Box component="form" onSubmit={async event => {
        event.preventDefault(); setSaving(true); setError("");
        try { await api.microsoftConnections.setup(clientId.trim()); setStatus(await api.microsoftConnections.list()); }
        catch (e) { setError(errorText(e)); }
        finally { setSaving(false); }
      }}>
        <Typography variant="subtitle2">Allow this workbench to use Microsoft sign-in</Typography>
        <Typography variant="body2" color="text.secondary" sx={{ my: 1 }}>
          Register a multitenant application in Microsoft Entra with a Mobile and desktop redirect of http://localhost.
          Add the delegated Microsoft Graph permissions your operations need, including Organization.Read.All.
          Copy its Application (client) ID below. No client secret is needed. Each tenant still controls consent and admin access.
        </Typography>
        <Button component="a" href="https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade" target="_blank" rel="noopener noreferrer" size="small">Open Microsoft app registrations</Button>
        <Button component="a" href="https://spillerstech.us/PartnerCenterBridge/local-workbench.html#direct-microsoft-sign-in" target="_blank" rel="noopener noreferrer" size="small">Setup guide</Button>
        <Stack spacing={1} sx={{ mt: 1 }}>
          <TextField label="Application (client) ID" value={clientId} onChange={event => setClientId(event.target.value)} size="small" required />
          <Box><Button type="submit" variant="outlined" disabled={saving || !clientId.trim()}>{saving ? "Saving..." : "Save sign-in setup"}</Button></Box>
        </Stack>
      </Box></AccordionDetails></Accordion>}
    </Box>}
    {error && <Alert severity="error" sx={{ mb: 2 }}>{error}</Alert>}
    {busy && <Alert severity="info" sx={{ mb: 2 }}>Complete Microsoft sign-in in your browser. This request expires after five minutes; cancelling sign-in leaves your existing connections intact.</Alert>}
    <Button variant="contained" disabled={busy || !status.configured || !canConnect} onClick={() => void connect()}>
      {busy ? "Waiting for Microsoft..." : "Add tenant with Microsoft"}
    </Button>
    {status.connections.map(connection => <Stack key={connection.id} direction={{ xs: "column", sm: "row" }} spacing={2} sx={{ mt: 2, alignItems: { sm: "center" } }}>
      <Box sx={{ flex: 1, minWidth: 0, overflowWrap: "anywhere" }}>
        <Typography sx={{ fontWeight: 600 }}>{connection.displayName}</Typography>
        <Typography>{connection.username}</Typography>
        <Typography variant="caption" color="text.secondary">{connection.tenantId}</Typography>
        {connection.reconnectRequired && <Typography color="warning.main">Microsoft needs you to sign in again.</Typography>}
      </Box>
      <Button disabled={busy || !status.configured || !canConnect} onClick={() => void connect(connection.id)}>Reconnect</Button>
    </Stack>)}
    <Typography variant="body2" color="text.secondary" sx={{ mt: 2 }}>
      Microsoft permissions and tenant consent determine which actions are available. Exchange Online still uses its certificate connection.
    </Typography>
  </Box>;
}
