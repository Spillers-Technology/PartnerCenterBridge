import { useEffect, useRef, useState, type ReactNode } from "react";
import { Link as RouterLink, useLocation } from "react-router";
import AppBar from "@mui/material/AppBar";
import Badge from "@mui/material/Badge";
import Box from "@mui/material/Box";
import Divider from "@mui/material/Divider";
import Drawer from "@mui/material/Drawer";
import IconButton from "@mui/material/IconButton";
import List from "@mui/material/List";
import ListItemButton from "@mui/material/ListItemButton";
import ListItemIcon from "@mui/material/ListItemIcon";
import ListItemText from "@mui/material/ListItemText";
import Menu from "@mui/material/Menu";
import MenuItem from "@mui/material/MenuItem";
import Toolbar from "@mui/material/Toolbar";
import Typography from "@mui/material/Typography";
import useMediaQuery from "@mui/material/useMediaQuery";
import { useTheme } from "@mui/material/styles";
import { visuallyHidden } from "@mui/utils";
import AccountCircle from "@mui/icons-material/AccountCircle";
import MenuIcon from "@mui/icons-material/Menu";
import { useIsPhone } from "../hooks/useIsPhone";
import { activeDestination, DESTINATIONS, type Destination, type DestinationKey } from "../navigation";

/** Full labeled sidebar width (md and up) and the compact icon rail width (sm). */
const SIDEBAR_WIDTH = 216;
const RAIL_WIDTH = 84;

function badgeText(key: DestinationKey, count: number) {
  if (key === "activity") return `${count} pending approval${count === 1 ? "" : "s"}`;
  return String(count);
}

function NavItems({
  destinations,
  active,
  badges,
  variant,
  onNavigate
}: {
  destinations: Destination[];
  active: DestinationKey | null;
  badges: Partial<Record<DestinationKey, number>>;
  variant: "full" | "rail";
  onNavigate?: () => void;
}) {
  return (
    <List component="div" sx={{ py: 1 }}>
      {destinations.map((d) => {
        const selected = d.key === active;
        const count = badges[d.key] ?? 0;
        const icon = (
          <Badge color="warning" badgeContent={count} max={99} invisible={count === 0}>
            {d.icon}
          </Badge>
        );
        const hiddenCount = count > 0 ? <Box component="span" sx={visuallyHidden}> ({badgeText(d.key, count)})</Box> : null;
        return variant === "rail" ? (
          <ListItemButton
            key={d.key}
            component={RouterLink}
            to={d.path}
            selected={selected}
            aria-current={selected ? "page" : undefined}
            onClick={onNavigate}
            sx={{
              flexDirection: "column",
              alignItems: "center",
              gap: 0.5,
              mx: 1,
              my: 0.25,
              py: 1,
              borderRadius: 2,
              minHeight: 56
            }}
          >
            {icon}
            <Typography variant="caption" sx={{ lineHeight: 1.1, fontWeight: selected ? 600 : 400 }}>
              {d.label}
              {hiddenCount}
            </Typography>
          </ListItemButton>
        ) : (
          <ListItemButton
            key={d.key}
            component={RouterLink}
            to={d.path}
            selected={selected}
            aria-current={selected ? "page" : undefined}
            onClick={onNavigate}
            sx={{ mx: 1, my: 0.25, borderRadius: 2, minHeight: 44 }}
          >
            <ListItemIcon sx={{ minWidth: 40, color: selected ? "primary.main" : "text.secondary" }}>{icon}</ListItemIcon>
            <ListItemText
              primary={<>{d.label}{hiddenCount}</>}
              slotProps={{ primary: { sx: { fontWeight: selected ? 600 : 400 } } }}
            />
          </ListItemButton>
        );
      })}
    </List>
  );
}

/**
 * The workbench frame: a top bar (brand + account) and a six-destination navigation that is a
 * labeled sidebar on desktop, a compact icon rail on tablets/unfolded foldables, and a temporary
 * drawer behind a menu button on phones. Navigation items are real links, so middle-click,
 * copy-link and keyboard activation all behave like any other link.
 */
export function AppShell({
  displayName,
  onSignOut,
  badges = {},
  children
}: {
  displayName: string | null;
  onSignOut?: () => void;
  badges?: Partial<Record<DestinationKey, number>>;
  children: ReactNode;
}) {
  const theme = useTheme();
  const isPhone = useIsPhone();
  const isWide = useMediaQuery(theme.breakpoints.up("md"));
  const location = useLocation();
  const active = activeDestination(location.pathname);
  const [drawerOpen, setDrawerOpen] = useState(false);
  const [menuAnchor, setMenuAnchor] = useState<HTMLElement | null>(null);
  const mainRef = useRef<HTMLElement>(null);
  const firstPath = useRef(location.pathname);

  useEffect(() => {
    if (!isPhone) setDrawerOpen(false);
  }, [isPhone]);

  // Move focus to the page on navigation (not on first load, and not when only the query string
  // changes -- a ?tab= switch keeps focus on the tab the user just pressed), so keyboard and
  // screen-reader users land on the new content instead of staying on the nav link.
  useEffect(() => {
    if (firstPath.current === location.pathname) return;
    firstPath.current = "";
    mainRef.current?.focus({ preventScroll: true });
    window.scrollTo?.(0, 0);
  }, [location.pathname]);

  const activeLabel = DESTINATIONS.find((d) => d.key === active)?.label;

  return (
    <Box sx={{ display: "flex", flexDirection: "column", minHeight: "100vh" }}>
      <Box
        component="a"
        href="#main-content"
        sx={{
          ...visuallyHidden,
          "&:focus": {
            position: "fixed", top: 8, left: 8, width: "auto", height: "auto", clip: "auto",
            zIndex: (t) => t.zIndex.tooltip, bgcolor: "background.paper", color: "text.primary", p: 1, borderRadius: 1
          }
        }}
      >
        Skip to content
      </Box>
      <AppBar position="sticky" color="default" enableColorOnDark elevation={0}>
        <Toolbar sx={{ px: { xs: 1, sm: 2 } }}>
          {isPhone && (
            <IconButton aria-label="Open navigation" edge="start" onClick={() => setDrawerOpen(true)} sx={{ mr: 1 }}>
              <Badge color="warning" variant="dot" invisible={!Object.values(badges).some((n) => (n ?? 0) > 0)}>
                <MenuIcon />
              </Badge>
            </IconButton>
          )}
          <Typography
            variant="h6"
            component="div"
            sx={{ flexGrow: 1, minWidth: 0, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}
          >
            {isPhone && activeLabel ? activeLabel : "Partner Center Bridge"}
          </Typography>
          <IconButton
            aria-label="Account menu"
            aria-haspopup="true"
            aria-expanded={menuAnchor !== null}
            onClick={(e) => setMenuAnchor(e.currentTarget)}
            sx={{ ml: 1 }}
          >
            <AccountCircle />
          </IconButton>
          <Menu anchorEl={menuAnchor} open={menuAnchor !== null} onClose={() => setMenuAnchor(null)}>
            <MenuItem disabled>{displayName}</MenuItem>
            <MenuItem component={RouterLink} to="/settings" onClick={() => setMenuAnchor(null)}>
              Settings
            </MenuItem>
            {onSignOut && (
              <MenuItem
                onClick={() => {
                  setMenuAnchor(null);
                  onSignOut();
                }}
              >
                Sign out
              </MenuItem>
            )}
          </Menu>
        </Toolbar>
      </AppBar>

      <Drawer anchor="left" open={isPhone && drawerOpen} onClose={() => setDrawerOpen(false)}>
        <Box component="nav" aria-label="Main navigation" sx={{ width: 260 }}>
          <Typography variant="subtitle2" color="text.secondary" sx={{ px: 3, pt: 2 }}>
            Partner Center Bridge
          </Typography>
          <NavItems destinations={DESTINATIONS} active={active} badges={badges} variant="full" onNavigate={() => setDrawerOpen(false)} />
        </Box>
      </Drawer>

      <Box sx={{ display: "flex", flex: 1, minWidth: 0 }}>
        {!isPhone && (
          <Box
            component="nav"
            aria-label="Main navigation"
            sx={{
              width: isWide ? SIDEBAR_WIDTH : RAIL_WIDTH,
              flex: "0 0 auto",
              borderRight: 1,
              borderColor: "divider",
              position: "sticky",
              top: 64,
              alignSelf: "flex-start",
              height: "calc(100vh - 64px)",
              overflowY: "auto"
            }}
          >
            <NavItems destinations={DESTINATIONS} active={active} badges={badges} variant={isWide ? "full" : "rail"} />
            {isWide && <Divider sx={{ mx: 2 }} />}
          </Box>
        )}
        <Box
          component="main"
          id="main-content"
          ref={mainRef}
          tabIndex={-1}
          sx={{ flex: "1 1 0", minWidth: 0, m: 0, maxWidth: "none", p: { xs: 1.5, sm: 2, md: 3 }, outline: "none" }}
        >
          <Box sx={{ maxWidth: 1100, mx: "auto", width: "100%" }}>{children}</Box>
        </Box>
      </Box>
    </Box>
  );
}
