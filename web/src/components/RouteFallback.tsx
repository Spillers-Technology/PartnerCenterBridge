import { Component, type ErrorInfo, type ReactNode } from "react";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import LinearProgress from "@mui/material/LinearProgress";
import { visuallyHidden } from "@mui/utils";

/** Shown while a code-split area downloads. */
export function PageLoading() {
  return (
    <Box aria-busy="true" sx={{ py: 2 }}>
      <Box component="span" sx={visuallyHidden}>Loading...</Box>
      <LinearProgress />
    </Box>
  );
}

/**
 * Catches a render/chunk-load failure in one routed page so the shell (navigation, account menu)
 * stays usable. `resetKey` (the pathname) clears the error when the user navigates elsewhere.
 */
export class RouteErrorBoundary extends Component<{ resetKey: string; children: ReactNode }, { error: Error | null }> {
  state: { error: Error | null } = { error: null };

  static getDerivedStateFromError(error: Error) {
    return { error };
  }

  componentDidUpdate(prev: { resetKey: string }) {
    if (prev.resetKey !== this.props.resetKey && this.state.error) this.setState({ error: null });
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error("Page failed to render", error, info.componentStack);
  }

  render() {
    if (this.state.error) {
      return (
        <Alert
          severity="error"
          action={<Button color="inherit" size="small" onClick={() => window.location.reload()}>Reload</Button>}
        >
          This page failed to load: {this.state.error.message}. If the workbench was just updated,
          reloading picks up the new version.
        </Alert>
      );
    }
    return this.props.children;
  }
}
