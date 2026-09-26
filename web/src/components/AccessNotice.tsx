import type { ReactNode } from "react";
import Alert from "@mui/material/Alert";
import AlertTitle from "@mui/material/AlertTitle";

/**
 * Shown in place of a screen or action the caller can't use: says what is missing (a role, a
 * grant, a server feature) and how to get it -- never a blank page or an unexplained disabled
 * button.
 */
export function AccessNotice({
  title,
  children,
  severity = "info"
}: {
  title: string;
  children: ReactNode;
  severity?: "info" | "warning";
}) {
  return (
    <Alert severity={severity} variant="outlined" sx={{ mb: 2 }}>
      <AlertTitle>{title}</AlertTitle>
      {children}
    </Alert>
  );
}
