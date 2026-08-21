import { useEffect, useState } from "react";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Card from "@mui/material/Card";
import CardContent from "@mui/material/CardContent";
import Dialog from "@mui/material/Dialog";
import DialogActions from "@mui/material/DialogActions";
import DialogContent from "@mui/material/DialogContent";
import DialogTitle from "@mui/material/DialogTitle";
import Skeleton from "@mui/material/Skeleton";
import Stack from "@mui/material/Stack";
import Table from "@mui/material/Table";
import TableBody from "@mui/material/TableBody";
import TableCell from "@mui/material/TableCell";
import TableContainer from "@mui/material/TableContainer";
import TableHead from "@mui/material/TableHead";
import TableRow from "@mui/material/TableRow";
import TextField from "@mui/material/TextField";
import Typography from "@mui/material/Typography";
import { visuallyHidden } from "@mui/utils";
import { api } from "../api";
import { useAsyncAction } from "../hooks/useAsyncAction";
import { useConfirm } from "../hooks/useConfirm";
import { useToast } from "../hooks/useToast";
import { createPasskey, type RegisterOptionsWire } from "../webauthn";
import type { McpTokenInfo, MeProfile, PasskeyInfo } from "../types";

type PasskeysLastAction = "addPasskey" | "removePasskey" | null;
type TotpLastAction = "startTotp" | "confirmTotp" | "disableTotp" | null;
type TokensLastAction = "createToken" | "revokeToken" | null;

export function Security({ me, onProfileChanged }: { me: MeProfile; onProfileChanged: () => void }) {
  const confirm = useConfirm();
  const toast = useToast();

  const [newTokenName, setNewTokenName] = useState("");
  const [issuedJwt, setIssuedJwt] = useState<string | null>(null);
  // Tracks which of a panel's own async actions should feed that panel's error slot -- one tracker
  // per panel (not one shared slot) so an unrelated panel's action can't knock an already-shown
  // error off screen just by starting. Mirrors Login.tsx's lastAttempt / ConfigSnapshots.tsx's
  // lastAction pattern, scoped per-panel since this screen (unlike either of those) has three
  // independent panels that can each have their own action in flight at the same time.
  const [lastPasskeysAction, setLastPasskeysAction] = useState<PasskeysLastAction>(null);
  const [lastTotpAction, setLastTotpAction] = useState<TotpLastAction>(null);
  const [lastTokensAction, setLastTokensAction] = useState<TokensLastAction>(null);

  // Passkey "add" flow: the nickname dialog opens first (replacing window.prompt) and only once the
  // user confirms it do we run the WebAuthn ceremony + server-side registration together. Asking for
  // the nickname before touching the authenticator means Cancel is a true no-op -- no credential has
  // been created on the device yet for it to orphan.
  const [passkeyDialogOpen, setPasskeyDialogOpen] = useState(false);
  const [passkeyNicknameInput, setPasskeyNicknameInput] = useState("");

  // TOTP enrollment (two-step: pending secret -> confirm code -> recovery codes shown once)
  const [pendingKey, setPendingKey] = useState<string | null>(null);
  const [secret, setSecret] = useState<string | null>(null);
  const [otpAuthUri, setOtpAuthUri] = useState<string | null>(null);
  const [enrollCode, setEnrollCode] = useState("");
  const [recoveryCodes, setRecoveryCodes] = useState<string[] | null>(null);
  const [disablePassword, setDisablePassword] = useState("");

  const passkeysAction = useAsyncAction(() => api.passkey.list());
  const mcpTokensAction = useAsyncAction(() => api.mcpTokens.list());

  useEffect(() => {
    void passkeysAction.run();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    void mcpTokensAction.run();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const passkeys = passkeysAction.result ?? [];
  const mcpTokens = mcpTokensAction.result ?? [];

  // Runs the whole "add a passkey" flow -- WebAuthn ceremony, then server-side registration -- as
  // one action, only once the user has already confirmed a nickname (see the comment above
  // passkeyDialogOpen for why the dialog comes first). The sensitive/one-shot results below
  // (this action's own success value, the TOTP secret/recovery codes, the issued JWT) are
  // deliberately never returned as this hook's `result` -- useAsyncAction.result is retained
  // in memory until the action runs again, which would keep a show-once secret inspectable
  // (e.g. via React DevTools) long after the user dismissed it on screen. Each action instead
  // writes any sensitive payload straight into its own plain useState slot and returns only a
  // boolean, so the one copy that persists is the one this component already clears on "I've
  // saved/copied it".
  const addPasskeyAction = useAsyncAction(async (nickname: string | undefined) => {
    const { challengeKey, options } = await api.passkey.registerOptions();
    const attestationResponse = await createPasskey(options as RegisterOptionsWire);
    await api.passkey.registerVerify(challengeKey, attestationResponse, nickname);
    await passkeysAction.run();
    return true;
  });

  const removePasskeyAction = useAsyncAction(async (id: string) => {
    await api.passkey.remove(id);
    await passkeysAction.run();
    return true;
  });

  const createMcpTokenAction = useAsyncAction(async (name: string) => {
    const r = await api.mcpTokens.create(name);
    // Show the issued JWT immediately -- it's already live and irretrievable from the server at
    // this point, so it must not wait behind the list refresh below.
    setIssuedJwt(r.jwt);
    setNewTokenName("");
    await mcpTokensAction.run();
    return true;
  });

  const revokeMcpTokenAction = useAsyncAction(async (id: string) => {
    await api.mcpTokens.revoke(id);
    await mcpTokensAction.run();
    return true;
  });

  const startTotpEnrollAction = useAsyncAction(async () => {
    const r = await api.totp.enroll();
    setPendingKey(r.pendingKey);
    setSecret(r.secret);
    setOtpAuthUri(r.otpAuthUri);
    return true;
  });

  const confirmTotpEnrollAction = useAsyncAction(async (key: string, code: string) => {
    const r = await api.totp.verifyEnroll(key, code);
    setRecoveryCodes(r.recoveryCodes);
    setPendingKey(null);
    setSecret(null);
    setOtpAuthUri(null);
    setEnrollCode("");
    onProfileChanged();
    return true;
  });

  const disableTotpAction = useAsyncAction(async (password: string) => {
    await api.totp.disable(password);
    onProfileChanged();
    return true;
  });

  const passkeysError =
    lastPasskeysAction === "addPasskey" ? addPasskeyAction.error :
    lastPasskeysAction === "removePasskey" ? removePasskeyAction.error :
    null;
  const totpError =
    lastTotpAction === "startTotp" ? startTotpEnrollAction.error :
    lastTotpAction === "confirmTotp" ? confirmTotpEnrollAction.error :
    lastTotpAction === "disableTotp" ? disableTotpAction.error :
    null;
  const tokensError =
    lastTokensAction === "createToken" ? createMcpTokenAction.error :
    lastTokensAction === "revokeToken" ? revokeMcpTokenAction.error :
    null;

  const handleOpenPasskeyDialog = () => {
    setPasskeyNicknameInput("");
    setPasskeyDialogOpen(true);
  };

  const handleCancelPasskeyDialog = () => {
    // Ignored while the ceremony is in flight -- see the "Add" button's disabled state below.
    if (addPasskeyAction.busy) return;
    setPasskeyDialogOpen(false);
  };

  const handleConfirmPasskeyDialog = async () => {
    setLastPasskeysAction("addPasskey");
    const nickname = passkeyNicknameInput.trim();
    const ok = await addPasskeyAction.run(nickname || undefined);
    if (ok) {
      setPasskeyDialogOpen(false);
      toast("Passkey added");
    }
  };

  const handleRemovePasskey = async (p: PasskeyInfo) => {
    const ok = await confirm({
      title: "Remove this passkey?",
      message: `Remove "${p.nickname ?? "this unnamed passkey"}"? You'll need another passkey or your password to sign in again with this device.`,
      confirmLabel: "Remove",
      destructive: true
    });
    if (!ok) return;
    setLastPasskeysAction("removePasskey");
    const removed = await removePasskeyAction.run(p.id);
    if (removed) toast("Passkey removed");
  };

  const handleCreateToken = async (ev: React.FormEvent) => {
    ev.preventDefault();
    setLastTokensAction("createToken");
    const ok = await createMcpTokenAction.run(newTokenName);
    if (ok) toast("Token created");
  };

  const handleRevokeToken = async (t: McpTokenInfo) => {
    const ok = await confirm({
      title: "Revoke this token?",
      message: `Revoke "${t.name}"? Any client currently using this token will immediately lose access.`,
      confirmLabel: "Revoke",
      destructive: true
    });
    if (!ok) return;
    setLastTokensAction("revokeToken");
    const revoked = await revokeMcpTokenAction.run(t.id);
    if (revoked) toast("Token revoked");
  };

  const handleStartTotpEnroll = async () => {
    setLastTotpAction("startTotp");
    await startTotpEnrollAction.run();
  };

  const handleConfirmTotpEnroll = async (ev: React.FormEvent) => {
    ev.preventDefault();
    if (!pendingKey) return;
    setLastTotpAction("confirmTotp");
    const ok = await confirmTotpEnrollAction.run(pendingKey, enrollCode);
    if (ok) toast("2FA enabled");
  };

  const handleDisableSubmit = async (ev: React.FormEvent) => {
    ev.preventDefault();
    const ok = await confirm({
      title: "Disable two-factor authentication?",
      message: "This lowers your account's security -- password sign-ins will no longer require a code from your authenticator app. You can re-enable it at any time.",
      confirmLabel: "Disable 2FA",
      destructive: true
    });
    if (!ok) return;
    setLastTotpAction("disableTotp");
    const disabled = await disableTotpAction.run(disablePassword);
    if (disabled) {
      setDisablePassword("");
      toast("2FA disabled");
    }
  };

  return (
    <Box>
      <Typography variant="h5" component="h2" gutterBottom>
        Security
      </Typography>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
        Signed in as {me.displayName} ({me.email}){me.isSystemAdmin && " -- system admin"}
      </Typography>

      <Card variant="outlined" sx={{ mb: 3 }}>
        <CardContent>
          <Typography variant="h6" gutterBottom>
            Passkeys
          </Typography>
          <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
            Primary sign-in method: a single tap, no password typed. Password stays as your
            permanent fallback -- it's never removable, so you can't lock yourself out.
          </Typography>

          {passkeysError && (
            <Alert severity="error" sx={{ mb: 2 }}>
              {passkeysError}
            </Alert>
          )}

          {passkeysAction.result === null ? (
            passkeysAction.status === "error" ? (
              <Alert severity="error">{passkeysAction.error}</Alert>
            ) : (
              <Box aria-busy="true">
                <Box component="span" sx={visuallyHidden}>Loading passkeys...</Box>
                <Skeleton variant="rounded" height={100} />
              </Box>
            )
          ) : (
            <TableContainer sx={{ overflowX: "auto" }}>
              <Table size="small">
                <TableHead>
                  <TableRow>
                    <TableCell>Nickname</TableCell>
                    <TableCell>Added</TableCell>
                    <TableCell>Last used</TableCell>
                    <TableCell></TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {passkeys.map((p) => (
                    <TableRow key={p.id}>
                      <TableCell>{p.nickname ?? "(unnamed)"}</TableCell>
                      <TableCell>{new Date(p.createdAt).toLocaleDateString()}</TableCell>
                      <TableCell>{p.lastUsedAt ? new Date(p.lastUsedAt).toLocaleDateString() : "never"}</TableCell>
                      <TableCell>
                        <Button
                          size="small"
                          onClick={() => void handleRemovePasskey(p)}
                          disabled={removePasskeyAction.busy || passkeysAction.busy}
                        >
                          Remove
                        </Button>
                      </TableCell>
                    </TableRow>
                  ))}
                  {passkeys.length === 0 && (
                    <TableRow>
                      <TableCell colSpan={4}>
                        <Typography variant="body2" color="text.secondary">
                          No passkeys registered yet.
                        </Typography>
                      </TableCell>
                    </TableRow>
                  )}
                </TableBody>
              </Table>
            </TableContainer>
          )}

          <Button variant="contained" sx={{ mt: 2 }} onClick={handleOpenPasskeyDialog} disabled={passkeysAction.busy}>
            Add a passkey
          </Button>
        </CardContent>
      </Card>

      <Card variant="outlined" sx={{ mb: 3 }}>
        <CardContent>
          <Typography variant="h6" gutterBottom>
            Two-factor authentication (TOTP)
          </Typography>

          {totpError && (
            <Alert severity="error" sx={{ mb: 2 }}>
              {totpError}
            </Alert>
          )}

          {recoveryCodes ? (
            <Box>
              <Typography sx={{ mb: 1 }}>
                <strong>Save these recovery codes now -- they will not be shown again.</strong>
              </Typography>
              <Typography sx={{ fontFamily: "monospace", wordBreak: "break-word", mb: 1 }}>
                {recoveryCodes.join("  ")}
              </Typography>
              <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
                Each code works once, if you lose access to your authenticator app.
              </Typography>
              <Button variant="contained" onClick={() => setRecoveryCodes(null)}>
                I've saved these codes
              </Button>
            </Box>
          ) : me.totpEnabled ? (
            <>
              <Typography sx={{ mb: 2 }}>
                2FA is enabled. Password logins require a code from your authenticator app.
              </Typography>
              <Stack component="form" spacing={2} sx={{ maxWidth: 360 }} onSubmit={(ev) => void handleDisableSubmit(ev)}>
                <TextField
                  label="Confirm your password to disable 2FA"
                  type="password"
                  value={disablePassword}
                  onChange={(e) => setDisablePassword(e.target.value)}
                />
                <Button type="submit" variant="contained" color="error" disabled={disableTotpAction.busy}>
                  {disableTotpAction.busy ? "Disabling..." : "Disable 2FA"}
                </Button>
              </Stack>
            </>
          ) : pendingKey ? (
            <Stack component="form" spacing={2} sx={{ maxWidth: 480 }} onSubmit={(ev) => void handleConfirmTotpEnroll(ev)}>
              <Typography variant="body2">
                Scan this into your authenticator app, or enter the key manually (no QR image is
                rendered client-side -- a TOTP secret is not something to hand to a third-party QR
                service):
              </Typography>
              <Typography sx={{ fontFamily: "monospace", wordBreak: "break-word" }}>{secret}</Typography>
              <Typography
                component="a"
                href={otpAuthUri ?? "#"}
                variant="body2"
                color="text.secondary"
                sx={{ wordBreak: "break-word" }}
              >
                {otpAuthUri}
              </Typography>
              <TextField
                autoFocus
                label="Enter the 6-digit code it shows"
                placeholder="123456"
                value={enrollCode}
                onChange={(e) => setEnrollCode(e.target.value)}
              />
              <Button type="submit" variant="contained" disabled={confirmTotpEnrollAction.busy}>
                {confirmTotpEnrollAction.busy ? "Confirming..." : "Confirm and enable"}
              </Button>
            </Stack>
          ) : (
            <Button variant="contained" onClick={() => void handleStartTotpEnroll()} disabled={startTotpEnrollAction.busy}>
              Enable 2FA
            </Button>
          )}
        </CardContent>
      </Card>

      <Card variant="outlined">
        <CardContent>
          <Typography variant="h6" gutterBottom>
            MCP access tokens
          </Typography>
          <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
            For headless/scripted MCP clients that can't do an interactive login. Each token has the
            same access as your account -- revoke one immediately if a client using it is
            decommissioned or compromised.
          </Typography>

          {tokensError && (
            <Alert severity="error" sx={{ mb: 2 }}>
              {tokensError}
            </Alert>
          )}

          {issuedJwt ? (
            <Box sx={{ mb: 2 }}>
              <Typography sx={{ mb: 1 }}>
                <strong>Copy this token now -- it will not be shown again.</strong>
              </Typography>
              <Typography sx={{ fontFamily: "monospace", wordBreak: "break-word", mb: 1 }}>{issuedJwt}</Typography>
              <Button variant="contained" onClick={() => setIssuedJwt(null)}>
                I've copied it
              </Button>
            </Box>
          ) : (
            <Stack component="form" spacing={2} sx={{ maxWidth: 360, mb: 2 }} onSubmit={(ev) => void handleCreateToken(ev)}>
              <TextField label={'Name this token (e.g. "Claude Desktop")'} value={newTokenName} onChange={(e) => setNewTokenName(e.target.value)} />
              <Button
                type="submit"
                variant="contained"
                disabled={createMcpTokenAction.busy || mcpTokensAction.busy || !newTokenName.trim()}
              >
                {createMcpTokenAction.busy ? "Creating..." : "Create token"}
              </Button>
            </Stack>
          )}

          {mcpTokensAction.result === null ? (
            mcpTokensAction.status === "error" ? (
              <Alert severity="error">{mcpTokensAction.error}</Alert>
            ) : (
              <Box aria-busy="true">
                <Box component="span" sx={visuallyHidden}>Loading MCP tokens...</Box>
                <Skeleton variant="rounded" height={100} />
              </Box>
            )
          ) : (
            <TableContainer sx={{ overflowX: "auto" }}>
              <Table size="small">
                <TableHead>
                  <TableRow>
                    <TableCell>Name</TableCell>
                    <TableCell>Created</TableCell>
                    <TableCell>Last used</TableCell>
                    <TableCell></TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {mcpTokens.map((t) => (
                    <TableRow key={t.id}>
                      <TableCell>{t.name}</TableCell>
                      <TableCell>{new Date(t.createdAt).toLocaleDateString()}</TableCell>
                      <TableCell>{t.lastUsedAt ? new Date(t.lastUsedAt).toLocaleDateString() : "never"}</TableCell>
                      <TableCell>
                        <Button
                          size="small"
                          onClick={() => void handleRevokeToken(t)}
                          disabled={revokeMcpTokenAction.busy || mcpTokensAction.busy}
                        >
                          Revoke
                        </Button>
                      </TableCell>
                    </TableRow>
                  ))}
                  {mcpTokens.length === 0 && (
                    <TableRow>
                      <TableCell colSpan={4}>
                        <Typography variant="body2" color="text.secondary">
                          No MCP tokens yet.
                        </Typography>
                      </TableCell>
                    </TableRow>
                  )}
                </TableBody>
              </Table>
            </TableContainer>
          )}
        </CardContent>
      </Card>

      <Dialog open={passkeyDialogOpen} onClose={handleCancelPasskeyDialog}>
        <DialogTitle>Name this passkey</DialogTitle>
        <DialogContent>
          <TextField
            autoFocus
            margin="dense"
            fullWidth
            label="Nickname"
            placeholder={'e.g. "YubiKey", "Laptop Touch ID"'}
            value={passkeyNicknameInput}
            onChange={(e) => setPasskeyNicknameInput(e.target.value)}
            disabled={addPasskeyAction.busy}
          />
        </DialogContent>
        <DialogActions>
          <Button onClick={handleCancelPasskeyDialog} disabled={addPasskeyAction.busy}>Cancel</Button>
          <Button
            variant="contained"
            onClick={() => void handleConfirmPasskeyDialog()}
            disabled={addPasskeyAction.busy || passkeysAction.busy}
          >
            {addPasskeyAction.busy ? "Waiting for device..." : "Add"}
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
}
