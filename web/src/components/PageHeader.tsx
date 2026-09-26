import type { ReactNode } from "react";
import { Link as RouterLink } from "react-router";
import Box from "@mui/material/Box";
import Link from "@mui/material/Link";
import Stack from "@mui/material/Stack";
import Typography from "@mui/material/Typography";
import ChevronLeft from "@mui/icons-material/ChevronLeft";

/**
 * A page title with an optional "back to parent" link, one-line purpose and trailing actions.
 * The parent link is how nested pages (a tenant workspace, a single operation) show where they
 * sit in the hierarchy without a full breadcrumb trail.
 */
export function PageHeader({
  title,
  subtitle,
  parent,
  actions,
  meta
}: {
  title: ReactNode;
  subtitle?: ReactNode;
  parent?: { label: string; to: string };
  actions?: ReactNode;
  /** Chips or other small status shown next to the title. */
  meta?: ReactNode;
}) {
  return (
    <Box sx={{ mb: 2 }}>
      {parent && <BackLink {...parent} />}
      <Stack
        direction={{ xs: "column", sm: "row" }}
        spacing={1}
        sx={{ alignItems: { xs: "stretch", sm: "center" }, justifyContent: "space-between" }}
      >
        <Stack direction="row" spacing={1} useFlexGap sx={{ alignItems: "center", flexWrap: "wrap", minWidth: 0 }}>
          <Typography variant="h5" component="h2" sx={{ overflowWrap: "anywhere" }}>
            {title}
          </Typography>
          {meta}
        </Stack>
        {actions && (
          <Stack direction="row" spacing={1} useFlexGap sx={{ flexWrap: "wrap", flexShrink: 0 }}>
            {actions}
          </Stack>
        )}
      </Stack>
      {subtitle && (
        <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
          {subtitle}
        </Typography>
      )}
    </Box>
  );
}

export function BackLink({ label, to }: { label: string; to: string }) {
  return (
    <Link
      component={RouterLink}
      to={to}
      underline="hover"
      variant="body2"
      sx={{ display: "inline-flex", alignItems: "center", mb: 0.5, color: "text.secondary" }}
    >
      <ChevronLeft fontSize="small" sx={{ ml: -0.5 }} />
      {label}
    </Link>
  );
}
