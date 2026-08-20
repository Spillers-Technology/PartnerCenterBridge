import { createTheme } from "@mui/material/styles";

export const theme = createTheme({
  palette: {
    mode: "dark",
    background: { default: "#0f172a", paper: "#1e293b" },
    divider: "#334155",
    text: { primary: "#e2e8f0", secondary: "#94a3b8" },
    primary: { light: "#a5b4fc", main: "#818cf8", dark: "#6366f1", contrastText: "#0b1020" },
    success: { main: "#4ade80" },
    warning: { main: "#fbbf24" },
    error: { main: "#f87171" }
  },
  shape: { borderRadius: 8 },
  typography: {
    fontFamily: "system-ui, sans-serif"
  }
});

export default theme;
