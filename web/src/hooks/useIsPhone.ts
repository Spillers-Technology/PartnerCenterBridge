import { useMediaQuery, useTheme } from "@mui/material";

export function useIsPhone(): boolean {
  const theme = useTheme();
  return useMediaQuery(theme.breakpoints.down("sm"));
}
