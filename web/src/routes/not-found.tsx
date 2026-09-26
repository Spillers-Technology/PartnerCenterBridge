import { Link as RouterLink, useLocation } from "react-router";
import Box from "@mui/material/Box";
import List from "@mui/material/List";
import ListItemButton from "@mui/material/ListItemButton";
import ListItemIcon from "@mui/material/ListItemIcon";
import ListItemText from "@mui/material/ListItemText";
import Typography from "@mui/material/Typography";
import { PageHeader } from "../components/PageHeader";
import { DESTINATIONS } from "../navigation";

/** Any URL no route claims: say so plainly, then offer every real starting point. */
export default function NotFound() {
  const location = useLocation();
  return (
    <Box>
      <PageHeader title="Page not found" />
      <Typography variant="body2" color="text.secondary" sx={{ mb: 2, overflowWrap: "anywhere" }}>
        Nothing lives at <Box component="code" sx={{ fontFamily: "monospace" }}>{location.pathname}</Box>. The link may
        be mistyped or from an older version. Pick up from one of these instead:
      </Typography>
      <List aria-label="Where to go instead" sx={{ maxWidth: 560 }}>
        {DESTINATIONS.map((d) => (
          <ListItemButton key={d.key} component={RouterLink} to={d.path} sx={{ borderRadius: 1 }}>
            <ListItemIcon sx={{ minWidth: 40 }}>{d.icon}</ListItemIcon>
            <ListItemText primary={d.label} secondary={d.description} />
          </ListItemButton>
        ))}
      </List>
    </Box>
  );
}
