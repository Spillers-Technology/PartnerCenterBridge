import { useCallback, useEffect, useState, type ReactNode } from "react";
import { Link as RouterLink } from "react-router";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Card from "@mui/material/Card";
import CardActionArea from "@mui/material/CardActionArea";
import CardContent from "@mui/material/CardContent";
import Chip from "@mui/material/Chip";
import Skeleton from "@mui/material/Skeleton";
import Stack from "@mui/material/Stack";
import TextField from "@mui/material/TextField";
import Typography from "@mui/material/Typography";
import LockOutlined from "@mui/icons-material/LockOutlined";
import CloudOutlined from "@mui/icons-material/CloudOutlined";
import MonitorHeartOutlined from "@mui/icons-material/MonitorHeartOutlined";
import { api, isApiStatus } from "../api";
import { AccessNotice } from "../components/AccessNotice";
import { DiagnosticsList } from "../components/DiagnosticsList";
import { BackLink, PageHeader } from "../components/PageHeader";
import { QuestChip } from "../components/QuestChip";
import { Security } from "../components/Security";
import { useAsyncAction } from "../hooks/useAsyncAction";
import { useConfirm } from "../hooks/useConfirm";
import { useDiagnostics } from "../hooks/useDiagnostics";
import { useToast } from "../hooks/useToast";
import { hasInstancePermission } from "../permissions";
import type { SamStatus } from "../types";
import { useWorkbench } from "../workbench";

const BACK = { label: "Settings", to: "/settings" };

function SettingsCard({ to, icon, title, detail }: { to: string; icon: ReactNode; title: string; detail: string }) {
  return (
    <Card variant="outlined" sx={{ minWidth: 0 }}>
      <CardActionArea component={RouterLink} to={to} sx={{ height: "100%" }}>
        <CardContent sx={{ display: "flex", gap: 1.5, alignItems: "flex-start" }}>
          <Box sx={{ color: "primary.main", display: "flex", pt: 0.25 }}>{icon}</Box>
          <Box sx={{ minWidth: 0 }}>
            <Typography variant="subtitle1" component="span" sx={{ display: "block", fontWeight: 600 }}>{title}</Typography>
            <Typography variant="body2" color="text.secondary">{detail}</Typography>
          </Box>
        </CardContent>
      </CardActionArea>
    </Card>
  );
}

export function SettingsOverview() {
  const { status, authMode } = useWorkbench();
  return (
    <Box>
      <PageHeader title="Settings" />
      <Box sx={{ display: "grid", gridTemplateColumns: { xs: "1fr", md: "repeat(3, minmax(0, 1fr))" }, gap: 1.5, mb: 3 }}>
        <SettingsCard to="/settings/security" icon={<LockOutlined />} title="Account & security" detail="Passkeys, two-factor, MCP tokens and who administers this workbench." />
        <SettingsCard to="/settings/microsoft" icon={<CloudOutlined />} title="Microsoft connection" detail="The Secure Application Model credential PCB uses to reach customer tenants." />
        <SettingsCard to="/settings/workbench" icon={<MonitorHeartOutlined />} title="Workbench health" detail="Database, sign-in, PowerShell and Exchange checks, with fixes." />
      </Box>
      <Typography variant="body2" color="text.secondary">
        {status
          ? `Partner Center Bridge ${status.version} - ${status.profile} profile - ${authMode} sign-in`
          : `${authMode} sign-in`}
      </Typography>
    </Box>
  );
}

export function SecuritySettingsPage() {
  const { me, authMode, refreshMe, refreshStatus } = useWorkbench();
  if (authMode !== "Local" || me === null) {
    return (
      <Box>
        <PageHeader title="Account & security" parent={BACK} />
        <AccessNotice title={authMode === "Oidc" ? "Managed by your identity provider" : "Sign-in is disabled"}>
          {authMode === "Oidc"
            ? "Passwords, MFA and passkeys for your account are managed by your organization's identity provider, not by PCB."
            : "This server runs in Dev mode with authentication disabled, so there is no account to secure. Use Auth:Mode=Local or Oidc for anything beyond local development."}
        </AccessNotice>
      </Box>
    );
  }
  return (
    <Box>
      <BackLink {...BACK} />
      <Security
        me={me}
        onProfileChanged={() => void refreshMe()}
        onAccountProtected={() => { void refreshMe(); void refreshStatus?.(); }}
      />
    </Box>
  );
}

const SAM_BOOTSTRAP_COMMAND = "dotnet run --project src/PartnerCenterBridge.Api -- bootstrap-sam";

export function MicrosoftSettingsPage() {
  const { me } = useWorkbench();
  const confirm = useConfirm();
  const toast = useToast();
  const allowed = hasInstancePermission(me, "instance.sam.manage");
  const [sam, setSam] = useState<SamStatus | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [token, setToken] = useState("");

  const load = useCallback(async () => {
    setLoadError(null);
    try {
      setSam(await api.sam.status());
    } catch (e) {
      setLoadError(isApiStatus(e, 403)
        ? "Your account can't read the Microsoft connection."
        : e instanceof Error ? e.message : String(e));
    }
  }, []);

  useEffect(() => { if (allowed) void load(); }, [allowed, load]);

  const seed = useAsyncAction(async () => {
    await api.sam.seed(token.trim());
    setToken("");
    await load();
    toast("Refresh token saved", "success");
  });

  const header = (
    <PageHeader
      title="Microsoft connection"
      parent={BACK}
      subtitle="PCB reaches every customer tenant through one Secure Application Model (SAM) refresh token."
    />
  );

  if (!allowed) {
    return (
      <Box>
        {header}
        <AccessNotice title="Needs the SAM credentials role">
          Viewing or rotating the Microsoft connection requires the instance SAM credentials role (or
          Administrator). An Administrator can grant it from Settings, Account & security, Instance access.
        </AccessNotice>
      </Box>
    );
  }

  return (
    <Box>
      {header}
      <Card variant="outlined" sx={{ mb: 2 }}>
        <CardContent>
          <Stack direction="row" spacing={1} sx={{ alignItems: "center", mb: 1 }}>
            <Typography variant="h6" component="h3">Status</Typography>
            {sam && (
              <Chip
                size="small"
                label={sam.bootstrapped ? "Connected" : "Not connected"}
                color={sam.bootstrapped ? "success" : "warning"}
              />
            )}
          </Stack>
          {loadError && <Alert severity="error">{loadError}</Alert>}
          {!loadError && sam === null && <Skeleton variant="rounded" height={40} />}
          {sam && sam.bootstrapped && (
            <Typography variant="body2" color="text.secondary">
              A refresh token is stored. PCB exchanges it for Graph and Partner Center tokens per tenant as needed.
            </Typography>
          )}
          {sam && !sam.bootstrapped && (
            <Stack spacing={1}>
              <Typography variant="body2">
                No refresh token yet, so PCB can't reach any customer tenant. Run the interactive bootstrap
                on the server (it signs in with a device code and stores the token):
              </Typography>
              <QuestChip fix={{ label: "Bootstrap SAM", command: SAM_BOOTSTRAP_COMMAND }} />
            </Stack>
          )}
        </CardContent>
      </Card>

      <Card variant="outlined">
        <CardContent>
          <Typography variant="h6" component="h3" gutterBottom>Paste a refresh token</Typography>
          <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
            For a token captured out-of-band, or to recover after rotation. Prefer the bootstrap command above.
            The token is stored encrypted and never shown again.
          </Typography>
          <Stack
            component="form"
            spacing={1}
            onSubmit={async (ev) => {
              ev.preventDefault();
              if (!token.trim()) return;
              const ok = await confirm({
                title: "Replace the SAM refresh token?",
                message: "Every tenant operation will use the new token immediately. A wrong token breaks access to all tenants until it is replaced.",
                confirmLabel: "Save token",
                destructive: true
              });
              if (ok) void seed.run();
            }}
          >
            <TextField
              label="Refresh token"
              type="password"
              autoComplete="off"
              value={token}
              onChange={(e) => setToken(e.target.value)}
              size="small"
              fullWidth
            />
            <Box>
              <Button type="submit" variant="contained" disabled={!token.trim() || seed.busy}>
                {seed.busy ? "Saving..." : "Save token"}
              </Button>
            </Box>
            {seed.error && <Alert severity="error">{seed.error}</Alert>}
          </Stack>
        </CardContent>
      </Card>
    </Box>
  );
}

function CapabilityChip({ label, on }: { label: string; on: boolean }) {
  return <Chip size="small" label={`${label}: ${on ? "available" : "unavailable"}`} color={on ? "success" : "default"} variant={on ? "filled" : "outlined"} />;
}

export function WorkbenchSettingsPage() {
  const { status } = useWorkbench();
  const diagnostics = useDiagnostics();

  return (
    <Box>
      <PageHeader
        title="Workbench health"
        parent={BACK}
        subtitle="What this workbench needs to do its job, and how to fix anything that's missing."
        actions={
          <Button size="small" onClick={() => void diagnostics.reload()} disabled={diagnostics.status === "loading"}>
            Re-run checks
          </Button>
        }
      />

      <Card variant="outlined" sx={{ mb: 2 }}>
        <CardContent>
          <Typography variant="h6" component="h3" gutterBottom>Server</Typography>
          {status ? (
            <Box sx={{ display: "grid", gridTemplateColumns: { xs: "1fr 1fr", sm: "repeat(3, minmax(0, 1fr))" }, gap: 1.5 }}>
              <Box><Typography variant="caption" color="text.secondary" component="div">Version</Typography><Typography variant="body2">{status.version}</Typography></Box>
              <Box><Typography variant="caption" color="text.secondary" component="div">Profile</Typography><Typography variant="body2">{status.profile}</Typography></Box>
              <Box><Typography variant="caption" color="text.secondary" component="div">Sign-in</Typography><Typography variant="body2">{status.authMode}</Typography></Box>
            </Box>
          ) : (
            <Typography variant="body2" color="text.secondary">
              This server doesn't report its status (it predates 0.9.0).
            </Typography>
          )}
        </CardContent>
      </Card>

      <Card variant="outlined">
        <CardContent>
          <Typography variant="h6" component="h3" gutterBottom>Checks</Typography>
          {diagnostics.status === "loading" && <Skeleton variant="rounded" height={160} />}
          {diagnostics.status === "unavailable" && (
            <Alert severity="info" variant="outlined">
              Diagnostics are unavailable on this server version. Upgrade the server to 0.9.0 or later to see
              setup checks and fixes here.
            </Alert>
          )}
          {diagnostics.status === "error" && (
            <Alert
              severity="error"
              action={<Button color="inherit" size="small" onClick={() => void diagnostics.reload()}>Retry</Button>}
            >
              Couldn't run diagnostics: {diagnostics.error}
            </Alert>
          )}
          {diagnostics.status === "ready" && (
            <>
              <Stack direction="row" spacing={1} useFlexGap sx={{ flexWrap: "wrap", mb: 2 }}>
                <CapabilityChip label="Microsoft Graph" on={diagnostics.data.capabilities.graph} />
                <CapabilityChip label="Exchange Online" on={diagnostics.data.capabilities.exchange} />
                <CapabilityChip label="Partner Center" on={diagnostics.data.capabilities.partnerCenter} />
              </Stack>
              {(diagnostics.data.checks ?? []).length > 0 ? (
                <DiagnosticsList checks={diagnostics.data.checks!} onChanged={diagnostics.reload} />
              ) : (
                <Typography variant="body2" color="text.secondary">
                  Detailed checks are visible to instance Administrators.
                </Typography>
              )}
            </>
          )}
        </CardContent>
      </Card>
    </Box>
  );
}
