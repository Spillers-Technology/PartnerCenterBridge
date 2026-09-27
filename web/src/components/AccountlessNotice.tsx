import { useLocation } from "react-router";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Stack from "@mui/material/Stack";
import Typography from "@mui/material/Typography";

/**
 * Shown instead of any sign-in form when a Local Workbench is used without an account and this
 * browser has no session (a bookmark, a signed-out tab, an expired token): only the launch link that
 * PartnerCenterBridge.exe opens can sign in, so there is nothing to type here.
 */
export function AccountlessNotice({ windowsUser, launchError }: { windowsUser: string | null; launchError: string | null }) {
  const location = useLocation();
  const signedOut = Boolean((location.state as { signedOut?: boolean } | null)?.signedOut);
  return (
    <Box sx={{ display: "grid", placeItems: "center", minHeight: "100vh", p: 2 }}>
      <Stack spacing={2} sx={{ width: "100%", maxWidth: 440 }}>
        <Typography variant="h5" component="h1">
          {signedOut ? "Signed out" : "Partner Center Bridge"}
        </Typography>
        {launchError && <Alert severity="error">{launchError}</Alert>}
        <Typography variant="body1">
          This workbench has no account. Open it from PartnerCenterBridge.exe (running it again opens a signed-in window).
        </Typography>
        <Typography variant="body2" color="text.secondary">
          {windowsUser ? `It is used without an account by ${windowsUser} on this computer. ` : ""}
          If you started it with --no-browser, open the sign-in link it printed in its window.
        </Typography>
      </Stack>
    </Box>
  );
}
